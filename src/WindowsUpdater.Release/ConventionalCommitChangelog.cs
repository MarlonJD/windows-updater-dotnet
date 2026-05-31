namespace WindowsUpdater.Release;

public sealed record ConventionalCommit(
    string Type,
    string? Scope,
    string Description,
    bool IsBreaking);

public sealed record ChangelogDraft(string Version, IReadOnlyList<string> Lines)
{
    public string ToMarkdown()
    {
        return $"## {Version}{Environment.NewLine}{Environment.NewLine}"
            + string.Join(Environment.NewLine, Lines.Select(line => $"- {line}"))
            + Environment.NewLine;
    }
}

public static class ConventionalCommitParser
{
    public static ConventionalCommit? Parse(string subject, string? body = null)
    {
        var separator = subject.IndexOf(':', StringComparison.Ordinal);
        if (separator < 1)
        {
            return null;
        }

        var header = subject[..separator];
        var description = subject[(separator + 1)..].Trim();
        var isBreaking = header.EndsWith('!')
            || (body?.Contains("BREAKING CHANGE", StringComparison.OrdinalIgnoreCase) ?? false);
        if (header.EndsWith('!'))
        {
            header = header[..^1];
        }

        string type;
        string? scope = null;
        var scopeStart = header.IndexOf('(', StringComparison.Ordinal);
        if (scopeStart >= 0 && header.EndsWith(')'))
        {
            type = header[..scopeStart];
            scope = header[(scopeStart + 1)..^1];
        }
        else
        {
            type = header;
        }

        return string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(description)
            ? null
            : new ConventionalCommit(type, scope, description, isBreaking);
    }
}

public sealed class ChangelogDraftGenerator
{
    private static readonly HashSet<string> IncludedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "feat",
        "fix",
        "perf",
        "security"
    };

    public ChangelogDraft Generate(string version, IEnumerable<ConventionalCommit> commits)
    {
        var lines = commits
            .Where(commit => commit.IsBreaking || IncludedTypes.Contains(commit.Type))
            .Select(commit =>
            {
                var scope = commit.Scope is null ? string.Empty : $"{commit.Scope}: ";
                var marker = commit.IsBreaking ? "BREAKING: " : string.Empty;
                return $"{marker}{scope}{commit.Description}";
            })
            .ToArray();

        return new ChangelogDraft(version, lines);
    }
}
