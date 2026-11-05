namespace Times.Tests;

public sealed class SemVersionTests
{
    // AC-2: numeric identifiers as numbers, prerelease below release, ASCII order for labels.
    [Theory]
    [InlineData("1.2.0-develop.10", "1.2.0-develop.9")]
    [InlineData("1.2.0", "1.2.0-develop.5")]
    [InlineData("1.2.0", "1.2.0-beta")]
    [InlineData("1.10.0", "1.9.0")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.0.10", "1.0.9")]
    [InlineData("1.0.0-beta", "1.0.0-alpha")]
    [InlineData("1.0.0-develop", "1.0.0-beta")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-alpha.1")]
    [InlineData("1.0.0-beta.11", "1.0.0-beta.2")]
    [InlineData("1.0.0-rc.1", "1.0.0-beta.11")]
    [InlineData("1.0.0-develop.100000000000000000000", "1.0.0-develop.99999999999999999999")]
    [InlineData("100000000000000000000.0.0", "99999999999999999999.0.0")]
    public void CompareTo_FollowsSemVerPrecedence(string higher, string lower)
    {
        Assert.True(SemVersion.TryParse(higher, out SemVersion high));
        Assert.True(SemVersion.TryParse(lower, out SemVersion low));

        Assert.True(high.CompareTo(low) > 0);
        Assert.True(low.CompareTo(high) < 0);
        Assert.True(high > low);
        Assert.True(low < high);
        Assert.False(high.Equals(low));
    }

    // AC-2: +build is ignored, both in comparison and in the text.
    [Theory]
    [InlineData("1.2.3+abc", "1.2.3")]
    [InlineData("1.2.3-develop.4+sha.5114f85", "1.2.3-develop.4")]
    [InlineData("1.2.3+a", "1.2.3+b")]
    public void BuildMetadata_IsIgnored(string left, string right)
    {
        Assert.True(SemVersion.TryParse(left, out SemVersion a));
        Assert.True(SemVersion.TryParse(right, out SemVersion b));

        Assert.Equal(0, a.CompareTo(b));
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.DoesNotContain("+", a.ToString(), StringComparison.Ordinal);
    }

    // AC-2
    [Theory]
    [InlineData("0.0.0", "0.0.0", false, null)]
    [InlineData("1.2.3", "1.2.3", false, null)]
    [InlineData("1.2.3-develop.39", "1.2.3-develop.39", true, "develop")]
    [InlineData("1.2.3-beta.1+build.7", "1.2.3-beta.1", true, "beta")]
    [InlineData("1.2.3-0.a", "1.2.3-0.a", true, "0")]
    [InlineData("1.2.3--x", "1.2.3--x", true, "-x")]
    public void TryParse_AcceptsStrictSemVer(string text, string normal, bool prerelease, string? label)
    {
        Assert.True(SemVersion.TryParse(text, out SemVersion version));

        Assert.Equal(normal, version.ToString());
        Assert.Equal(prerelease, version.IsPrerelease);
        Assert.Equal(label, version.Label);
    }

    // AC-2: anything that is not strict SemVer does not parse, including "unknown".
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-a_b")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3\n")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.٣")]
    [InlineData("1.2.3-é")]
    public void TryParse_RefusesAnythingElse(string? text)
    {
        Assert.False(SemVersion.TryParse(text, out _));
    }

    // AC-2: a tag parses only as "v" + strict SemVer.
    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("v1.2.3-develop.5", true)]
    [InlineData("1.2.3", false)]
    [InlineData("V1.2.3", false)]
    [InlineData("vv1.2.3", false)]
    [InlineData("v 1.2.3", false)]
    [InlineData("v", false)]
    [InlineData("release-1.2.3", false)]
    [InlineData("vunknown", false)]
    [InlineData(null, false)]
    public void TryParseTag_NeedsLowercaseVAndStrictSemVer(string? tag, bool parses)
    {
        Assert.Equal(parses, SemVersion.TryParseTag(tag, out _));
    }
}
