namespace Times;

/// <summary>A release on the chosen channel that is newer than the running copy.</summary>
internal sealed record UpdateCandidate
{
    public UpdateCandidate(SemVersion version, ReleaseInfo release)
    {
        Version = version;
        Release = release;
    }

    public SemVersion Version { get; }

    public ReleaseInfo Release { get; }
}

/// <summary>
/// Picks the update to offer from the release list: drafts and tags that are not <c>v</c> + strict
/// SemVer are skipped, the channel filters what is left, and the highest version strictly above the
/// running one wins, whatever order the list comes in.
/// </summary>
internal static class UpdateChecker
{
    public const string Stable = "stable";

    public const string Beta = "beta";

    public const string Develop = "develop";

    /// <summary>
    /// True if <paramref name="release"/> (tagged <paramref name="version"/>) belongs to <paramref name="channel"/>.
    /// <c>stable</c>: <c>prerelease:false</c> and no prerelease part. <c>beta</c>: also versions whose first
    /// prerelease identifier is <c>beta</c>. <c>develop</c>: also those whose first one is <c>develop</c>.
    /// Any other label, and any unknown channel, is skipped.
    /// </summary>
    public static bool InChannel(ReleaseInfo release, SemVersion version, string channel)
    {
        bool stable = !release.Prerelease && !version.IsPrerelease;
        return channel switch
        {
            Stable => stable,
            Beta => stable || version.Label == "beta",
            Develop => stable || version.Label is "beta" or "develop",
            _ => false,
        };
    }

    /// <summary>The highest release on <paramref name="channel"/> above <paramref name="running"/>, or <c>null</c>.</summary>
    public static UpdateCandidate? Pick(IEnumerable<ReleaseInfo> releases, string channel, SemVersion running)
    {
        UpdateCandidate? best = null;
        foreach (ReleaseInfo release in releases)
        {
            if (release.Draft || !SemVersion.TryParseTag(release.Tag, out SemVersion version))
            {
                continue;
            }

            if (!InChannel(release, version, channel) || !(version > running))
            {
                continue;
            }

            if (best is null || version > best.Version)
            {
                best = new UpdateCandidate(version, release);
            }
        }

        return best;
    }

    /// <summary>
    /// Lists the releases and picks the update. Returns <c>null</c> without asking the source when
    /// <paramref name="runningVersion"/> does not parse (for example <c>unknown</c>), and <c>null</c> when
    /// nothing is newer. Throws <see cref="UpdateException"/> when the source fails.
    /// </summary>
    public static async Task<UpdateCandidate?> CheckAsync(IReleaseSource source, string runningVersion, string channel, CancellationToken cancellation)
    {
        if (!SemVersion.TryParse(runningVersion, out SemVersion running))
        {
            return null;
        }

        IReadOnlyList<ReleaseInfo> releases = await source.ListAsync(cancellation).ConfigureAwait(false);
        return Pick(releases, channel, running);
    }
}
