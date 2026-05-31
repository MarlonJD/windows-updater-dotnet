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
    bool IsMutable);

public sealed record PublishDryRunPlan(IReadOnlyList<PublishObjectPlan> Objects)
{
    public string ToText()
    {
        return string.Join(
            Environment.NewLine,
            Objects.Select(item => $"{(item.IsMutable ? "MUTABLE" : "IMMUTABLE")} s3://{item.S3Key} <- {item.LocalPath}"));
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

        foreach (var payload in deltas
            .SelectMany(delta => delta.Operations)
            .Select(operation => operation.Payload)
            .Where(payload => payload is not null)
            .OfType<CompressedPayloadMetadata>()
            .OrderBy(payload => payload.PayloadPath, StringComparer.OrdinalIgnoreCase))
        {
            objects.Add(PlanObject(
                options,
                Path.Combine(outputDirectory, payload.PayloadPath),
                $"{prefix}/{payload.PayloadPath}",
                $"{baseUrl}/{prefix}/{payload.PayloadPath}",
                isMutable: false));
        }

        objects.Add(PlanObject(
            options,
            Path.Combine(outputDirectory, "target-file-manifest.json"),
            $"{prefix}/target-file-manifest.json",
            $"{baseUrl}/{prefix}/target-file-manifest.json",
            isMutable: false));

        foreach (var delta in deltas.OrderBy(delta => delta.BaseBuild))
        {
            var fileName = $"delta-from-{delta.BaseBuild}-to-{delta.TargetBuild}.json";
            objects.Add(PlanObject(
                options,
                Path.Combine(outputDirectory, fileName),
                $"{prefix}/{fileName}",
                $"{baseUrl}/{prefix}/{fileName}",
                isMutable: false));
        }

        objects.Add(PlanObject(
            options,
            Path.Combine(outputDirectory, "latest.json"),
            $"{options.Platform}/{options.Channel}/latest.json",
            $"{baseUrl}/{options.Platform}/{options.Channel}/latest.json",
            isMutable: true));

        return new PublishDryRunPlan(objects);
    }

    private static PublishObjectPlan PlanObject(
        S3CloudFrontPublishOptions options,
        string localPath,
        string keyWithoutBucket,
        string url,
        bool isMutable)
    {
        return new PublishObjectPlan(
            localPath,
            $"{options.Bucket.TrimEnd('/')}/{keyWithoutBucket}",
            url,
            isMutable);
    }
}
