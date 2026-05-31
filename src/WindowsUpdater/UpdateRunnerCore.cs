namespace WindowsUpdater;

public enum UpdateStatus
{
    Installing,
    Installed,
    Failed,
    RolledBack
}

public sealed record UpdateRunnerResult(UpdateStatus Status, string? Reason = null);

public sealed class UpdateRunnerCore
{
    private readonly CurrentVersionStore stateStore;
    private readonly ManifestSignatureService signatureService;
    private readonly IReadOnlyDictionary<string, string> requestPublicKeys;

    public UpdateRunnerCore(
        CurrentVersionStore stateStore,
        IReadOnlyDictionary<string, string> requestPublicKeys,
        ManifestSignatureService? signatureService = null)
    {
        this.stateStore = stateStore;
        this.requestPublicKeys = requestPublicKeys;
        this.signatureService = signatureService ?? new ManifestSignatureService();
    }

    public async Task<UpdateRunnerResult> SwitchAsync(
        LocalUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!signatureService.Verify(request, requestPublicKeys))
        {
            return new UpdateRunnerResult(UpdateStatus.Failed, "Local update request signature is not trusted.");
        }

        if (!Directory.Exists(request.StagedVersionDirectory))
        {
            return new UpdateRunnerResult(UpdateStatus.Failed, "Staged version directory is missing.");
        }

        var rollback = new RollbackCandidate(
            request.PreviousState.Version,
            request.PreviousState.Build,
            request.PreviousState.VersionDirectory,
            request.PreviousState.ExecutablePath,
            request.PreviousState.ManifestHash);
        var target = request.TargetState with { RollbackCandidate = rollback };

        await stateStore.WriteAtomicAsync(target, cancellationToken);
        return new UpdateRunnerResult(UpdateStatus.Installed);
    }
}
