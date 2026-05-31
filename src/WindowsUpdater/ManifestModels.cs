namespace WindowsUpdater;

public enum UpdateFileKind
{
    Executable,
    DynamicLibrary,
    NativeMetadata,
    DependencyManifest,
    RuntimeConfig,
    PriResource,
    Resource,
    Manifest,
    Catalog,
    Other
}

public enum DeltaOperationKind
{
    CopyFromBase,
    DownloadFile,
    Delete,
    SetMetadata
}

public sealed record ManifestSignature(
    string Algorithm,
    string KeyId,
    string ContentSha256,
    string Signature);

public sealed record CompressedPayloadMetadata(
    string PayloadPath,
    string Compression,
    string CompressedSha256,
    long CompressedSize,
    string UncompressedSha256,
    long UncompressedSize);

public sealed record UpdateFileEntry(
    string Path,
    string Sha256,
    long Size,
    UpdateFileKind Kind,
    bool RequiredAtLaunch,
    string? AuthenticodePublisher = null,
    string? Catalog = null,
    string? RuntimeGroup = null);

public sealed record ReleaseManifest(
    string Product,
    string Channel,
    string Architecture,
    string Version,
    long Build,
    string MinimumWindowsVersion,
    string Publisher,
    IReadOnlyList<UpdateFileEntry> Files,
    ManifestSignature? Signature = null);

public sealed record DeltaOperation(
    DeltaOperationKind Kind,
    string Path,
    string? Sha256 = null,
    long? Size = null,
    CompressedPayloadMetadata? Payload = null,
    UpdateFileKind? FileKind = null);

public sealed record DeltaManifest(
    string Product,
    string Channel,
    string Architecture,
    long BaseBuild,
    long TargetBuild,
    string TargetVersion,
    string TargetManifestHash,
    IReadOnlyList<DeltaOperation> Operations,
    ManifestSignature? Signature = null);
