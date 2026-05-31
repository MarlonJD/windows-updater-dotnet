namespace WindowsUpdater.Release;

public sealed record SemVersion(int Major, int Minor, int Patch, string? Prerelease = null)
    : IComparable<SemVersion>
{
    public static SemVersion Parse(string value)
    {
        var prereleaseParts = value.Split('-', 2, StringSplitOptions.TrimEntries);
        var versionParts = prereleaseParts[0].Split('.');
        if (versionParts.Length != 3
            || !int.TryParse(versionParts[0], out var major)
            || !int.TryParse(versionParts[1], out var minor)
            || !int.TryParse(versionParts[2], out var patch))
        {
            throw new FormatException($"Invalid SemVer value: {value}");
        }

        return new SemVersion(
            major,
            minor,
            patch,
            prereleaseParts.Length == 2 ? prereleaseParts[1] : null);
    }

    public override string ToString()
    {
        return Prerelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{Prerelease}";
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }

        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        if (Prerelease is null && other.Prerelease is null)
        {
            return 0;
        }

        if (Prerelease is null)
        {
            return 1;
        }

        if (other.Prerelease is null)
        {
            return -1;
        }

        return string.Compare(Prerelease, other.Prerelease, StringComparison.Ordinal);
    }
}
