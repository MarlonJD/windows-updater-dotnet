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
        var payloadPath = $"payload/{target.Sha256}.gz";
        var outputPath = Path.Combine(outputDirectory, payloadPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? outputDirectory);

        await using (var source = File.OpenRead(sourcePath))
        await using (var destination = File.Create(outputPath))
        await using (var gzip = new GZipStream(destination, CompressionLevel.SmallestSize))
        {
            await source.CopyToAsync(gzip, cancellationToken);
        }

        var output = new FileInfo(outputPath);
        return new CompressedPayloadMetadata(
            payloadPath,
            "gzip",
            FileHash.Sha256File(outputPath),
            output.Length,
            target.Sha256,
            target.Size);
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
