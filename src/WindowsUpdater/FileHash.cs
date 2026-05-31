using System.Security.Cryptography;

namespace WindowsUpdater;

public static class FileHash
{
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256Bytes(ReadOnlySpan<byte> content)
    {
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }
}
