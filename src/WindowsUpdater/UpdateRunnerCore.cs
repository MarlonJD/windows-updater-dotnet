namespace WindowsUpdater;

public enum UpdateStatus
{
    NotAvailable,
    Available,
    Downloading,
    Staged,
    RestartReady,
    Installing,
    Installed,
    Failed,
    RolledBack
}

public sealed record UpdateRunnerResult(UpdateStatus Status, string? Reason = null);

public interface IProcessWaiter
{
    Task WaitForExitAsync(int processId, CancellationToken cancellationToken = default);
}

public sealed class SystemProcessWaiter : IProcessWaiter
{
    public async Task WaitForExitAsync(int processId, CancellationToken cancellationToken = default)
    {
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        await process.WaitForExitAsync(cancellationToken);
    }
}

public interface IProcessLauncher
{
    ProcessLaunchResult Launch(string executablePath, string? arguments = null);
}

public sealed class SystemProcessLauncher : IProcessLauncher
{
    public ProcessLaunchResult Launch(string executablePath, string? arguments = null)
    {
        try
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
            });

            return process is null
                ? new ProcessLaunchResult(false, Error: "Process.Start returned null.")
                : new ProcessLaunchResult(true, process.Id);
        }
        catch (Exception error)
        {
            return new ProcessLaunchResult(false, Error: error.Message);
        }
    }
}

public interface IUpdateLaunchProbe
{
    Task<bool> WaitForSuccessAsync(
        string successMarkerPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class FileUpdateLaunchProbe : IUpdateLaunchProbe
{
    public async Task<bool> WaitForSuccessAsync(
        string successMarkerPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(successMarkerPath))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return false;
    }
}

public sealed class UpdateRunnerCore
{
    private readonly CurrentVersionStore stateStore;
    private readonly ManifestSignatureService signatureService;
    private readonly StagedVersionVerifier verifier;
    private readonly IProcessWaiter processWaiter;
    private readonly IProcessLauncher processLauncher;
    private readonly IUpdateLaunchProbe launchProbe;
    private readonly IReadOnlyDictionary<string, string> requestPublicKeys;

    public UpdateRunnerCore(
        CurrentVersionStore stateStore,
        IReadOnlyDictionary<string, string> requestPublicKeys,
        ManifestSignatureService? signatureService = null,
        StagedVersionVerifier? verifier = null,
        IProcessWaiter? processWaiter = null,
        IProcessLauncher? processLauncher = null,
        IUpdateLaunchProbe? launchProbe = null)
    {
        this.stateStore = stateStore;
        this.requestPublicKeys = requestPublicKeys;
        this.signatureService = signatureService ?? new ManifestSignatureService();
        this.verifier = verifier ?? new StagedVersionVerifier();
        this.processWaiter = processWaiter ?? new SystemProcessWaiter();
        this.processLauncher = processLauncher ?? new SystemProcessLauncher();
        this.launchProbe = launchProbe ?? new FileUpdateLaunchProbe();
    }

    public async Task<UpdateRunnerResult> ApplyAsync(
        LocalUpdateRequest request,
        ReleaseManifest targetManifest,
        CancellationToken cancellationToken = default)
    {
        if (!signatureService.Verify(request, requestPublicKeys))
        {
            return new UpdateRunnerResult(UpdateStatus.Failed, "Local update request signature is not trusted.");
        }

        if (request.AppProcessId is int processId)
        {
            await processWaiter.WaitForExitAsync(processId, cancellationToken);
        }

        var verification = await verifier.VerifyAsync(request.StagedVersionDirectory, targetManifest, cancellationToken);
        if (!verification.IsValid)
        {
            return new UpdateRunnerResult(UpdateStatus.Failed, string.Join(Environment.NewLine, verification.Errors));
        }

        var rollback = new RollbackCandidate(
            request.PreviousState.Version,
            request.PreviousState.Build,
            request.PreviousState.VersionDirectory,
            request.PreviousState.ExecutablePath,
            request.PreviousState.ManifestHash);
        var target = request.TargetState with { RollbackCandidate = rollback };

        await stateStore.WriteAtomicAsync(target, cancellationToken);

        var launch = processLauncher.Launch(request.LauncherPath);
        if (!launch.Started)
        {
            await RollBackAsync(request, cancellationToken);
            return new UpdateRunnerResult(UpdateStatus.RolledBack, launch.Error ?? "Launcher did not start.");
        }

        var launched = await launchProbe.WaitForSuccessAsync(
            request.SuccessMarkerPath,
            TimeSpan.FromSeconds(Math.Max(1, request.LaunchProbeTimeoutSeconds)),
            cancellationToken);

        if (!launched)
        {
            await RollBackAsync(request, cancellationToken);
            processLauncher.Launch(request.LauncherPath);
            return new UpdateRunnerResult(UpdateStatus.RolledBack, "Updated app did not report startup success.");
        }

        await stateStore.WriteAtomicAsync(target with { LastSuccessfulLaunchUtc = DateTimeOffset.UtcNow }, cancellationToken);
        return new UpdateRunnerResult(UpdateStatus.Installed);
    }

    public Task<UpdateRunnerResult> SwitchAsync(
        LocalUpdateRequest request,
        ReleaseManifest targetManifest,
        CancellationToken cancellationToken = default)
    {
        return ApplyAsync(request, targetManifest, cancellationToken);
    }

    private async Task RollBackAsync(LocalUpdateRequest request, CancellationToken cancellationToken)
    {
        await stateStore.WriteAtomicAsync(
            request.PreviousState with { LastSuccessfulLaunchUtc = DateTimeOffset.UtcNow },
            cancellationToken);
    }
}
