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

## End-to-End Release Workflow

This is the full intended release flow from source changes to a published
update.

### 1. Merge Changes With Conventional Commits

Every user-visible change should reach the release branch with a Conventional
Commit subject:

```text
feat(updater): download changed payload files
fix(runner): restore previous version on launch failure
perf(release): reduce payload compression time
security: rotate updater signing key
docs: update release checklist
```

The release-note draft includes `feat`, `fix`, `perf`, `security`, and breaking
changes by default. It excludes `docs`, `test`, `chore`, and non-breaking
`refactor` commits unless the release owner edits the draft.

Before cutting a release, the host app repository should be clean, on the
release branch, and up to date with its remote.

### 2. Decide The Next Version

Use SemVer for the user-visible version and a monotonically increasing build
number for updater ordering:

```text
releaseId = <semver>+<buildNumber>
example   = 1.1.0+110
```

Version bump rules:

- `MAJOR`: incompatible product or updater contract changes.
- `MINOR`: backward-compatible features.
- `PATCH`: backward-compatible fixes.
- prerelease: beta or release-candidate channels, for example `1.2.0-beta.1`.

Clients decide whether a target is newer by comparing build numbers:

```text
target.build > current.build
```

Keep the local build-number ledger in the host app or release repository:

```text
release/windows-updater-release-state.json
```

Example:

```json
{
  "lastBuildNumber": 109,
  "lastStableVersion": "1.0.0",
  "lastBetaVersion": "1.1.0-beta.1"
}
```

When releasing `1.1.0`, allocate the next build number and commit the updated
state file with the release change:

```json
{
  "lastBuildNumber": 110,
  "lastStableVersion": "1.1.0",
  "lastBetaVersion": "1.1.0-beta.1"
}
```

This file is the source-controlled record of the latest allocated build number.
The public update channel still comes from `latest.json`, described below.

### 3. Draft The Changelog From Changes

Collect commit subjects since the previous release tag:

```sh
git log --format=%s v1.0.0+100..HEAD
```

Pass the subjects to the release tool as a pipe-separated list:

```sh
windows-updater-release changelog \
  --version 1.1.0 \
  --commits "feat(updater): download changed payload files|fix(runner): restore previous version on launch failure|docs: update checklist"
```

Review the generated markdown. The release owner should edit it for clarity and
save the final text in the host app release notes, for example:

```text
release/notes/1.1.0+110.md
```

The changelog is user-facing release context. It is not the update channel
pointer by itself.

### 4. Build The EXE Files

Build the host app as an unpackaged release. The exact host app project name is
app-specific; this example uses `ExampleApp.Windows.csproj`.

```powershell
dotnet publish .\src\ExampleApp.Windows\ExampleApp.Windows.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:WindowsPackageType=None `
  -p:PublishSingleFile=false `
  -o .\artifacts\1.1.0+110\app
```

Build the stable launcher and update runner:

```powershell
dotnet publish .\src\WindowsUpdater.Launcher\WindowsUpdater.Launcher.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o .\artifacts\1.1.0+110\launcher

dotnet publish .\src\WindowsUpdater.UpdateRunner\WindowsUpdater.UpdateRunner.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o .\artifacts\1.1.0+110\runner
```

Create the signed release directory shape:

```text
artifacts\
  1.1.0+110\
    release\
      ExampleApp.exe
      ExampleApp.dll
      ExampleApp.deps.json
      ExampleApp.runtimeconfig.json
      WindowsUpdater.Launcher.exe
      WindowsUpdater.UpdateRunner.exe
      Resources\
    update\
```

Copy the host app publish output into `release\`, then copy
`WindowsUpdater.Launcher.exe` and `WindowsUpdater.UpdateRunner.exe` into the
same directory.

Repeat the publish process for each supported architecture, such as `win-x64`
and `win-arm64`.

### 5. Sign And Verify The EXE/DLL Files

Sign every PE file after publish and before manifest generation. Do not modify
signed files after this step.

```powershell
$release = ".\artifacts\1.1.0+110\release"
$timestamp = "http://timestamp.digicert.com"

Get-ChildItem $release -Recurse -Include *.exe,*.dll |
  ForEach-Object {
    signtool sign /fd SHA256 /td SHA256 /tr $timestamp `
      /sha1 $env:WINDOWS_CODE_SIGNING_CERT_THUMBPRINT $_.FullName
  }

Get-ChildItem $release -Recurse -Include *.exe,*.dll |
  ForEach-Object {
    signtool verify /pa /tw $_.FullName
  }
```

The updater manifest hashes must be generated from the final signed bytes.

### 6. Generate The Target Manifest And Delta

For the first release on a channel, generate only the target file manifest:

```sh
windows-updater-release generate \
  --release-dir ./artifacts/1.0.0+100/release \
  --output-dir ./artifacts/1.0.0+100/update \
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

For later releases, pass the previous target manifest so changed-file payloads
and delete operations are generated:

```sh
windows-updater-release generate \
  --release-dir ./artifacts/1.1.0+110/release \
  --output-dir ./artifacts/1.1.0+110/update \
  --product ExampleApp.Windows \
  --channel stable \
  --architecture win-x64 \
  --version 1.1.0 \
  --build 110 \
  --publisher "CN=Example Publisher" \
  --base-manifest ./artifacts/1.0.0+100/update/target-file-manifest.json \
  --required-files "ExampleApp.exe;ExampleApp.deps.json;ExampleApp.runtimeconfig.json;WindowsUpdater.Launcher.exe;WindowsUpdater.UpdateRunner.exe" \
  --key-id updater-key-2026 \
  --private-key "$WINDOWS_UPDATER_PRIVATE_KEY"
```

The update directory contains:

```text
artifacts/1.1.0+110/update/
  target-file-manifest.json
  delta-from-100-to-110.json
  payload/
    <changed-file-sha256>.gz
```

Only files with changed hashes become `downloadFile` payloads. Unchanged files
are represented as `copyFromBase`, and files removed from the target release are
represented as `delete`.

### 7. Create Release Metadata And Channel Pointer

The host release process should write a release metadata file next to the
manifest output:

```text
artifacts/1.1.0+110/update/release.json
```

Example:

```json
{
  "product": "ExampleApp.Windows",
  "channel": "stable",
  "architecture": "win-x64",
  "version": "1.1.0",
  "build": 110,
  "releaseId": "1.1.0+110",
  "commit": "<host-app-git-sha>",
  "targetManifestPath": "target-file-manifest.json",
  "targetManifestSha256": "<sha256>",
  "deltaManifests": [
    {
      "baseBuild": 100,
      "targetBuild": 110,
      "path": "delta-from-100-to-110.json",
      "sha256": "<sha256>"
    }
  ],
  "releaseNotesPath": "release/notes/1.1.0+110.md",
  "publishedAtUtc": null
}
```

The public channel pointer is:

```text
windows/<channel>/latest.json
```

Example `latest.json`:

```json
{
  "product": "ExampleApp.Windows",
  "channel": "stable",
  "architecture": "win-x64",
  "version": "1.1.0",
  "build": 110,
  "releaseId": "1.1.0+110",
  "releaseManifestUrl": "https://updates.example.com/windows/stable/releases/1.1.0+110/release.json",
  "targetManifestUrl": "https://updates.example.com/windows/stable/releases/1.1.0+110/target-file-manifest.json",
  "minimumSupportedBuild": 100,
  "mandatory": false,
  "publishedAtUtc": "2026-05-31T19:00:00Z"
}
```

This file answers "which version is currently published?" for clients. The
source-controlled release state answers "which build number was allocated
last?" and the git tag answers "which source commit produced this release?"

### 8. Dry-Run The Upload Order

Create a dry-run plan before uploading release objects:

```sh
windows-updater-release dry-run \
  --manifest ./artifacts/1.1.0+110/update/target-file-manifest.json \
  --bucket windows-updates-prod \
  --cloudfront https://updates.example.com \
  --platform windows \
  --channel stable
```

The upload order must be:

1. compressed payload files under `windows/stable/releases/1.1.0+110/payload/`;
2. `target-file-manifest.json`;
3. `delta-from-100-to-110.json`;
4. `release.json`;
5. `windows/stable/latest.json`.

Do not upload or overwrite `latest.json` until every immutable release object is
present and its SHA-256 hash matches local metadata.

### 9. Upload And Publish

Upload immutable objects first:

```text
s3://windows-updates-prod/windows/stable/releases/1.1.0+110/
  release.json
  target-file-manifest.json
  delta-from-100-to-110.json
  payload/<sha256>.gz
```

Verify each uploaded object by downloading it back or comparing S3 checksums.

Publish the release by uploading the mutable channel pointer last:

```text
s3://windows-updates-prod/windows/stable/latest.json
```

If upload fails before `latest.json`, clients should not see the new release.
If `latest.json` is wrong, fix it by pointing back to the previous known-good
release or by publishing a corrected signed channel pointer.

### 10. Tag, Record, And Monitor

After publishing:

```sh
git tag v1.1.0+110
git push origin v1.1.0+110
```

Record release evidence in the host app repository:

```text
release/evidence/1.1.0+110.md
```

Include:

- release id, version, build, channel, architecture, and git SHA;
- package paths and SHA-256 hashes;
- signing certificate subject and thumbprint;
- target manifest hash;
- delta manifest hashes;
- `latest.json` URL and hash;
- smoke test result;
- rollback test result;
- release owner approval.

Monitor update checks, changed bytes downloaded, staged versions, successful
launch markers, failures, and rollbacks. Keep the previous version directory
available until the new release has passed its retention window.

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
