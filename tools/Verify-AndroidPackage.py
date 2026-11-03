"""Verify the release APK's public package boundaries using the Android SDK."""

import os
from pathlib import Path
import re
import subprocess
import sys
import zipfile


def verify(directory: Path, rid: str, version: str) -> None:
    abi = {"android-arm64": "arm64-v8a", "android-x64": "x86_64"}[rid]
    packages = list(directory.glob("*Signed.apk"))
    if len(packages) != 1:
        raise ValueError("Expected exactly one signed APK.")
    apk = packages[0]
    with zipfile.ZipFile(apk) as archive:
        names = archive.namelist()
        abis = {name.split("/")[1] for name in names if name.startswith("lib/")}
        if abis != {abi}:
            raise ValueError(f"Unexpected APK ABIs: {abis}")
        # .NET Android 36.1.2 and later workloads use different ELF store names.
        store_names = {f"lib/{abi}/libassembly-store.so", f"lib/{abi}/libassemblies.{abi}.blob.so"}
        stores = [name for name in names if name in store_names]
        if len(stores) != 1:
            raise ValueError("Expected exactly one recognized embedded assembly store.")
        store = archive.read(stores[0])
        if b"ProxyNetworker.App.Android" not in store or b"ProxyNetworker.Client.Runtime" not in store:
            raise ValueError("The APK does not embed the required managed assemblies.")
        if b"spyglass" in store.lower():
            raise ValueError("Spyglass was found in the managed assembly store.")
        for name in names:
            if re.search(r"spyglass|appsettings.*\.json|proxynetworker\.client\.json|\.(db|pfx|jks|keystore)$", name, re.I):
                raise ValueError(f"Forbidden package entry: {name}")
    sdk = Path(os.environ.get("ANDROID_HOME", os.environ.get("ANDROID_SDK_ROOT", "")))
    tool = sdk / "build-tools" / "36.0.0" / ("aapt.exe" if os.name == "nt" else "aapt")
    badging = subprocess.check_output([str(tool), "dump", "badging", str(apk)], text=True)
    if "package: name='io.github.jkh404.proxynetworker'" not in badging:
        raise ValueError("Unexpected Android application identity.")
    if f"versionName='{version}'" not in badging or "application-debuggable" in badging:
        raise ValueError("Incorrect version or debuggable release APK.")
    manifest = subprocess.check_output(
        [str(tool), "dump", "xmltree", str(apk), "AndroidManifest.xml"], text=True
    )
    for service in ("NetworkClientService", "NetworkVpnService"):
        if f"io.github.jkh404.proxynetworker.{service}" not in manifest:
            raise ValueError(f"Missing service: {service}")
    if "android.permission.BIND_VPN_SERVICE" not in manifest:
        raise ValueError("Missing VPN binding protection.")
    print(f"PASS {apk.name}: {rid}, {version}, non-debug, embedded assemblies, public boundaries.")


if __name__ == "__main__":
    verify(Path(sys.argv[1]), sys.argv[2], sys.argv[3])
