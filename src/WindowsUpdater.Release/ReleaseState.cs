using System.Text.Json;

namespace WindowsUpdater.Release;

public sealed record ReleaseState(
    long LastBuildNumber,
    string LastStableVersion,
    string? LastBetaVersion = null)
{
    public ReleaseState Allocate(string nextVersion)
    {
        var semVersion = SemVersion.Parse(nextVersion);
        return semVersion.Prerelease is null
            ? this with
            {
                LastBuildNumber = LastBuildNumber + 1,
                LastStableVersion = nextVersion
            }
            : this with
            {
                LastBuildNumber = LastBuildNumber + 1,
                LastBetaVersion = nextVersion
            };
    }
}

public static class ReleaseStateStore
{
    public static async Task<ReleaseState> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ReleaseState>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken) ?? throw new InvalidOperationException($"Unable to read release state: {path}");
    }

    public static async Task WriteAsync(
        string path,
        ReleaseState state,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream,
            state,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true },
            cancellationToken);
    }
}
