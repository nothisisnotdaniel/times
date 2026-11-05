namespace Times.Tests;

public sealed class UpdateCheckerTests
{
    private static ReleaseInfo Release(string tag, bool prerelease = false, bool draft = false) =>
        new(tag, prerelease, draft, []);

    private static SemVersion V(string text)
    {
        Assert.True(SemVersion.TryParse(text, out SemVersion version));
        return version;
    }

    private static readonly ReleaseInfo[] Mixed =
    [
        Release("v1.0.0"),
        Release("v1.1.0-develop.9", prerelease: true),
        Release("v1.1.0-develop.10", prerelease: true),
        Release("v1.1.0-beta.2", prerelease: true),
        Release("v1.0.5"),
        Release("v1.2.0", draft: true),
        Release("v9.9.9-rc.1", prerelease: true),
        Release("not-a-version"),
        Release("v1.0.7", prerelease: true),
        Release("1.5.0"),
    ];

    // AC-3: stable takes only prerelease:false without a prerelease part; beta adds -beta; develop adds -develop.
    [Theory]
    [InlineData("stable", "1.0.5")]
    [InlineData("beta", "1.1.0-beta.2")]
    [InlineData("develop", "1.1.0-develop.10")]
    public void Pick_TakesTheHighestOnTheChannel(string channel, string expected)
    {
        UpdateCandidate? picked = UpdateChecker.Pick(Mixed, channel, V("0.1.0"));

        Assert.NotNull(picked);
        Assert.Equal(expected, picked.Version.ToString());
    }

    // AC-3: the order of the list does not matter.
    [Theory]
    [InlineData("stable", "1.0.5")]
    [InlineData("beta", "1.1.0-beta.2")]
    [InlineData("develop", "1.1.0-develop.10")]
    public void Pick_IsTheSameForShuffledLists(string channel, string expected)
    {
        Random random = new Random(93);
        for (int round = 0; round < 50; round++)
        {
            ReleaseInfo[] shuffled = Mixed.ToArray();
            random.Shuffle(shuffled);

            Assert.Equal(expected, UpdateChecker.Pick(shuffled, channel, V("0.1.0"))?.Version.ToString());
            Assert.Equal(expected, UpdateChecker.Pick(shuffled.Reverse(), channel, V("0.1.0"))?.Version.ToString());
        }
    }

    // AC-3: drafts, unparsable tags and other labels are skipped.
    [Theory]
    [InlineData("v2.0.0", false, true, "stable", false)]
    [InlineData("v2.0.0", false, true, "develop", false)]
    [InlineData("2.0.0", false, false, "develop", false)]
    [InlineData("v2.0", false, false, "develop", false)]
    [InlineData("v2.0.0-rc.1", true, false, "develop", false)]
    [InlineData("v2.0.0-alpha", true, false, "beta", false)]
    [InlineData("v2.0.0", true, false, "stable", false)]
    [InlineData("v2.0.0-beta.1", false, false, "stable", false)]
    [InlineData("v2.0.0-develop.1", true, false, "beta", false)]
    [InlineData("v2.0.0-develop.1", true, false, "stable", false)]
    [InlineData("v2.0.0", false, false, "stable", true)]
    [InlineData("v2.0.0", false, false, "beta", true)]
    [InlineData("v2.0.0", false, false, "develop", true)]
    [InlineData("v2.0.0-beta.1", true, false, "beta", true)]
    [InlineData("v2.0.0-beta.1", true, false, "develop", true)]
    [InlineData("v2.0.0-develop.1", true, false, "develop", true)]
    [InlineData("v2.0.0", false, false, "nightly", false)]
    public void Pick_FiltersByDraftTagAndChannel(string tag, bool prerelease, bool draft, string channel, bool taken)
    {
        UpdateCandidate? picked = UpdateChecker.Pick([Release(tag, prerelease, draft)], channel, V("1.0.0"));

        Assert.Equal(taken, picked is not null);
    }

    // AC-3: only strictly above the running version.
    [Theory]
    [InlineData("1.0.5", null)]
    [InlineData("1.0.4", "1.0.5")]
    [InlineData("1.0.5-develop.3", "1.0.5")]
    [InlineData("1.0.6", null)]
    public void Pick_OnlyStrictlyAboveRunning(string running, string? expected)
    {
        Assert.Equal(expected, UpdateChecker.Pick(Mixed, "stable", V(running))?.Version.ToString());
    }

    // AC-3: a develop build sees the next develop build by its run number.
    [Fact]
    public void Pick_ComparesRunNumbersAsNumbers()
    {
        ReleaseInfo[] releases = [Release("v0.1.1-develop.9", true), Release("v0.1.1-develop.10", true), Release("v0.1.1-develop.8", true)];

        Assert.Equal("0.1.1-develop.10", UpdateChecker.Pick(releases, "develop", V("0.1.1-develop.9"))?.Version.ToString());
        Assert.Null(UpdateChecker.Pick(releases, "develop", V("0.1.1-develop.10")));
    }

    // AC-3: a running version that does not parse runs no check at all.
    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("1.0")]
    public async Task CheckAsync_UnparsableRunningVersion_NeverAsksTheSource(string running)
    {
        FakeReleaseSource source = new FakeReleaseSource(Mixed);

        UpdateCandidate? picked = await UpdateChecker.CheckAsync(source, running, "develop", CancellationToken.None);

        Assert.Null(picked);
        Assert.Equal(0, source.ListCalls);
    }

    // AC-3
    [Fact]
    public async Task CheckAsync_ListsOnceAndPicks()
    {
        FakeReleaseSource source = new FakeReleaseSource(Mixed);

        UpdateCandidate? picked = await UpdateChecker.CheckAsync(source, "0.1.0", "stable", CancellationToken.None);

        Assert.Equal("1.0.5", picked?.Version.ToString());
        Assert.Equal(1, source.ListCalls);
    }
}
