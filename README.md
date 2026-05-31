# Windows Updater for .NET

`windows-updater-dotnet` is the reusable foundation for a native Windows
file-level updater. It is intentionally not a WinUI integration package and does
not use Squirrel, Velopack, WinSparkle, ClickOnce, MSIX App Installer, or another
ready-made product updater.

## Projects

- `src/WindowsUpdater`: reusable updater models, SHA-256 hashing, manifest
  signing, payload metadata, and immutable version-directory state.
- `src/WindowsUpdater.Release`: release-side manifest generation, compressed
  payload metadata, SemVer/build-number release state, Conventional Commits
  changelog drafts, and S3/CloudFront dry-run planning.
- `src/WindowsUpdater.Launcher`: generic launcher shell for an immutable
  version directory.
- `src/WindowsUpdater.UpdateRunner`: generic update-runner shell.
- `src/WindowsUpdater.Cli`: .NET tool packaged as `windows-updater-release`.
- `tests/WindowsUpdater.Tests`: no-external-dependency console tests.

## Packages

- `MarlonJD.WindowsUpdater`
- `MarlonJD.WindowsUpdater.Release`
- `MarlonJD.WindowsUpdater.Cli` as the `windows-updater-release` .NET tool

## Checks

```sh
dotnet build WindowsUpdater.sln
dotnet run --project tests/WindowsUpdater.Tests/WindowsUpdater.Tests.csproj
```

No test runner package is used yet, so `dotnet test` is not required for the
initial foundation.

## CLI

Generate a signed target manifest and optional delta:

```sh
dotnet run --project src/WindowsUpdater.Cli/WindowsUpdater.Cli.csproj -- generate \
  --release-dir ./artifacts/v2/release \
  --output-dir ./artifacts/v2/update \
  --channel stable \
  --architecture win-x64 \
  --version 1.1.0 \
  --build 110 \
  --publisher "CN=Example Publisher" \
  --base-manifest ./artifacts/v1/update/target-file-manifest.json
```

Produce an S3/CloudFront dry-run upload plan:

```sh
dotnet run --project src/WindowsUpdater.Cli/WindowsUpdater.Cli.csproj -- dry-run \
  --manifest ./artifacts/v2/update/target-file-manifest.json \
  --bucket windows-updates-prod \
  --cloudfront https://updates.example.com \
  --platform windows \
  --channel stable
```

Host applications own UI prompts, shortcuts, app activation, runtime decisions,
and certificate policy. This repository owns only reusable updater and release
mechanics.
