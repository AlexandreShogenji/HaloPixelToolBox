# UI resource lifecycle regression

Links the actual `WindowActivityService` and `DeviceConnectionStatusService` source. A controllable timer and fake physical-device boundary verify startup, hide/minimize signals, restore, duplicate visibility notifications, shared shell/dashboard enumeration, detachment and explicit refresh. No audio, device, DSH, native window or user configuration is accessed.

```powershell
dotnet run --project tests/resource-lifecycle-tests/ResourceLifecycleTests.csproj
dotnet run --project tests/resource-lifecycle-tests/ResourceLifecycleTests.csproj -- --hidden-start
```

Actual `MainWindow` event delivery and dashboard animation remain native UI acceptance checks: minimize or hide the window, then restore it and verify the current preview and device state resume. These tests exercise the real service boundary, not a substitute for WinUI event delivery.
