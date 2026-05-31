using System.IO.Compression;
using WindowsUpdater;

namespace WindowsUpdater.Release;

public sealed record ReleaseManifestGenerationOptions(
    string Product,
    string Channel,
    string Architecture,
    string Version,
    long Build,
    string MinimumWindowsVersion,
    string Publisher,
    IReadOnlyList<string>? RequiredFileNames = null);

public sealed record ReleaseManifestValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ReleaseManifestValidationResult Valid { get; } = new(true, []);
}

public sealed class ReleaseManifestGenerator
{
    private static readonly string[] DefaultRequiredFileNames =
    [
        "WindowsUpdater.Launcher.exe",
        "WindowsUpdater.UpdateRunner.exe"
    ];

    public ReleaseManifest Generate(string releaseDirectory, ReleaseManifestGenerationOptions options)
    {
        if (!Directory.Exists(releaseDirectory))
        {
            throw new DirectoryNotFoundException(releaseDirectory);
        }

        var root = Path.GetFullPath(releaseDirectory);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => CreateEntry(root, path, options.Publisher))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var manifest = new ReleaseManifest(
            options.Product,
            options.Channel,
            options.Architecture,
            options.Version,
            options.Build,
            options.MinimumWindowsVersion,
            options.Publisher,
            files);

        var validation = Validate(manifest, options.RequiredFileNames);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));
        }

        return manifest;
    }

    public ReleaseManifestValidationResult Validate(
        ReleaseManifest manifest,
        IReadOnlyList<string>? requiredFileNames = null)
    {
        var errors = new List<string>();
        var files = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var requiredFile in requiredFileNames ?? DefaultRequiredFileNames)
        {
            if (!files.ContainsKey(requiredFile))
            {
                errors.Add($"Required release file is missing: {requiredFile}");
            }
        }

        if (!manifest.Files.Any(file => file.Kind == UpdateFileKind.DependencyManifest))
        {
            errors.Add("Release manifest must include at least one .deps.json file.");
        }

        if (!manifest.Files.Any(file => file.Kind == UpdateFileKind.RuntimeConfig))
        {
            errors.Add("Release manifest must include at least one .runtimeconfig.json file.");
        }

        return errors.Count == 0
            ? ReleaseManifestValidationResult.Valid
            : new ReleaseManifestValidationResult(false, errors);
    }

    public async Task<DeltaManifest> GenerateDeltaAsync(
        ReleaseManifest baseManifest,
        ReleaseManifest targetManifest,
        string targetReleaseDirectory,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!baseManifest.Product.Equals(targetManifest.Product, StringComparison.Ordinal)
            || !baseManifest.Channel.Equals(targetManifest.Channel, StringComparison.OrdinalIgnoreCase)
            || !baseManifest.Architecture.Equals(targetManifest.Architecture, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Base and target manifests must share product, channel, and architecture.");
        }

        if (targetManifest.Build <= baseManifest.Build)
        {
            throw new InvalidOperationException("Target build must be newer than the base build.");
        }

        var baseFiles = baseManifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var targetFiles = targetManifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var operations = new List<DeltaOperation>();

        foreach (var target in targetManifest.Files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (baseFiles.TryGetValue(target.Path, out var existing)
                && existing.Sha256.Equals(target.Sha256, StringComparison.OrdinalIgnoreCase)
                && existing.Size == target.Size)
            {
                operations.Add(new DeltaOperation(
                    DeltaOperationKind.CopyFromBase,
                    target.Path,
                    target.Sha256,
                    target.Size,
                    FileKind: target.Kind));
                continue;
            }

            var payload = await WritePayloadAsync(targetReleaseDirectory, outputDirectory, target, cancellationToken);
            operations.Add(new DeltaOperation(
                DeltaOperationKind.DownloadFile,
                target.Path,
                target.Sha256,
                target.Size,
                payload,
                target.Kind));
        }

        foreach (var deleted in baseManifest.Files.Where(file => !targetFiles.ContainsKey(file.Path)))
        {
            operations.Add(new DeltaOperation(DeltaOperationKind.Delete, deleted.Path));
        }

        return new DeltaManifest(
            targetManifest.Product,
            targetManifest.Channel,
            targetManifest.Architecture,
            baseManifest.Build,
            targetManifest.Build,
            targetManifest.Version,
            ManifestSignatureService.ContentHash(targetManifest),
            operations.OrderBy(operation => operation.Path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<FullArchiveMetadata> GenerateFullArchiveAsync(
        string targetReleaseDirectory,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(targetReleaseDirectory))
        {
            throw new DirectoryNotFoundException(targetReleaseDirectory);
        }

        Directory.CreateDirectory(outputDirectory);
        var tempPath = Path.Combine(outputDirectory, $"full-{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(
            targetReleaseDirectory,
            tempPath,
            CompressionLevel.SmallestSize,
            includeBaseDirectory: false);

        cancellationToken.ThrowIfCancellationRequested();
        var hash = FileHash.Sha256File(tempPath);
        var archivePath = ContentAddressedPath("archives", hash, ".zip");
        var outputPath = Path.Combine(outputDirectory, archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? outputDirectory);

        if (File.Exists(outputPath))
        {
            File.Delete(tempPath);
        }
        else
        {
            File.Move(tempPath, outputPath);
        }

        var output = new FileInfo(outputPath);
        await Task.CompletedTask;
        return new FullArchiveMetadata(archivePath, "zip", hash, output.Length);
    }

    public ReleaseMetadata GenerateReleaseMetadata(
        ReleaseManifest targetManifest,
        FullArchiveMetadata fullArchive,
        IReadOnlyList<DeltaManifest> deltas,
        string commit,
        string? changelogMarkdown = null,
        DateTimeOffset? publishedAtUtc = null)
    {
        var releaseId = $"{targetManifest.Version}+{targetManifest.Build}";
        return new ReleaseMetadata(
            targetManifest.Product,
            targetManifest.Channel,
            targetManifest.Architecture,
            targetManifest.Version,
            targetManifest.Build,
            releaseId,
            commit,
            "target-file-manifest.json",
            ManifestSignatureService.ContentHash(targetManifest),
            fullArchive,
            deltas
                .OrderBy(delta => delta.BaseBuild)
                .Select(delta =>
                {
                    var path = $"delta-from-{delta.BaseBuild}-to-{delta.TargetBuild}.json";
                    return new ReleaseDeltaMetadata(
                        delta.BaseBuild,
                        delta.TargetBuild,
                        path,
                        ManifestSignatureService.ContentHash(delta),
                        EstimateJsonSize(delta));
                })
                .ToArray(),
            changelogMarkdown,
            publishedAtUtc);
    }

    private static UpdateFileEntry CreateEntry(string root, string path, string publisher)
    {
        var relativePath = Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        var kind = UpdateFileClassifier.Classify(relativePath);
        var info = new FileInfo(path);
        return new UpdateFileEntry(
            relativePath,
            FileHash.Sha256File(path),
            info.Length,
            kind,
            UpdateFileClassifier.IsRequiredAtLaunch(kind, relativePath),
            kind is UpdateFileKind.Executable or UpdateFileKind.DynamicLibrary ? publisher : null,
            RuntimeGroup: RuntimeGroupFor(kind, relativePath));
    }

    private static async Task<CompressedPayloadMetadata> WritePayloadAsync(
        string targetReleaseDirectory,
        string outputDirectory,
        UpdateFileEntry target,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.Combine(targetReleaseDirectory, target.Path);
        Directory.CreateDirectory(outputDirectory);
        var tempPath = Path.Combine(outputDirectory, $"payload-{Guid.NewGuid():N}.gz");

        await using (var source = File.OpenRead(sourcePath))
        await using (var destination = File.Create(tempPath))
        await using (var gzip = new GZipStream(destination, CompressionLevel.SmallestSize))
        {
            await source.CopyToAsync(gzip, cancellationToken);
        }

        var compressedSha256 = FileHash.Sha256File(tempPath);
        var payloadPath = ContentAddressedPath("payloads", compressedSha256, ".gz");
        var outputPath = Path.Combine(outputDirectory, payloadPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? outputDirectory);
        if (File.Exists(outputPath))
        {
            File.Delete(tempPath);
        }
        else
        {
            File.Move(tempPath, outputPath);
        }

        var output = new FileInfo(outputPath);
        return new CompressedPayloadMetadata(
            payloadPath,
            "gzip",
            compressedSha256,
            output.Length,
            target.Sha256,
            target.Size);
    }

    private static string ContentAddressedPath(string root, string sha256, string extension)
    {
        return $"{root}/{sha256[..2]}/{sha256.Substring(2, 2)}/{sha256}{extension}";
    }

    private static long EstimateJsonSize(DeltaManifest value)
    {
        return ManifestJson.CanonicalBytes(value).LongLength;
    }

    private static string? RuntimeGroupFor(UpdateFileKind kind, string relativePath)
    {
        if (relativePath.Contains("Microsoft.WindowsAppRuntime", StringComparison.OrdinalIgnoreCase))
        {
            return "windows-app-sdk";
        }

        return kind is UpdateFileKind.DependencyManifest or UpdateFileKind.RuntimeConfig
            ? "dotnet"
            : null;
    }
}
