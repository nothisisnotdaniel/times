using System.Text.RegularExpressions;

namespace Times;

/// <summary>
/// A strict SemVer 2.0 version (<c>MAJOR.MINOR.PATCH[-prerelease][+build]</c>) with SemVer precedence:
/// numeric identifiers compare as numbers, a prerelease sorts below its release, and build metadata
/// is ignored (it is parsed but not kept). Only ASCII digits and letters count.
/// </summary>
internal sealed partial class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private readonly string[] prerelease;

    private SemVersion(string major, string minor, string patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        this.prerelease = prerelease;
    }

    /// <summary>The major number, as its digits (no leading zeros), so any length compares correctly.</summary>
    public string Major { get; }

    public string Minor { get; }

    public string Patch { get; }

    /// <summary>The dot-separated prerelease identifiers; empty for a release.</summary>
    public IReadOnlyList<string> Prerelease => prerelease;

    public bool IsPrerelease => prerelease.Length > 0;

    /// <summary>The first prerelease identifier (<c>develop</c> in <c>1.2.0-develop.5</c>), or <c>null</c> for a release.</summary>
    public string? Label => prerelease.Length > 0 ? prerelease[0] : null;

    /// <summary>Parses a strict SemVer 2.0 version (no leading <c>v</c>, no whitespace).</summary>
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = null!;
        if (text is null)
        {
            return false;
        }

        Match match = Pattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        string[] identifiers = match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : [];
        version = new SemVersion(match.Groups["major"].Value, match.Groups["minor"].Value, match.Groups["patch"].Value, identifiers);
        return true;
    }

    /// <summary>Parses a release tag: exactly <c>v</c> followed by a strict SemVer 2.0 version.</summary>
    public static bool TryParseTag(string? tag, out SemVersion version)
    {
        version = null!;
        if (tag is null || !tag.StartsWith('v'))
        {
            return false;
        }

        return TryParse(tag[1..], out version);
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int core = CompareNumbers(Major, other.Major);
        if (core == 0)
        {
            core = CompareNumbers(Minor, other.Minor);
        }

        if (core == 0)
        {
            core = CompareNumbers(Patch, other.Patch);
        }

        if (core != 0)
        {
            return core;
        }

        // A release sorts above any of its prereleases.
        if (prerelease.Length == 0 && other.prerelease.Length == 0)
        {
            return 0;
        }

        if (prerelease.Length == 0)
        {
            return 1;
        }

        if (other.prerelease.Length == 0)
        {
            return -1;
        }

        int count = Math.Min(prerelease.Length, other.prerelease.Length);
        for (int i = 0; i < count; i++)
        {
            int compared = CompareIdentifiers(prerelease[i], other.prerelease[i]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return prerelease.Length.CompareTo(other.prerelease.Length);
    }

    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVersion other && Equals(other);

    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);

    /// <summary>The version without build metadata, e.g. <c>1.2.0-develop.5</c>.</summary>
    public override string ToString() =>
        prerelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{string.Join('.', prerelease)}";

    public static bool operator >(SemVersion left, SemVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(SemVersion left, SemVersion right) => left.CompareTo(right) < 0;

    // Numeric identifiers have no leading zeros, so a longer one is larger; equal lengths compare digit by digit.
    private static int CompareNumbers(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return left.Length.CompareTo(right.Length);
        }

        return Math.Sign(string.CompareOrdinal(left, right));
    }

    // SemVer 11.4: numeric < alphanumeric; numerics as numbers; alphanumerics in ASCII order.
    private static int CompareIdentifiers(string left, string right)
    {
        bool leftNumeric = left.All(char.IsAsciiDigit);
        bool rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            return CompareNumbers(left, right);
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(left, right));
    }

    // The SemVer 2.0 regex, ASCII only, anchored with \z so a trailing line break does not match.
    [GeneratedRegex(
        @"^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)" +
        @"(?:-(?<pre>(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?" +
        @"(?:\+[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
