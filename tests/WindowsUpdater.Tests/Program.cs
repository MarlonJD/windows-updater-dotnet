using WindowsUpdater;
using WindowsUpdater.Release;

namespace WindowsUpdater.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("manifest signatures reject tampering", ManifestSignaturesRejectTampering),
            ("release generator emits compressed changed payload metadata", ReleaseGeneratorEmitsCompressedChangedPayloadMetadata),
            ("release state allocates SemVer and build numbers", ReleaseStateAllocatesSemVerAndBuildNumbers),
            ("Conventional Commits generate scoped changelog draft", ConventionalCommitsGenerateScopedChangelogDraft),
            ("S3 CloudFront dry run uploads immutable release objects before latest", S3CloudFrontDryRunOrdersObjects),
            ("current version state falls back to last known good", CurrentVersionStateFallsBackToLastKnownGood)
        };

        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL {test.Name}");
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        return 0;
    }

    private static Task ManifestSignaturesRejectTampering()
    {
        var keys = ManifestSignatureService.CreateKeyPair("test-key");
        var signer = new ManifestSignatureService();
        var manifest = signer.Sign(
            new ReleaseManifest(
                "App",
                "stable",
                "win-x64",
                "1.0.0",
                100,
                "10.0.19041.0",
                "CN=Publisher",
                [new UpdateFileEntry("App.exe", "abc", 3, UpdateFileKind.Executable, true)]),
            keys.KeyId,
            keys.PrivateKey);

        Assert.True(
            signer.Verify(manifest, new Dictionary<string, string> { [keys.KeyId] = keys.PublicKey }),
            "Signed manifest should verify with the pinned public key.");
        Assert.False(
            signer.Verify(manifest with { Build = 101 }, new Dictionary<string, string> { [keys.KeyId] = keys.PublicKey }),
            "Changing signed content must invalidate the manifest signature.");
        return Task.CompletedTask;
    }

    private static async Task ReleaseGeneratorEmitsCompressedChangedPayloadMetadata()
    {
        using var fixture = ReleaseFixture.Create();
        var generator = new ReleaseManifestGenerator();
        var baseManifest = generator.Generate(fixture.BaseDirectory, Options("1.0.0", 100));
        var targetManifest = generator.Generate(fixture.TargetDirectory, Options("1.1.0", 110));
        var delta = await generator.GenerateDeltaAsync(
            baseManifest,
            targetManifest,
            fixture.TargetDirectory,
            fixture.OutputDirectory);

        var operations = delta.Operations.ToDictionary(operation => operation.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(DeltaOperationKind.CopyFromBase, operations["Core.dll"].Kind);
        Assert.Equal(DeltaOperationKind.DownloadFile, operations["App.exe"].Kind);
        Assert.Equal(DeltaOperationKind.DownloadFile, operations["WindowsUpdater.Launcher.exe"].Kind);
        Assert.Equal(DeltaOperationKind.DownloadFile, operations["WindowsUpdater.UpdateRunner.exe"].Kind);
        Assert.Equal(DeltaOperationKind.Delete, operations["old.config"].Kind);

        var payload = operations["App.exe"].Payload;
        Assert.NotNull(payload);
        Assert.Equal("gzip", payload!.Compression);
        Assert.True(File.Exists(Path.Combine(fixture.OutputDirectory, payload.PayloadPath)), "Compressed payload should be written.");
        Assert.Equal(operations["App.exe"].Sha256, payload.UncompressedSha256);
        Assert.True(payload.CompressedSize > 0, "Compressed payload size should be recorded.");
    }

    private static Task ReleaseStateAllocatesSemVerAndBuildNumbers()
    {
        var state = new ReleaseState(1847, "1.4.2");
        var stable = state.Allocate("1.4.3");
        var beta = stable.Allocate("1.5.0-beta.1");

        Assert.Equal(1848, stable.LastBuildNumber);
        Assert.Equal("1.4.3", stable.LastStableVersion);
        Assert.Equal(1849, beta.LastBuildNumber);
        Assert.Equal("1.5.0-beta.1", beta.LastBetaVersion);
        Assert.True(
            SemVersion.Parse("1.5.0").CompareTo(SemVersion.Parse("1.5.0-beta.1")) > 0,
            "Stable SemVer must sort after prerelease.");
        return Task.CompletedTask;
    }

    private static Task ConventionalCommitsGenerateScopedChangelogDraft()
    {
        var commits = new[]
        {
            ConventionalCommitParser.Parse("feat(release): add dry run planning"),
            ConventionalCommitParser.Parse("fix: reject stale builds"),
            ConventionalCommitParser.Parse("docs: update readme"),
            ConventionalCommitParser.Parse("refactor!: change manifest contract")
        }.Where(commit => commit is not null).OfType<ConventionalCommit>();

        var markdown = new ChangelogDraftGenerator()
            .Generate("1.1.0", commits)
            .ToMarkdown();

        Assert.Contains("release: add dry run planning", markdown);
        Assert.Contains("reject stale builds", markdown);
        Assert.Contains("BREAKING: change manifest contract", markdown);
        Assert.False(markdown.Contains("update readme", StringComparison.Ordinal), "Docs-only commits should not be included by default.");
        return Task.CompletedTask;
    }

    private static async Task S3CloudFrontDryRunOrdersObjects()
    {
        using var fixture = ReleaseFixture.Create();
        var generator = new ReleaseManifestGenerator();
        var baseManifest = generator.Generate(fixture.BaseDirectory, Options("1.0.0", 100));
        var targetManifest = generator.Generate(fixture.TargetDirectory, Options("1.1.0", 110));
        var delta = await generator.GenerateDeltaAsync(baseManifest, targetManifest, fixture.TargetDirectory, fixture.OutputDirectory);
        await ManifestJson.WriteAsync(Path.Combine(fixture.OutputDirectory, "target-file-manifest.json"), targetManifest);
        await ManifestJson.WriteAsync(Path.Combine(fixture.OutputDirectory, "delta-from-100-to-110.json"), delta);

        var plan = new S3CloudFrontPlanner().Plan(
            new S3CloudFrontPublishOptions(
                "updates-prod",
                "https://updates.example.com",
                "windows",
                "stable",
                "1.1.0+110"),
            fixture.OutputDirectory,
            targetManifest,
            [delta]);

        Assert.True(plan.Objects.Count >= 3, "Dry run should include payloads, manifests, deltas, and latest.");
        Assert.False(plan.Objects.Take(plan.Objects.Count - 1).Any(item => item.IsMutable), "Immutable release objects should upload before latest.");
        Assert.True(plan.Objects.Last().IsMutable, "latest.json should be planned last.");
        Assert.Contains("windows/stable/releases/1.1.0+110", plan.Objects.First().S3Key);
    }

    private static async Task CurrentVersionStateFallsBackToLastKnownGood()
    {
        using var fixture = ReleaseFixture.Create();
        var installRoot = Path.Combine(fixture.Root, "install");
        var versionDirectory = Path.Combine(installRoot, "versions", "1.0.0");
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllTextAsync(Path.Combine(versionDirectory, "App.exe"), "app");

        var store = new CurrentVersionStore(installRoot);
        var lastKnownGood = new CurrentVersionState(
            "1.0.0",
            100,
            "versions/1.0.0",
            "App.exe",
            "manifest",
            LastSuccessfulLaunchUtc: DateTimeOffset.UtcNow);
        await store.WriteAtomicAsync(lastKnownGood);
        await File.WriteAllTextAsync(store.StatePath, "{ broken json");

        var resolved = await store.ReadOrFallbackAsync();
        Assert.NotNull(resolved);
        Assert.Equal(100, resolved!.Build);
    }

    private static ReleaseManifestGenerationOptions Options(string version, long build)
    {
        return new ReleaseManifestGenerationOptions(
            "App",
            "stable",
            "win-x64",
            version,
            build,
            "10.0.19041.0",
            "CN=Publisher");
    }

    private sealed class ReleaseFixture : IDisposable
    {
        private ReleaseFixture(string root)
        {
            Root = root;
            BaseDirectory = Path.Combine(root, "base");
            TargetDirectory = Path.Combine(root, "target");
            OutputDirectory = Path.Combine(root, "output");
        }

        public string Root { get; }

        public string BaseDirectory { get; }

        public string TargetDirectory { get; }

        public string OutputDirectory { get; }

        public static ReleaseFixture Create()
        {
            var fixture = new ReleaseFixture(Path.Combine(Path.GetTempPath(), $"windows-updater-tests-{Guid.NewGuid():N}"));
            WriteRelease(fixture.BaseDirectory, "v1", includeOldFile: true);
            WriteRelease(fixture.TargetDirectory, "v2", includeOldFile: false);
            Directory.CreateDirectory(fixture.OutputDirectory);
            return fixture;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static void WriteRelease(string directory, string version, bool includeOldFile)
        {
            Write(directory, "App.exe", $"app-{version}");
            Write(directory, "Core.dll", "core-stable");
            Write(directory, "WindowsUpdater.Launcher.exe", $"launcher-{version}");
            Write(directory, "WindowsUpdater.UpdateRunner.exe", $"runner-{version}");
            Write(directory, "Resources/en-us/Resources.resw", $"strings-{version}");

            if (includeOldFile)
            {
                Write(directory, "old.config", "delete-me");
            }
        }

        private static void Write(string directory, string relativePath, string content)
        {
            var path = Path.Combine(directory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void False(bool condition, string message)
    {
        if (condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void NotNull(object? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected a non-null value.");
        }
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{actual}' to contain '{expected}'.");
        }
    }
}
