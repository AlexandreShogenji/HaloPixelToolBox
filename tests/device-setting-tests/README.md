# Categorical device setting regressions

Run `dotnet run --project tests/device-setting-tests/DeviceSettingTests.csproj -c Release`.
The DSH suite in `eng/Test.ps1` also includes these tests.

The executable links the production cosmetic reference resolver, standalone-command default
parser, ambient effect resolver, and lighting coordinator. Hardware writes and persisted
profile properties are replaced with in-memory fakes; it never changes the connected speaker
or the user's saved settings.

Coverage includes missing-target random defaults, invalid explicit parameter rejection,
named/partial/traditional/ordinal references, random selection excluding equivalent current
color pairs, singleton/empty catalogs, failed-write state, and two overlapping preset requests
choosing against the latest successful write under the mutation gate.

The generic selection cases also exercise the resolver shared by scene choices. Scene
catalog loading and real hardware delivery remain part of application integration validation.
