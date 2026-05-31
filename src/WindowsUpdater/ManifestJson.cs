using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsUpdater;

public static class ManifestJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonSerializerOptions CompactOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] CanonicalBytes(ReleaseManifest manifest)
    {
        var unsigned = manifest with
        {
            Signature = null,
            Files = manifest.Files
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

        return JsonSerializer.SerializeToUtf8Bytes(unsigned, CompactOptions);
    }

    public static byte[] CanonicalBytes(DeltaManifest manifest)
    {
        var unsigned = manifest with
        {
            Signature = null,
            Operations = manifest.Operations
                .OrderBy(operation => operation.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(operation => operation.Kind)
                .ToArray()
        };

        return JsonSerializer.SerializeToUtf8Bytes(unsigned, CompactOptions);
    }

    public static byte[] CanonicalBytes(LocalUpdateRequest request)
    {
        var unsigned = request with { Signature = null };
        return JsonSerializer.SerializeToUtf8Bytes(unsigned, CompactOptions);
    }

    public static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken)
            ?? throw new InvalidOperationException($"Unable to read JSON file '{path}'.");
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
