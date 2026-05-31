namespace WindowsUpdater;

public sealed record StagedVersionVerificationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static StagedVersionVerificationResult Valid { get; } = new(true, []);
}

public sealed class StagedVersionVerifier
{
    private readonly IFileSignatureVerifier signatureVerifier;

    public StagedVersionVerifier(IFileSignatureVerifier? signatureVerifier = null)
    {
        this.signatureVerifier = signatureVerifier ?? new PlatformFileSignatureVerifier();
    }

    public async Task<StagedVersionVerificationResult> VerifyAsync(
        string stagedDirectory,
        ReleaseManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(stagedDirectory, file.Path);
            if (!File.Exists(path))
            {
                errors.Add($"Missing staged file: {file.Path}");
                continue;
            }

            var actualHash = FileHash.Sha256File(path);
            if (!actualHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Hash mismatch for staged file: {file.Path}");
            }

            if (UpdateFileClassifier.IsSignable(file.Kind))
            {
                var signature = await signatureVerifier.VerifyAsync(path, file.AuthenticodePublisher, cancellationToken);
                if (!signature.IsTrusted)
                {
                    errors.Add($"Signature verification failed for staged file: {file.Path}: {signature.Reason}");
                }
            }
        }

        if (!manifest.Files.Any(file => file.Kind == UpdateFileKind.DependencyManifest))
        {
            errors.Add("Staged version is missing .deps.json runtime dependency metadata.");
        }

        if (!manifest.Files.Any(file => file.Kind == UpdateFileKind.RuntimeConfig))
        {
            errors.Add("Staged version is missing .runtimeconfig.json runtime configuration.");
        }

        foreach (var path in Directory.EnumerateFiles(stagedDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(stagedDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            if (expected.ContainsKey(relativePath))
            {
                continue;
            }

            var kind = UpdateFileClassifier.Classify(relativePath);
            if (UpdateFileClassifier.IsSignable(kind))
            {
                errors.Add($"Unexpected executable file in staged version: {relativePath}");
            }
        }

        return errors.Count == 0
            ? StagedVersionVerificationResult.Valid
            : new StagedVersionVerificationResult(false, errors);
    }
}
