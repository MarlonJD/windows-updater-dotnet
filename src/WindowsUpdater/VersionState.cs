namespace WindowsUpdater;

public sealed record RollbackCandidate(
    string Version,
    long Build,
    string VersionDirectory,
    string ExecutablePath,
    string ManifestHash);

public sealed record CurrentVersionState(
    string Version,
    long Build,
    string VersionDirectory,
    string ExecutablePath,
    string ManifestHash,
    RollbackCandidate? RollbackCandidate = null,
    DateTimeOffset? LastSuccessfulLaunchUtc = null);

public sealed record LocalUpdateRequest(
    string InstallRoot,
    int? AppProcessId,
    string StagedVersionDirectory,
    CurrentVersionState TargetState,
    CurrentVersionState PreviousState,
    string LauncherPath,
    string SuccessMarkerPath,
    int LaunchProbeTimeoutSeconds,
    ManifestSignature? Signature = null);

public sealed class CurrentVersionStore
{
    private readonly string installRoot;
    private readonly string currentPath;
    private readonly string lastKnownGoodPath;

    public CurrentVersionStore(string installRoot)
    {
        this.installRoot = Path.GetFullPath(installRoot);
        var stateDirectory = Path.Combine(this.installRoot, "state");
        currentPath = Path.Combine(stateDirectory, "current.json");
        lastKnownGoodPath = Path.Combine(stateDirectory, "last-known-good.json");
    }

    public string StatePath => currentPath;

    public string LastKnownGoodPath => lastKnownGoodPath;

    public string ResolveVersionDirectory(string versionDirectory)
    {
        return Path.IsPathRooted(versionDirectory)
            ? versionDirectory
            : Path.Combine(installRoot, versionDirectory);
    }

    public string ResolveExecutablePath(CurrentVersionState state)
    {
        return Path.Combine(ResolveVersionDirectory(state.VersionDirectory), state.ExecutablePath);
    }

    public async Task<CurrentVersionState?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(currentPath))
        {
            return null;
        }

        return await ManifestJson.ReadAsync<CurrentVersionState>(currentPath, cancellationToken);
    }

    public async Task<CurrentVersionState?> ReadOrFallbackAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await ReadAsync(cancellationToken);
            if (state is null)
            {
                return null;
            }

            return ResolveIfUsable(state)
                ?? RollbackToCurrentState(state.RollbackCandidate)
                ?? await ReadLastKnownGoodAsync(cancellationToken);
        }
        catch
        {
            return await ReadLastKnownGoodAsync(cancellationToken);
        }
    }

    public async Task WriteAtomicAsync(CurrentVersionState state, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(currentPath) ?? ".");
        await WriteAtomicJsonAsync(currentPath, state, cancellationToken);

        if (state.LastSuccessfulLaunchUtc is not null)
        {
            await WriteAtomicJsonAsync(lastKnownGoodPath, state, cancellationToken);
        }
    }

    private async Task<CurrentVersionState?> ReadLastKnownGoodAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(lastKnownGoodPath))
        {
            return null;
        }

        try
        {
            var state = await ManifestJson.ReadAsync<CurrentVersionState>(lastKnownGoodPath, cancellationToken);
            return ResolveIfUsable(state);
        }
        catch
        {
            return null;
        }
    }

    private CurrentVersionState? ResolveIfUsable(CurrentVersionState state)
    {
        var versionDirectory = ResolveVersionDirectory(state.VersionDirectory);
        return Directory.Exists(versionDirectory)
            && File.Exists(Path.Combine(versionDirectory, state.ExecutablePath))
                ? state
                : null;
    }

    private CurrentVersionState? RollbackToCurrentState(RollbackCandidate? rollback)
    {
        if (rollback is null)
        {
            return null;
        }

        var versionDirectory = ResolveVersionDirectory(rollback.VersionDirectory);
        if (!Directory.Exists(versionDirectory)
            || !File.Exists(Path.Combine(versionDirectory, rollback.ExecutablePath)))
        {
            return null;
        }

        return new CurrentVersionState(
            rollback.Version,
            rollback.Build,
            rollback.VersionDirectory,
            rollback.ExecutablePath,
            rollback.ManifestHash,
            LastSuccessfulLaunchUtc: DateTimeOffset.UtcNow);
    }

    private static async Task WriteAtomicJsonAsync(
        string path,
        CurrentVersionState state,
        CancellationToken cancellationToken)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await ManifestJson.WriteAsync(tempPath, state, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }
}
