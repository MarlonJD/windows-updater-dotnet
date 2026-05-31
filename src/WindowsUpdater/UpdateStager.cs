using System.IO.Compression;

namespace WindowsUpdater;

public sealed record UpdateStagingResult(
    UpdateStatus Status,
    IReadOnlyList<string> DownloadedFiles,
    string StagedDirectory,
    bool UsedFullArchive);

public interface IUpdatePayloadSource
{
    Task<Stream> OpenPayloadAsync(DeltaOperation operation, CancellationToken cancellationToken = default);
}

public sealed class DirectoryPayloadSource : IUpdatePayloadSource
{
    private readonly string releaseDirectory;

    public DirectoryPayloadSource(string releaseDirectory)
    {
        this.releaseDirectory = releaseDirectory;
    }

    public Task<Stream> OpenPayloadAsync(DeltaOperation operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(File.OpenRead(Path.Combine(releaseDirectory, operation.Path)));
    }
}

public sealed class CompressedFilePayloadSource : IUpdatePayloadSource
{
    private readonly string payloadRoot;

    public CompressedFilePayloadSource(string payloadRoot)
    {
        this.payloadRoot = payloadRoot;
    }

    public Task<Stream> OpenPayloadAsync(DeltaOperation operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (operation.Payload is null)
        {
            throw new InvalidOperationException($"Download operation for '{operation.Path}' does not declare payload metadata.");
        }

        var path = Path.Combine(payloadRoot, operation.Payload.PayloadPath);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Compressed payload is missing.", path);
        }

        if (info.Length != operation.Payload.CompressedSize)
        {
            throw new InvalidOperationException($"Compressed payload size mismatch: {operation.Payload.PayloadPath}");
        }

        var compressedHash = FileHash.Sha256File(path);
        if (!compressedHash.Equals(operation.Payload.CompressedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Compressed payload hash mismatch: {operation.Payload.PayloadPath}");
        }

        Stream stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        return Task.FromResult(stream);
    }
}

public sealed class UpdateStager
{
    private readonly StagedVersionVerifier verifier;

    public UpdateStager(StagedVersionVerifier? verifier = null)
    {
        this.verifier = verifier ?? new StagedVersionVerifier();
    }

    public async Task<UpdateStagingResult> StageDeltaAsync(
        string activeVersionDirectory,
        string stagedDirectory,
        DeltaManifest delta,
        ReleaseManifest targetManifest,
        IUpdatePayloadSource payloadSource,
        CancellationToken cancellationToken = default)
    {
        PrepareStagingDirectory(stagedDirectory);
        var downloaded = new List<string>();

        foreach (var operation in delta.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (operation.Kind)
            {
                case DeltaOperationKind.CopyFromBase:
                    await CopyFromBaseAsync(activeVersionDirectory, stagedDirectory, operation, cancellationToken);
                    break;
                case DeltaOperationKind.DownloadFile:
                    if (!IsExistingFileValid(stagedDirectory, operation))
                    {
                        await DownloadAsync(stagedDirectory, operation, payloadSource, cancellationToken);
                        downloaded.Add(operation.Path);
                    }

                    break;
                case DeltaOperationKind.Delete:
                    DeleteFromStaging(stagedDirectory, operation.Path);
                    break;
                case DeltaOperationKind.SetMetadata:
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported delta operation '{operation.Kind}'.");
            }
        }

        await VerifyOrThrowAsync(stagedDirectory, targetManifest, cancellationToken);
        return new UpdateStagingResult(UpdateStatus.Staged, downloaded, stagedDirectory, UsedFullArchive: false);
    }

    public async Task<UpdateStagingResult> StageFullArchiveAsync(
        string archivePath,
        FullArchiveMetadata archive,
        string stagedDirectory,
        ReleaseManifest targetManifest,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(archivePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Full fallback archive is missing.", archivePath);
        }

        if (info.Length != archive.Size)
        {
            throw new InvalidOperationException($"Full archive size mismatch: {archive.ArchivePath}");
        }

        var archiveHash = FileHash.Sha256File(archivePath);
        if (!archiveHash.Equals(archive.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Full archive hash mismatch: {archive.ArchivePath}");
        }

        PrepareStagingDirectory(stagedDirectory);
        ZipFile.ExtractToDirectory(archivePath, stagedDirectory, overwriteFiles: true);
        await VerifyOrThrowAsync(stagedDirectory, targetManifest, cancellationToken);

        return new UpdateStagingResult(
            UpdateStatus.Staged,
            targetManifest.Files.Select(file => file.Path).ToArray(),
            stagedDirectory,
            UsedFullArchive: true);
    }

    private static async Task CopyFromBaseAsync(
        string activeVersionDirectory,
        string stagedDirectory,
        DeltaOperation operation,
        CancellationToken cancellationToken)
    {
        if (IsExistingFileValid(stagedDirectory, operation))
        {
            return;
        }

        var source = Path.Combine(activeVersionDirectory, operation.Path);
        var destination = Path.Combine(stagedDirectory, operation.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? stagedDirectory);

        await using var input = File.OpenRead(source);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task DownloadAsync(
        string stagedDirectory,
        DeltaOperation operation,
        IUpdatePayloadSource payloadSource,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(stagedDirectory, operation.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? stagedDirectory);

        await using (var input = await payloadSource.OpenPayloadAsync(operation, cancellationToken))
        await using (var output = File.Create(destination))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        if (!IsExistingFileValid(stagedDirectory, operation))
        {
            throw new InvalidOperationException($"Downloaded payload failed hash validation: {operation.Path}");
        }
    }

    private async Task VerifyOrThrowAsync(
        string stagedDirectory,
        ReleaseManifest targetManifest,
        CancellationToken cancellationToken)
    {
        var verification = await verifier.VerifyAsync(stagedDirectory, targetManifest, cancellationToken);
        if (!verification.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, verification.Errors));
        }
    }

    private static bool IsExistingFileValid(string directory, DeltaOperation operation)
    {
        if (operation.Sha256 is null)
        {
            return false;
        }

        var path = Path.Combine(directory, operation.Path);
        return File.Exists(path)
            && FileHash.Sha256File(path).Equals(operation.Sha256, StringComparison.OrdinalIgnoreCase)
            && (operation.Size is null || new FileInfo(path).Length == operation.Size.Value);
    }

    private static void DeleteFromStaging(string stagedDirectory, string relativePath)
    {
        var path = Path.Combine(stagedDirectory, relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void PrepareStagingDirectory(string stagedDirectory)
    {
        if (Directory.Exists(stagedDirectory))
        {
            Directory.Delete(stagedDirectory, recursive: true);
        }

        Directory.CreateDirectory(stagedDirectory);
    }
}
