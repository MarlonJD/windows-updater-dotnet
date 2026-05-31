using System.Security.Cryptography;

namespace WindowsUpdater;

public sealed record UpdaterKeyPair(string KeyId, string PublicKey, string PrivateKey);

public sealed class ManifestSignatureService
{
    public const string Algorithm = "ECDSA_P256_SHA256";

    public static UpdaterKeyPair CreateKeyPair(string keyId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new UpdaterKeyPair(
            keyId,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
    }

    public ReleaseManifest Sign(ReleaseManifest manifest, string keyId, string privateKey)
    {
        var content = ManifestJson.CanonicalBytes(manifest);
        return manifest with { Signature = SignContent(content, keyId, privateKey) };
    }

    public DeltaManifest Sign(DeltaManifest manifest, string keyId, string privateKey)
    {
        var content = ManifestJson.CanonicalBytes(manifest);
        return manifest with { Signature = SignContent(content, keyId, privateKey) };
    }

    public LocalUpdateRequest Sign(LocalUpdateRequest request, string keyId, string privateKey)
    {
        var content = ManifestJson.CanonicalBytes(request);
        return request with { Signature = SignContent(content, keyId, privateKey) };
    }

    public bool Verify(ReleaseManifest manifest, IReadOnlyDictionary<string, string> publicKeys)
    {
        return manifest.Signature is not null
            && VerifyContent(ManifestJson.CanonicalBytes(manifest), manifest.Signature, publicKeys);
    }

    public bool Verify(DeltaManifest manifest, IReadOnlyDictionary<string, string> publicKeys)
    {
        return manifest.Signature is not null
            && VerifyContent(ManifestJson.CanonicalBytes(manifest), manifest.Signature, publicKeys);
    }

    public bool Verify(LocalUpdateRequest request, IReadOnlyDictionary<string, string> publicKeys)
    {
        return request.Signature is not null
            && VerifyContent(ManifestJson.CanonicalBytes(request), request.Signature, publicKeys);
    }

    public static string ContentHash(ReleaseManifest manifest)
    {
        return FileHash.Sha256Bytes(ManifestJson.CanonicalBytes(manifest));
    }

    public static string ContentHash(DeltaManifest manifest)
    {
        return FileHash.Sha256Bytes(ManifestJson.CanonicalBytes(manifest));
    }

    private static ManifestSignature SignContent(byte[] content, string keyId, string privateKey)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
        return new ManifestSignature(
            Algorithm,
            keyId,
            FileHash.Sha256Bytes(content),
            Convert.ToBase64String(key.SignData(content, HashAlgorithmName.SHA256)));
    }

    private static bool VerifyContent(
        byte[] content,
        ManifestSignature signature,
        IReadOnlyDictionary<string, string> publicKeys)
    {
        if (!signature.Algorithm.Equals(Algorithm, StringComparison.Ordinal)
            || !publicKeys.TryGetValue(signature.KeyId, out var publicKey)
            || !signature.ContentSha256.Equals(FileHash.Sha256Bytes(content), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        return key.VerifyData(content, Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256);
    }
}
