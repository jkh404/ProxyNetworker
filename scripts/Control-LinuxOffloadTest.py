import argparse
import hashlib
import json
import os
import shutil
import subprocess
from pathlib import Path


FLAGS = ("PN_DISABLE_UDP_GSO", "PN_DISABLE_UDP_GRO", "PN_DIRECT_ADDRESS_ALLOWLIST")


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def run(command, **kwargs):
    return subprocess.run(command, check=True, capture_output=True, text=True, timeout=30, **kwargs).stdout


def live_pid(client):
    pid = int((client / "run/client.pid").read_text().strip())
    executable = Path("/proc") / str(pid) / "exe"
    if str(executable.resolve()).removesuffix(" (deleted)") != str(client / "ProxyNetworker.Client"):
        raise RuntimeError("PID file does not identify the expected client executable")
    return pid


def check_configs(client, state):
    for name, expected in state["config_hashes"].items():
        if digest(client / name) != expected:
            raise RuntimeError("Client configuration changed during the test: " + name)


def status(client, state):
    check_configs(client, state)
    pid = live_pid(client)
    process = Path("/proc") / str(pid)
    environment = dict(
        entry.split(b"=", 1) for entry in (process / "environ").read_bytes().split(b"\0") if b"=" in entry
    )
    log = client / "logs/client-console.latest.log"
    return {
        "pid": pid,
        "executable_sha256": digest(process / "exe"),
        "configuration_unchanged": True,
        "test_environment": {key: environment.get(key.encode(), b"").decode() for key in FLAGS},
        "log_path": str(log.resolve()),
        "log_tail": log.read_text(errors="replace").splitlines()[-12:],
    }


def prepare(workspace, client, args):
    state_path = workspace / "state.json"
    if state_path.exists():
        raise RuntimeError("Test workspace has already been prepared")
    original = client / "ProxyNetworker.Client"
    candidate = workspace / "ProxyNetworker.Client.candidate"
    if digest(original) != args.original_hash or digest(candidate) != args.candidate_hash:
        raise RuntimeError("Original or candidate executable hash mismatch")
    pid = live_pid(client)
    process = Path("/proc") / str(pid)
    if len([item for item in (process / "cmdline").read_bytes().split(b"\0") if item]) != 1:
        raise RuntimeError("This test controller only supports the default-config client invocation")
    environment = dict(
        (key.decode(), value.decode())
        for key, value in (entry.split(b"=", 1) for entry in
                           (process / "environ").read_bytes().split(b"\0") if b"=" in entry)
    )
    configs = {name: digest(client / name) for name in ("appsettings.json", "proxynetworker.client.json")
               if (client / name).is_file()}
    if not configs:
        raise RuntimeError("No client configuration found")
    shutil.copy2(original, workspace / "original-client")
    for name in configs:
        shutil.copy2(client / name, workspace / ("original-" + name))
    state = {
        "client_directory": str(client),
        "original_hash": args.original_hash,
        "candidate_hash": args.candidate_hash,
        "original_pid": pid,
        "original_environment": environment,
        "config_hashes": configs,
    }
    state_path.write_text(json.dumps(state, indent=2))
    candidate.chmod(0o755)
    result = run([str(candidate), "crypto", "self-test"], cwd=workspace, env=environment)
    return {"prepared": True, "self_test": result.strip(), **status(client, state)}


def restart(workspace, client, state, candidate, mode, allowlist):
    check_configs(client, state)
    pid = live_pid(client)
    if digest(Path("/proc") / str(pid) / "exe") not in (state["original_hash"], state["candidate_hash"]):
        raise RuntimeError("Running client changed outside the test")
    expected = state["candidate_hash"] if candidate else state["original_hash"]
    source = workspace / ("ProxyNetworker.Client.candidate" if candidate else "original-client")
    if digest(source) != expected:
        raise RuntimeError("Staged executable changed")
    run(["bash", "run.sh", "stop"], cwd=client)
    temporary = client / "ProxyNetworker.Client.offload-ab-new"
    shutil.copy2(source, temporary)
    temporary.chmod(0o755)
    os.replace(temporary, client / "ProxyNetworker.Client")
    environment = dict(state["original_environment"])
    if candidate:
        for name in FLAGS:
            environment.pop(name, None)
        if mode in ("off", "gro"):
            environment["PN_DISABLE_UDP_GSO"] = "1"
        if mode in ("off", "gso"):
            environment["PN_DISABLE_UDP_GRO"] = "1"
        if allowlist:
            environment["PN_DIRECT_ADDRESS_ALLOWLIST"] = allowlist
    try:
        output = run(["bash", "run.sh", "start"], cwd=client, env=environment)
    except Exception:
        if candidate:
            rollback = client / "ProxyNetworker.Client.offload-ab-rollback"
            shutil.copy2(workspace / "original-client", rollback)
            os.replace(rollback, client / "ProxyNetworker.Client")
            run(["bash", "run.sh", "start"], cwd=client, env=state["original_environment"])
        raise
    return {"mode": mode if candidate else "original", "startup": output.strip(), **status(client, state)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=("prepare", "activate", "restore", "status"))
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--client-directory", required=True)
    parser.add_argument("--original-hash")
    parser.add_argument("--candidate-hash")
    parser.add_argument("--mode", choices=("off", "gso", "gro", "both"), default="both")
    parser.add_argument("--allowlist")
    args = parser.parse_args()
    os.umask(0o077)
    workspace = Path(args.workspace).resolve(strict=True)
    client = Path(args.client_directory).resolve(strict=True)
    if workspace == client or workspace in client.parents or client in workspace.parents:
        raise RuntimeError("Test workspace must be separate from the deployed client directory")
    if args.action == "prepare":
        result = prepare(workspace, client, args)
    else:
        state = json.loads((workspace / "state.json").read_text())
        if state["client_directory"] != str(client):
            raise RuntimeError("Deployment directory differs from the prepared target")
        if args.action == "status":
            result = status(client, state)
        else:
            result = restart(workspace, client, state, args.action == "activate", args.mode, args.allowlist)
    print(json.dumps(result), flush=True)


if __name__ == "__main__":
    main()
