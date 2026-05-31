using WindowsUpdater;

var arguments = CliArguments.Parse(args);
if (!arguments.TryGetValue("install-root", out var installRoot)
    || !arguments.TryGetValue("request", out var requestPath)
    || !arguments.TryGetValue("public-key-id", out var publicKeyId)
    || !arguments.TryGetValue("public-key", out var publicKey))
{
    Console.Error.WriteLine("Usage: WindowsUpdater.UpdateRunner --install-root <path> --request <path> --public-key-id <id> --public-key <base64>");
    return 2;
}

var request = await ManifestJson.ReadAsync<LocalUpdateRequest>(requestPath);
var runner = new UpdateRunnerCore(
    new CurrentVersionStore(installRoot),
    new Dictionary<string, string> { [publicKeyId] = publicKey });
var result = await runner.SwitchAsync(request);

if (result.Status == UpdateStatus.Installed)
{
    Console.WriteLine("Installed");
    return 0;
}

Console.Error.WriteLine(result.Reason ?? "Update failed.");
return 1;

internal static class CliArguments
{
    public static Dictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length - 1; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            values[args[index][2..]] = args[index + 1];
        }

        return values;
    }
}
