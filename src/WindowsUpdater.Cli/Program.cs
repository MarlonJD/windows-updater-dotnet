using WindowsUpdater;
using WindowsUpdater.Release;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

var command = args[0];
var arguments = CliArguments.Parse(args.Skip(1).ToArray());

try
{
    return command switch
    {
        "generate" => await GenerateAsync(arguments),
        "dry-run" => await DryRunAsync(arguments),
        "changelog" => GenerateChangelog(arguments),
        "allocate" => await AllocateAsync(arguments),
        _ => Unknown(command)
    };
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static async Task<int> GenerateAsync(IReadOnlyDictionary<string, string> arguments)
{
    if (!Required(arguments, "release-dir", "output-dir", "channel", "architecture", "version", "build", "publisher"))
    {
        PrintUsage();
        return 2;
    }

    var generator = new ReleaseManifestGenerator();
    var manifest = generator.Generate(
        arguments["release-dir"],
        new ReleaseManifestGenerationOptions(
            arguments.GetValueOrDefault("product") ?? "WindowsApp",
            arguments["channel"],
            arguments["architecture"],
            arguments["version"],
            long.Parse(arguments["build"]),
            arguments.GetValueOrDefault("minimum-windows-version") ?? "10.0.19041.0",
            arguments["publisher"],
            RequiredFileNames(arguments)));

    var signer = new ManifestSignatureService();
    if (arguments.TryGetValue("key-id", out var keyId) && arguments.TryGetValue("private-key", out var privateKey))
    {
        manifest = signer.Sign(manifest, keyId, privateKey);
    }

    var outputDirectory = arguments["output-dir"];
    var deltas = new List<DeltaManifest>();
    var manifestPath = Path.Combine(outputDirectory, "target-file-manifest.json");
    await ManifestJson.WriteAsync(manifestPath, manifest);
    Console.WriteLine(manifestPath);

    var fullArchive = await generator.GenerateFullArchiveAsync(arguments["release-dir"], outputDirectory);
    Console.WriteLine(Path.Combine(outputDirectory, fullArchive.ArchivePath));

    if (arguments.TryGetValue("base-manifest", out var baseManifestPath))
    {
        var baseManifest = await ManifestJson.ReadAsync<ReleaseManifest>(baseManifestPath);
        var delta = await generator.GenerateDeltaAsync(baseManifest, manifest, arguments["release-dir"], outputDirectory);
        if (arguments.TryGetValue("key-id", out keyId) && arguments.TryGetValue("private-key", out privateKey))
        {
            delta = signer.Sign(delta, keyId, privateKey);
        }

        var deltaPath = Path.Combine(outputDirectory, $"delta-from-{delta.BaseBuild}-to-{delta.TargetBuild}.json");
        await ManifestJson.WriteAsync(deltaPath, delta);
        deltas.Add(delta);
        Console.WriteLine(deltaPath);
    }

    var changelogMarkdown = arguments.TryGetValue("changelog", out var changelogPath) && File.Exists(changelogPath)
        ? await File.ReadAllTextAsync(changelogPath)
        : null;
    var releaseMetadata = generator.GenerateReleaseMetadata(
        manifest,
        fullArchive,
        deltas,
        arguments.GetValueOrDefault("commit") ?? "unknown",
        changelogMarkdown);
    if (arguments.TryGetValue("key-id", out keyId) && arguments.TryGetValue("private-key", out privateKey))
    {
        releaseMetadata = signer.Sign(releaseMetadata, keyId, privateKey);
    }

    var releasePath = Path.Combine(outputDirectory, "release.json");
    await ManifestJson.WriteAsync(releasePath, releaseMetadata);
    Console.WriteLine(releasePath);

    return 0;
}

static async Task<int> DryRunAsync(IReadOnlyDictionary<string, string> arguments)
{
    if (!Required(arguments, "manifest", "bucket", "cloudfront", "platform", "channel"))
    {
        PrintUsage();
        return 2;
    }

    var manifest = await ManifestJson.ReadAsync<ReleaseManifest>(arguments["manifest"]);
    var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(arguments["manifest"])) ?? ".";
    var deltas = Directory.EnumerateFiles(outputDirectory, "delta-from-*-to-*.json")
        .Select(path => ManifestJson.ReadAsync<DeltaManifest>(path).GetAwaiter().GetResult())
        .ToArray();
    var releaseId = $"{manifest.Version}+{manifest.Build}";
    var plan = new S3CloudFrontPlanner().Plan(
        new S3CloudFrontPublishOptions(
            arguments["bucket"],
            arguments["cloudfront"],
            arguments["platform"],
            arguments["channel"],
            releaseId),
        outputDirectory,
        manifest,
        deltas);

    Console.WriteLine(plan.ToText());
    return 0;
}

static int GenerateChangelog(IReadOnlyDictionary<string, string> arguments)
{
    if (!arguments.TryGetValue("version", out var version)
        || !arguments.TryGetValue("commits", out var commits))
    {
        PrintUsage();
        return 2;
    }

    var parsed = commits.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(subject => ConventionalCommitParser.Parse(subject))
        .Where(commit => commit is not null)
        .OfType<ConventionalCommit>();
    Console.WriteLine(new ChangelogDraftGenerator().Generate(version, parsed).ToMarkdown());
    return 0;
}

static async Task<int> AllocateAsync(IReadOnlyDictionary<string, string> arguments)
{
    if (!Required(arguments, "state", "version"))
    {
        PrintUsage();
        return 2;
    }

    var state = await ReleaseStateStore.ReadAsync(arguments["state"]);
    var next = state.Allocate(arguments["version"]);
    await ReleaseStateStore.WriteAsync(arguments["state"], next);
    Console.WriteLine($"{arguments["version"]}+{next.LastBuildNumber}");
    return 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintUsage();
    return 2;
}

static bool Required(IReadOnlyDictionary<string, string> arguments, params string[] names)
{
    return names.All(arguments.ContainsKey);
}

static IReadOnlyList<string>? RequiredFileNames(IReadOnlyDictionary<string, string> arguments)
{
    return arguments.TryGetValue("required-files", out var value)
        ? value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : null;
}

static void PrintUsage()
{
    Console.WriteLine("windows-updater-release generate --release-dir <path> --output-dir <path> --channel <name> --architecture <rid> --version <semver> --build <number> --publisher <subject>");
    Console.WriteLine("windows-updater-release dry-run --manifest <path> --bucket <name> --cloudfront <url> --platform windows --channel stable");
    Console.WriteLine("windows-updater-release changelog --version <semver> --commits \"feat: one|fix: two\"");
    Console.WriteLine("windows-updater-release allocate --state release/desktop-release-state.json --version <semver>");
}

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
