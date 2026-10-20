# FFXIV Flipper Plugin
- Stack: C#, .NET 10, Dalamud API 15, native Dalamud windows; Universalis market snapshots.
- Build: `dotnet build FFXIVFlipper/FFXIVFlipper.csproj -c Release`.
- Tests: `dotnet run --project tests/Economics.Tests.csproj`. No separate lint tool.
- Keep the website in its existing separate repository. Never copy website assets here.
- Keep UI on the game thread and network requests asynchronous; cancel on disposal and input changes.
- Preserve quality/world/freshness/coverage distinctions. Missing data is not a forecast. Charge whole purchased stacks and full upfront capital.
- Definition of done: build, relevant regression checks, and in-game flows tested or explicitly unverified. Keep the port checklist accurate.
