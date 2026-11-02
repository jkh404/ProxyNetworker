import argparse
import contextlib
import json
import os
import shutil
import subprocess
import tempfile
from pathlib import Path


FEATURE = "tx-udp-segmentation"
CONFIG = Path("/etc/proxynetworker/udp-offload.json")
STATE = Path("/var/lib/proxynetworker/udp-offload-state.json")
PROGRAM = Path("/usr/local/libexec/proxynetworker/Manage-LinuxUdpOffload.py")
UNIT = Path("/etc/systemd/system/proxynetworker-udp-offload.service")
SERVICE = """[Unit]
Description=ProxyNetworker private-interface UDP segmentation
Wants=network-online.target
After=network-online.target

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/usr/bin/python3 /usr/local/libexec/proxynetworker/Manage-LinuxUdpOffload.py apply
ExecStop=/usr/bin/python3 /usr/local/libexec/proxynetworker/Manage-LinuxUdpOffload.py restore
TimeoutStartSec=45
TimeoutStopSec=45

[Install]
WantedBy=multi-user.target
"""


def run(command):
    return subprocess.run(command, check=True, capture_output=True, text=True, timeout=10).stdout


def atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", dir=path.parent, delete=False) as stream:
            temporary = Path(stream.name)
            json.dump(value, stream, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, 0o600)
        os.replace(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


@contextlib.contextmanager
def locked(state_path):
    import fcntl
    state_path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    with state_path.with_suffix(".lock").open("a") as stream:
        fcntl.flock(stream, fcntl.LOCK_EX)
        yield


def inspect_interface(name):
    link = json.loads(run(["ip", "-j", "link", "show", "dev", name]))[0]
    features = json.loads(run(["ethtool", "--json", "-k", name]))[0]
    return {
        "mac": link["address"],
        "driver": (Path("/sys/class/net") / name / "device/driver").resolve(strict=True).name,
        "feature": features.get(FEATURE),
        "checksum": features.get("tx-checksum-ip-generic", {}).get("active", False),
    }


def check_identity(expected, current):
    if any(expected[key] != current[key] for key in ("mac", "driver")):
        raise RuntimeError("Interface identity changed; refusing to modify it")


def set_feature(name, enabled):
    run(["ethtool", "-K", name, FEATURE, "on" if enabled else "off"])


def restore_entry(name, entry, state, state_path):
    if not entry["managed"]:
        return {"interface": name, "result": "not-managed"}
    current = inspect_interface(name)
    check_identity(entry, current)
    original = entry["original"]
    feature = current["feature"]
    if feature is None:
        raise RuntimeError("Cannot verify or restore the original feature state")
    if any(feature[key] != original[key] for key in ("active", "requested")):
        set_feature(name, original["requested"])
        feature = inspect_interface(name)["feature"]
        if feature is None or any(feature[key] != original[key] for key in ("active", "requested")):
            raise RuntimeError("Original UDP segmentation state could not be restored")
    entry["managed"] = False
    atomic_json(state_path, state)
    return {"interface": name, "result": "restored", "feature": feature}


def apply(config, state, state_path):
    results = []
    for desired in config["interfaces"]:
        name = desired["name"]
        entry = state["interfaces"][name]
        current = None
        try:
            current = inspect_interface(name)
            check_identity(entry, current)
            route = json.loads(run(["ip", "-j", "route", "get", desired["peer_address"]]))
            if not route or route[0].get("dev") != name:
                raise RuntimeError("Peer no longer uses the configured private interface")
            feature = current["feature"]
            if feature is None or not current["checksum"] or (feature["fixed"] and not feature["active"]):
                raise RuntimeError("UDP segmentation/checksum support is unavailable")
        except (OSError, ValueError, KeyError, subprocess.SubprocessError, RuntimeError) as error:
            # A startup capability failure must leave ordinary UDP networking usable.
            if entry["managed"]:
                restore_entry(name, entry, state, state_path)
            results.append({"interface": name, "result": "software-fallback", "reason": str(error)})
            continue
        if feature["active"]:
            results.append({"interface": name, "result": "already-on", "managed": entry["managed"]})
            continue
        # Journal ownership before changing the kernel so a crash remains recoverable.
        entry["managed"] = True
        atomic_json(state_path, state)
        try:
            set_feature(name, True)
            enabled = inspect_interface(name)
            check_identity(entry, enabled)
            if not enabled["feature"] or not enabled["feature"]["active"]:
                raise RuntimeError("Driver did not activate UDP segmentation")
            results.append({"interface": name, "result": "enabled", "feature": enabled["feature"]})
        except (OSError, ValueError, KeyError, subprocess.SubprocessError, RuntimeError) as error:
            restore_entry(name, entry, state, state_path)
            results.append({"interface": name, "result": "software-fallback", "reason": str(error)})
    return results


def restore(state, state_path):
    results, errors = [], []
    for name, entry in state["interfaces"].items():
        try:
            results.append(restore_entry(name, entry, state, state_path))
        except (OSError, ValueError, KeyError, subprocess.SubprocessError, RuntimeError) as error:
            errors.append(name + ": " + str(error))
    if errors:
        raise RuntimeError("Rollback incomplete; ownership retained for retry: " + "; ".join(errors))
    return results


def install(name, peer_address):
    import ipaddress
    address = ipaddress.IPv4Address(peer_address)
    if not any(address in ipaddress.IPv4Network(network)
               for network in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")):
        raise RuntimeError("Installation requires the actual private peer address")
    if not name or name.startswith("-") or "/" in name:
        raise RuntimeError("Invalid interface name")
    for path in (CONFIG, STATE, PROGRAM, UNIT):
        if path.exists():
            raise RuntimeError("Refusing to overwrite an existing installation: " + str(path))
    current = inspect_interface(name)
    feature = current["feature"]
    route = json.loads(run(["ip", "-j", "route", "get", str(address)]))
    if not route or route[0].get("dev") != name:
        raise RuntimeError("Peer route does not use this interface")
    if feature is None or feature["fixed"] or not current["checksum"]:
        raise RuntimeError("Interface does not have configurable UDP segmentation/checksum support")
    if feature["active"] != feature["requested"]:
        raise RuntimeError("Original active/requested feature states differ; refusing ambiguous rollback")
    config = {"version": 1, "interfaces": [{"name": name, "peer_address": str(address)}]}
    state = {"version": 1, "interfaces": {name: {
        "mac": current["mac"], "driver": current["driver"],
        "original": {key: feature[key] for key in ("active", "requested")},
        "managed": False,
    }}}
    try:
        atomic_json(STATE, state)
        atomic_json(CONFIG, config)
        PROGRAM.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(Path(__file__).resolve(), PROGRAM)
        PROGRAM.chmod(0o755)
        UNIT.write_text(SERVICE, encoding="ascii")
        UNIT.chmod(0o644)
        run(["systemctl", "daemon-reload"])
    except Exception:
        for path in (UNIT, PROGRAM, CONFIG, STATE):
            path.unlink(missing_ok=True)
        raise
    return {"installed": True, "enabled": False, "original": state["interfaces"][name]["original"]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=("install", "apply", "restore", "status"))
    parser.add_argument("--interface")
    parser.add_argument("--peer-address")
    args = parser.parse_args()
    os.umask(0o077)
    if os.geteuid() != 0:
        raise RuntimeError("Administrative privileges are required")
    if args.action == "install":
        if not args.interface or not args.peer_address:
            parser.error("install requires --interface and --peer-address")
        with locked(STATE):
            result = install(args.interface, args.peer_address)
    else:
        with locked(STATE):
            config = json.loads(CONFIG.read_text())
            state = json.loads(STATE.read_text())
            if config["version"] != 1 or state["version"] != 1:
                raise RuntimeError("Unsupported configuration/state version")
            if args.action == "apply":
                result = apply(config, state, STATE)
            elif args.action == "restore":
                result = restore(state, STATE)
            else:
                result = {"config": config, "state": state, "current": {
                    name: inspect_interface(name) for name in state["interfaces"]
                }}
    print(json.dumps(result), flush=True)


if __name__ == "__main__":
    main()
