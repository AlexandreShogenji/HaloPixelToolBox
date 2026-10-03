# Display ownership regression

```powershell
dotnet run --project tests/display-ownership-tests/DisplayOwnershipTests.csproj
```

Links the production display service, its actual serialized device-operation queue, scene-restore service and display models. Only the physical HID boundary is substituted. Tests verify foreground revision changes, rejected stale task writes, shared leases across task pages, cloning, a foreground switch during pagination, queue ordering, queued cancellation, failed writes and ordinary unleased subtitle compatibility. Display-state probes also distinguish the initial restore fallback, successful scene history and temporary task-status content, including snapshot isolation and explicit non-readback wire metadata. No device, DSH, network or user configuration is accessed.
