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

## Install

Use the packages once they are published:

```sh
dotnet add package MarlonJD.WindowsUpdater
dotnet add package MarlonJD.WindowsUpdater.Release
dotnet tool install --global MarlonJD.WindowsUpdater.Cli
```

During local development, reference the projects directly:

```sh
dotnet add <host-app>.csproj reference src/WindowsUpdater/WindowsUpdater.csproj
dotnet add <release-tool>.csproj reference src/WindowsUpdater.Release/WindowsUpdater.Release.csproj
```

## Release Layout

The host application should install into a per-user root with immutable version
directories:

```text
%LocalAppData%\Programs\<AppName>\
  WindowsUpdater.Launcher.exe
  WindowsUpdater.UpdateRunner.exe
  state\
    current.json
    last-known-good.json
  versions\
    1.0.0+100\
      App.exe
      App.dll
      App.deps.json
      App.runtimeconfig.json
      Resources\
    1.1.0+110\
      ...
  staging\
  downloads\
```

Shortcuts should point to the stable launcher, not directly to a versioned app
executable. Version directories should be treated as immutable after activation.

## Host App Integration

The host app owns UI prompts, shortcuts, app activation, runtime decisions, and
certificate policy. This package owns reusable updater and release mechanics.

Write the initial active version state after install:

```csharp
using WindowsUpdater;

var installRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Programs",
    "ExampleApp");
var state = new CurrentVersionState(
    Version: "1.0.0",
    Build: 100,
    VersionDirectory: "versions/1.0.0+100",
    ExecutablePath: "ExampleApp.exe",
    ManifestHash: "<target-file-manifest-sha256>",
    LastSuccessfulLaunchUtc: DateTimeOffset.UtcNow);

await new CurrentVersionStore(installRoot).WriteAtomicAsync(state);
```

Launch the active version through the generic launcher core:

```csharp
using WindowsUpdater;

var launcher = new LauncherCore(new CurrentVersionStore(installRoot));
var launch = await launcher.LaunchActiveVersionAsync();

if (!launch.Started)
{
    Console.Error.WriteLine(launch.Error);
}
```

Create a signed local update request after the host app has staged and verified
the target version:

```csharp
using WindowsUpdater;

var previous = await new CurrentVersionStore(installRoot).ReadAsync()
    ?? throw new InvalidOperationException("No active version is configured.");
var target = new CurrentVersionState(
    Version: "1.1.0",
    Build: 110,
    VersionDirectory: "versions/1.1.0+110",
    ExecutablePath: "ExampleApp.exe",
    ManifestHash: "<target-file-manifest-sha256>");
var request = new LocalUpdateRequest(
    InstallRoot: installRoot,
    AppProcessId: Environment.ProcessId,
    StagedVersionDirectory: Path.Combine(installRoot, "staging", "1.1.0+110"),
    TargetState: target,
    PreviousState: previous,
    LauncherPath: Path.Combine(installRoot, "WindowsUpdater.Launcher.exe"),
    SuccessMarkerPath: Path.Combine(installRoot, "state", "startup-success-1.1.0+110"),
    LaunchProbeTimeoutSeconds: 30);

var signedRequest = new ManifestSignatureService().Sign(
    request,
    keyId: "updater-key-2026",
    privateKey: Environment.GetEnvironmentVariable("WINDOWS_UPDATER_PRIVATE_KEY")
        ?? throw new InvalidOperationException("Missing updater signing key."));

await ManifestJson.WriteAsync(
    Path.Combine(installRoot, "state", "pending-update-request.json"),
    signedRequest);
```

The host app should then start `WindowsUpdater.UpdateRunner.exe` detached and
exit. The update runner verifies the signed request and atomically switches the
active state. Future revisions should add host-specific staged-version
verification and launch probing before enabling production updates.

## Generate Release Manifests

The release directory must already contain the signed application files,
launcher, update runner, runtime files, resources, and manifests before running
the release tool.

Generate a target manifest:

```sh
windows-updater-release generate \
  --release-dir ./artifacts/v1/release \
  --output-dir ./artifacts/v1/update \
  --product ExampleApp.Windows \
  --channel stable \
  --architecture win-x64 \
  --version 1.0.0 \
  --build 100 \
  --publisher "CN=Example Publisher" \
  --required-files "ExampleApp.exe;ExampleApp.deps.json;ExampleApp.runtimeconfig.json;WindowsUpdater.Launcher.exe;WindowsUpdater.UpdateRunner.exe" \
  --key-id updater-key-2026 \
  --private-key "$WINDOWS_UPDATER_PRIVATE_KEY"
```

Generate a target manifest plus changed-file delta from a previous release:

```sh
windows-updater-release generate \
  --release-dir ./artifacts/v2/release \
  --output-dir ./artifacts/v2/update \
  --product ExampleApp.Windows \
  --channel stable \
  --architecture win-x64 \
  --version 1.1.0 \
  --build 110 \
  --publisher "CN=Example Publisher" \
  --base-manifest ./artifacts/v1/update/target-file-manifest.json \
  --required-files "ExampleApp.exe;ExampleApp.deps.json;ExampleApp.runtimeconfig.json;WindowsUpdater.Launcher.exe;WindowsUpdater.UpdateRunner.exe" \
  --key-id updater-key-2026 \
  --private-key "$WINDOWS_UPDATER_PRIVATE_KEY"
```

Outputs:

- `target-file-manifest.json`
- `delta-from-<base-build>-to-<target-build>.json` when `--base-manifest` is set
- `payload/<sha256>.gz` for changed files

## Draft Release Notes

Generate a release-note draft from Conventional Commit subjects:

```sh
windows-updater-release changelog \
  --version 1.1.0 \
  --commits "feat(updater): stage changed payloads|fix: reject stale builds|docs: update readme"
```

Included by default: `feat`, `fix`, `perf`, `security`, and breaking changes.
Docs, chores, tests, and refactors are omitted unless they are breaking.

## Plan S3 and CloudFront Publish

Create a dry-run plan before uploading release objects:

```sh
windows-updater-release dry-run \
  --manifest ./artifacts/v2/update/target-file-manifest.json \
  --bucket windows-updates-prod \
  --cloudfront https://updates.example.com \
  --platform windows \
  --channel stable
```

The plan orders immutable release objects before the mutable channel pointer:

1. compressed payload objects;
2. target manifest;
3. delta manifests;
4. `latest.json`.

Do not publish `latest.json` until every referenced immutable object is present
and verified.

## Build And Pack

Build, run the console tests, and package the libraries/tool:

```sh
dotnet build WindowsUpdater.sln -m:1 -p:UseSharedCompilation=false
dotnet run --no-build --project tests/WindowsUpdater.Tests/WindowsUpdater.Tests.csproj
dotnet pack WindowsUpdater.sln -m:1 -p:UseSharedCompilation=false -o artifacts/packages
```

The `-m:1` flag avoids MSBuild parallelism stalls seen on some macOS/.NET 10
developer machines.

## License

Copyright (C) 2026 Burak Karahan.

This project is licensed under `LGPL-3.0-or-later`.

## Current Limits

This initial package provides reusable foundation pieces only. It does not yet
download manifests from a CDN, verify Authenticode signatures, install
shortcuts, write uninstall registry entries, or implement host-app UI prompts.
Host applications must add those pieces around these primitives before shipping
production updates.
