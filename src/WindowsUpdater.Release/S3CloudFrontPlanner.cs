using WindowsUpdater;

namespace WindowsUpdater.Release;

public sealed record S3CloudFrontPublishOptions(
    string Bucket,
    string CloudFrontBaseUrl,
    string Platform,
    string Channel,
    string ReleaseId);

public sealed record PublishObjectPlan(
    string LocalPath,
    string S3Key,
    string CloudFrontUrl,
    bool IsMutable,
    string? Sha256 = null,
    long? Size = null);

public sealed record PublishDryRunPlan(
    string ReleaseId,
    int ChangedFileCount,
    long CompressedPayloadBytes,
    long FullArchiveBytes,
    IReadOnlyList<PublishObjectPlan> Objects)
{
    public string ToText()
    {
        var lines = new List<string>
        {
            $"Release: {ReleaseId}",
            $"Changed files: {ChangedFileCount}",
            $"Compressed payload bytes: {CompressedPayloadBytes}",
            $"Full archive bytes: {FullArchiveBytes}"
        };
        lines.AddRange(Objects.Select(item =>
        {
            var hash = string.IsNullOrWhiteSpace(item.Sha256) ? string.Empty : $" sha256={item.Sha256}";
            var size = item.Size is null ? string.Empty : $" bytes={item.Size}";
            return $"{(item.IsMutable ? "MUTABLE" : "IMMUTABLE")} s3://{item.S3Key} <- {item.LocalPath}{hash}{size}";
        }));

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class S3CloudFrontPlanner
{
    public PublishDryRunPlan Plan(
        S3CloudFrontPublishOptions options,
        string outputDirectory,
        ReleaseManifest targetManifest,
        IReadOnlyList<DeltaManifest> deltas)
    {
        var prefix = $"{options.Platform}/{options.Channel}/releases/{options.ReleaseId}";
        var baseUrl = options.CloudFrontBaseUrl.TrimEnd('/');
        var objects = new List<PublishObjectPlan>();
        var payloads = deltas
            .SelectMany(delta => delta.Operations)
            .Select(operation => operation.Payload)
            .Where(payload => payload is not null)
            .OfType<CompressedPayloadMetadata>()
            .DistinctBy(payload => payload.CompressedSha256)
            .OrderBy(payload => payload.PayloadPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var payload in payloads)
        {
            objects.Add(PlanObject(
                options,
                Path.Combine(outputDirectory, payload.PayloadPath),
                $"{prefix}/{payload.PayloadPath}",
                $"{baseUrl}/{prefix}/{payload.PayloadPath}",
                isMutable: false,
                payload.CompressedSha256,
                payload.CompressedSize));
        }

        var archives = Directory.Exists(Path.Combine(outputDirectory, "archives"))
            ? Directory.EnumerateFiles(Path.Combine(outputDirectory, "archives"), "*.zip", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        foreach (var archive in archives)
        {
            var relative = Path.GetRelativePath(outputDirectory, archive)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            objects.Add(PlanObject(
                options,
                archive,
                $"{prefix}/{relative}",
                $"{baseUrl}/{prefix}/{relative}",
                isMutable: false,
                FileHash.Sha256File(archive),
                new FileInfo(archive).Length));
        }

        objects.Add(PlanObject(
            options,
            Path.Combine(outputDirectory, "target-file-manifest.json"),
            $"{prefix}/target-file-manifest.json",
            $"{baseUrl}/{prefix}/target-file-manifest.json",
            isMutable: false,
            ManifestSignatureService.ContentHash(targetManifest)));

        foreach (var delta in deltas.OrderBy(delta => delta.BaseBuild))
        {
            var fileName = $"delta-from-{delta.BaseBuild}-to-{delta.TargetBuild}.json";
            objects.Add(PlanObject(
                options,
                Path.Combine(outputDirectory, fileName),
                $"{prefix}/{fileName}",
                $"{baseUrl}/{prefix}/{fileName}",
                isMutable: false,
                ManifestSignatureService.ContentHash(delta)));
        }

        var releasePath = Path.Combine(outputDirectory, "release.json");
        if (File.Exists(releasePath))
        {
            objects.Add(PlanObject(
                options,
                releasePath,
                $"{prefix}/release.json",
                $"{baseUrl}/{prefix}/release.json",
                isMutable: false,
                FileHash.Sha256File(releasePath),
                new FileInfo(releasePath).Length));
        }

        objects.Add(PlanObject(
            options,
            Path.Combine(outputDirectory, "latest.json"),
            $"{options.Platform}/{options.Channel}/latest.json",
            $"{baseUrl}/{options.Platform}/{options.Channel}/latest.json",
            isMutable: true));

        return new PublishDryRunPlan(
            options.ReleaseId,
            payloads.Length,
            payloads.Sum(payload => payload.CompressedSize),
            archives.Sum(archive => new FileInfo(archive).Length),
            objects);
    }

    private static PublishObjectPlan PlanObject(
        S3CloudFrontPublishOptions options,
        string localPath,
        string keyWithoutBucket,
        string url,
        bool isMutable,
        string? sha256 = null,
        long? size = null)
    {
        return new PublishObjectPlan(
            localPath,
            $"{options.Bucket.TrimEnd('/')}/{keyWithoutBucket}",
            url,
            isMutable,
            sha256,
            size);
    }
}
