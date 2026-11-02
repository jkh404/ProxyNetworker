import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


manager = load("offload_manager", "Manage-LinuxUdpOffload.py")
diagnostic = load("offload_diagnostic", "Diagnose-LinuxTransfer.py") if os.name == "posix" else None


class TestControllerArguments(unittest.TestCase):
    def test_requires_explicit_client_directory(self):
        with tempfile.TemporaryDirectory() as workspace:
            result = subprocess.run(
                [sys.executable, str(Path(__file__).with_name("Control-LinuxOffloadTest.py")),
                 "status", "--workspace", workspace],
                capture_output=True, text=True, timeout=10,
            )
        self.assertEqual(2, result.returncode)
        self.assertIn("--client-directory", result.stderr)


class OffloadManagerTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "state.json"
        self.config = {"interfaces": [{"name": "eth1", "peer_address": "10.20.0.11"}]}
        self.state = {"interfaces": {"eth1": {
            "mac": "12:34:56:78:90:ab", "driver": "virtio_net",
            "original": {"active": False, "requested": False}, "managed": False,
        }}}
        self.active = False
        self.fixed = False
        self.checksum = True
        self.mac = self.state["interfaces"]["eth1"]["mac"]
        self.route = "eth1"
        self.changes = []
        self.addCleanup(patch.stopall)
        patch.object(manager, "inspect_interface", side_effect=self.inspect).start()
        patch.object(manager, "set_feature", side_effect=self.change).start()
        patch.object(manager, "run", side_effect=lambda _: json.dumps([{"dev": self.route}])).start()

    def inspect(self, _):
        return {
            "mac": self.mac, "driver": "virtio_net", "checksum": self.checksum,
            "feature": {"active": self.active, "requested": self.active, "fixed": self.fixed},
        }

    def change(self, name, value):
        self.assertTrue(json.loads(self.path.read_text())["interfaces"][name]["managed"])
        self.changes.append(value)
        self.active = value

    def apply(self):
        return manager.apply(self.config, self.state, self.path)

    def test_enable_and_restore_preserve_original_state(self):
        self.assertEqual("enabled", self.apply()[0]["result"])
        self.assertTrue(self.active)
        self.assertEqual("restored", manager.restore(self.state, self.path)[0]["result"])
        self.assertFalse(self.active)
        self.assertEqual([True, False], self.changes)
        self.assertFalse(self.state["interfaces"]["eth1"]["managed"])

    def test_repeated_apply_does_not_overwrite_backup(self):
        self.apply()
        self.assertEqual("already-on", self.apply()[0]["result"])
        self.assertFalse(self.state["interfaces"]["eth1"]["original"]["active"])
        self.assertEqual([True], self.changes)

    def test_external_enable_is_not_owned_or_disabled(self):
        self.active = True
        self.assertEqual("already-on", self.apply()[0]["result"])
        self.assertEqual("not-managed", manager.restore(self.state, self.path)[0]["result"])
        self.assertTrue(self.active)
        self.assertEqual([], self.changes)

    def test_original_enabled_state_is_preserved(self):
        self.active = True
        self.state["interfaces"]["eth1"]["original"] = {"active": True, "requested": True}
        self.apply()
        manager.restore(self.state, self.path)
        self.assertTrue(self.active)
        self.assertEqual([], self.changes)

    def test_unsupported_feature_uses_software_without_changes(self):
        self.fixed = True
        self.assertEqual("software-fallback", self.apply()[0]["result"])
        self.assertEqual([], self.changes)

    def test_disabled_checksum_uses_software_without_changes(self):
        self.checksum = False
        self.assertEqual("software-fallback", self.apply()[0]["result"])
        self.assertEqual([], self.changes)

    def test_changed_route_does_not_modify_another_interface(self):
        self.route = "eth0"
        self.assertEqual("software-fallback", self.apply()[0]["result"])
        self.assertEqual([], self.changes)

    def test_route_change_restores_previously_managed_interface(self):
        self.apply()
        self.route = "eth0"
        self.assertEqual("software-fallback", self.apply()[0]["result"])
        self.assertFalse(self.active)

    def test_changed_identity_is_never_modified(self):
        self.mac = "aa:bb:cc:dd:ee:ff"
        self.assertEqual("software-fallback", self.apply()[0]["result"])
        self.assertEqual([], self.changes)

    def test_identity_change_during_rollback_keeps_recovery_state(self):
        self.apply()
        self.mac = "aa:bb:cc:dd:ee:ff"
        with self.assertRaisesRegex(RuntimeError, "Rollback incomplete"):
            manager.restore(self.state, self.path)
        self.assertTrue(self.state["interfaces"]["eth1"]["managed"])
        self.assertEqual([True], self.changes)

    def test_failed_activation_restores_even_if_command_changed_state(self):
        def partial_failure(name, value):
            self.change(name, value)
            if value:
                raise subprocess.CalledProcessError(1, ["ethtool"])
        with patch.object(manager, "set_feature", side_effect=partial_failure):
            self.assertEqual("software-fallback", self.apply()[0]["result"])
        self.assertFalse(self.active)
        self.assertEqual([True, False], self.changes)

    def test_failed_restore_retains_ownership_for_retry(self):
        self.apply()
        with patch.object(manager, "set_feature", side_effect=OSError("test failure")):
            with self.assertRaisesRegex(RuntimeError, "Rollback incomplete"):
                manager.restore(self.state, self.path)
        self.assertTrue(json.loads(self.path.read_text())["interfaces"]["eth1"]["managed"])
        manager.restore(self.state, self.path)
        self.assertFalse(self.active)

    def test_restore_is_idempotent(self):
        self.apply()
        manager.restore(self.state, self.path)
        self.assertEqual("not-managed", manager.restore(self.state, self.path)[0]["result"])
        self.assertEqual([True, False], self.changes)

    def install_paths(self):
        root = Path(self.directory.name)
        for key, name in (("CONFIG", "config.json"), ("STATE", "state.json"),
                          ("PROGRAM", "bin/manager.py"), ("UNIT", "manager.service")):
            patch.object(manager, key, root / name).start()

    def test_install_records_state_without_enabling_or_changing_nic(self):
        self.install_paths()
        result = manager.install("eth1", "10.20.0.11")
        self.assertTrue(result["installed"])
        self.assertFalse(result["enabled"])
        self.assertEqual([], self.changes)
        self.assertIn("ExecStop=", manager.UNIT.read_text())
        self.assertFalse(json.loads(manager.STATE.read_text())["interfaces"]["eth1"]["managed"])

    def test_install_refuses_to_overwrite_recovery_files(self):
        self.install_paths()
        manager.STATE.write_text("existing backup")
        with self.assertRaisesRegex(RuntimeError, "Refusing to overwrite"):
            manager.install("eth1", "10.20.0.11")
        self.assertEqual("existing backup", manager.STATE.read_text())
        self.assertFalse(manager.CONFIG.exists())

    def test_install_rejects_public_peer(self):
        self.install_paths()
        with self.assertRaisesRegex(RuntimeError, "private peer"):
            manager.install("eth1", "8.8.8.8")
        self.assertFalse(manager.STATE.exists())

    def test_install_failure_removes_only_new_installation_files(self):
        self.install_paths()
        sentinel = Path(self.directory.name) / "unrelated"
        sentinel.write_text("keep")
        def commands(command):
            if command[0] == "systemctl":
                raise subprocess.CalledProcessError(1, command)
            return json.dumps([{"dev": "eth1"}])
        with patch.object(manager, "run", side_effect=commands):
            with self.assertRaises(subprocess.CalledProcessError):
                manager.install("eth1", "10.20.0.11")
        self.assertFalse(manager.STATE.exists())
        self.assertFalse(manager.UNIT.exists())
        self.assertEqual("keep", sentinel.read_text())


@unittest.skipIf(diagnostic is None, "Requires Linux diagnostic imports")
class DiagnosticTests(unittest.TestCase):
    def test_tcp_info_decodes_kernel_uapi_offsets(self):
        data = bytearray(248)
        for offset, value in ((24, 0), (100, 19), (136, 2000), (156, 1800), (220, 4)):
            diagnostic.struct.pack_into("=I", data, offset, value)
        value = diagnostic.decode_tcp_info(data.hex())
        self.assertEqual(19, value["total_retrans"])
        self.assertEqual(1800, value["data_segs_out"])
        self.assertEqual(4, value["reord_seen"])

    def test_short_tcp_info_does_not_invent_new_fields(self):
        self.assertEqual({}, diagnostic.decode_tcp_info(""))
        self.assertNotIn("reord_seen", diagnostic.decode_tcp_info(bytes(104).hex()))

    def test_ping_summary_contains_loss_and_latency(self):
        output = "time=0.5 ms\ntime=2.5 ms\n3 packets transmitted, 2 received, 33% packet loss"
        result = diagnostic.ping_summary(output)
        self.assertEqual(3, result["transmitted"])
        self.assertEqual(2, result["received"])
        self.assertEqual(1.5, result["median_ms"])
        self.assertEqual(2.5, result["p95_ms"])


if __name__ == "__main__":
    unittest.main()
