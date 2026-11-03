# Avalonia Client Preview

The GUI is separate from the standalone `ProxyNetworker.Client` CLI. Both
reuse the configuration models, management API, port tunnels and Core
protocol implementation. The CLI keeps its own startup and session host.

The UI uses Avalonia 12, TKCode.Arc, Arc.Mobile and Lucide icons. Spyglass is
enabled in Debug only. Release packages must not contain Spyglass, client
credentials or machine-local configuration.

The desktop host also supplies Avalonia Fluent base templates for built-in
controls such as native tray menus. Arc continues to style the application's
components; the Android host does not depend on Fluent.

## Windows

```powershell
dotnet run --project ProxyNetworker.App.Desktop -- --config appsettings.json
dotnet publish ProxyNetworker.App.Desktop -c Release -r win-x64 --self-contained true -o artifacts/desktop-win-x64
```

Keep the `worker/` directory next to the desktop executable. The GUI runs
without administrator rights; its Worker requests elevation only when a
virtual network is enabled. Port-only connections do not need elevation.
While connected, closing the window leaves the network running in the tray.
Tray Exit disconnects; a second GUI launch activates the existing window.
Without tray support, closing the window disconnects and exits.

Configuration is loaded from `--config`, the working directory, the
executable directory, or the user's local application data directory.
Importing a desktop configuration retains its path. CLI startup remains
configuration-driven and does not require the GUI.

Windows packages are managed self-contained applications, not Native AOT
GUI packages. The standalone CLI retains Native AOT publishing.

## Android

Install the .NET 10 Android workload, Android SDK 36 and JDK 21. Android is
not part of the ordinary solution build.

```powershell
dotnet workload install android
dotnet publish ProxyNetworker.App.Android -c Release -r android-x64 -p:RuntimeIdentifiers=android-x64 -o artifacts/android-x64
dotnet publish ProxyNetworker.App.Android -c Release -r android-arm64 -p:RuntimeIdentifiers=android-arm64 -o artifacts/android-arm64
```

Set `AndroidSdkDirectory` and `JavaSdkDirectory` when their locations cannot
be discovered automatically. The explicit `RuntimeIdentifiers` override
produces one ABI per package; `-r` alone does not override the project's
multi-ABI list.

Use the system file picker to import a client configuration. Android keeps
its own copy in application-private storage and does not edit the imported
document. System VPN authorization is required for a virtual network.
QUIC is rejected explicitly on Android; it does not silently select UDP.

### Background Lifecycle

- A private foreground `NetworkClientService` owns the connection and binds
  the system `VpnService` in the same process. The Activity owns neither.
  Home, screen off and task dismissal do not request disconnection.
- A persistent notification opens the app or disconnects the network.
  Settings shows the actual notification and battery policy, with explicit
  links to Android's settings. The app does not silently change exemptions,
  request boot startup, hold an indefinite wake lock or enable always-on VPN.
- A private preference records whether the user still requests a connection.
  A system-delivered sticky restart may reload the saved configuration only
  while that request remains active and VPN authorization still exists.
- Disconnect, notification stop and VPN revocation clear the request before
  cleanup. Reopening the UI attaches to an existing service's actual state.
- `START_STICKY` is a best-effort system request, not a keep-alive guarantee.
  After separating the session owner from the system VPN binding, the final
  Android 16 x64 emulator package restored a new process and TUN in three
  no-Activity SIGKILL tests (3.33, 6.12 and 18.16 seconds), with full-MTU ICMP
  and TCP/UDP payload verification each time. Earlier failed tests are kept
  in the acceptance report; other Android versions and vendors may differ.
- A system Force Stop is not bypassed. Android vendor battery managers,
  physical-device sleep and Wi-Fi/cellular switching still need device tests.

The APKs produced by the preview workflow use development signing. They are
not store releases or a production signing arrangement. CI runners generate
their own development keys, so a later build may not install over an earlier
APK. Keep the original imported configuration outside the app before
uninstalling; uninstall removes the app's private configuration. Do not use
this signing arrangement for unattended production upgrades.

Versioned releases also attach the Windows GUI ZIP and both Android APKs as
explicitly named preview assets. Their preview status does not change just
because the standalone CLI release has a stable version tag.

## Verification Scope

Windows x64 and an Android 16/API 36 x64 emulator have real OS TUN and
encrypted relay data-plane evidence. ARM64 APK compilation is not ARM64
phone acceptance. macOS physical-machine testing remains deferred; Linux
and macOS GUI workflows are not covered by the Windows results.

See [the acceptance report](../docs/2026-11-03-client-acceptance-report.md)
for exact results, failures, pending checks and local evidence names.
