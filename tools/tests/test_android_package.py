import contextlib
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile


spec = importlib.util.spec_from_file_location(
    "android_package", Path(__file__).resolve().parents[1] / "Verify-AndroidPackage.py"
)
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class AndroidPackageTests(unittest.TestCase):
    def check_package(self, *, abi="x86_64", store=None, extra=None,
                      version="1.2.0", debug=False, manifest=None):
        if store is None:
            store = b"ProxyNetworker.App.Android\0ProxyNetworker.Client.Runtime"
        if manifest is None:
            manifest = (
                "io.github.jkh404.proxynetworker.NetworkClientService "
                "io.github.jkh404.proxynetworker.NetworkVpnService "
                "android.permission.BIND_VPN_SERVICE"
            )
        badging = (
            f"package: name='io.github.jkh404.proxynetworker' versionName='{version}'"
            + ("\napplication-debuggable" if debug else "")
        )
        with tempfile.TemporaryDirectory() as directory:
            with zipfile.ZipFile(Path(directory) / "client-Signed.apk", "w") as archive:
                archive.writestr(f"lib/{abi}/libassembly-store.so", store)
                if extra:
                    archive.writestr(extra, "")
            with patch.object(package.subprocess, "check_output", side_effect=[badging, manifest]):
                with contextlib.redirect_stdout(io.StringIO()):
                    package.verify(Path(directory), "android-x64", "1.2.0")

    def test_release_package(self):
        self.check_package()

    def test_wrong_abi(self):
        with self.assertRaisesRegex(ValueError, "ABIs"):
            self.check_package(abi="arm64-v8a")

    def test_embedded_assemblies_are_required(self):
        with self.assertRaisesRegex(ValueError, "embed"):
            self.check_package(store=b"missing managed app")

    def test_debug_instrumentation_in_store(self):
        with self.assertRaisesRegex(ValueError, "Spyglass"):
            self.check_package(store=b"ProxyNetworker.App.Android ProxyNetworker.Client.Runtime TKCode.Spyglass.Agent")

    def test_private_settings(self):
        with self.assertRaisesRegex(ValueError, "Forbidden"):
            self.check_package(extra="assets/appsettings.Local.json")

    def test_debuggable_apk(self):
        with self.assertRaisesRegex(ValueError, "debuggable"):
            self.check_package(debug=True)

    def test_version_mismatch(self):
        with self.assertRaisesRegex(ValueError, "version"):
            self.check_package(version="0.1.0")

    def test_both_services_are_required(self):
        with self.assertRaisesRegex(ValueError, "Missing service"):
            self.check_package(manifest="io.github.jkh404.proxynetworker.NetworkVpnService")


if __name__ == "__main__":
    unittest.main()
