using Spectre.Console;

namespace Times;

/// <summary>What the licence store holds, as <see cref="Pro.Status"/> sees it.</summary>
internal enum ProStatusKind
{
    /// <summary>No key is stored.</summary>
    None,

    /// <summary>A key is stored but does not verify (bad, unknown key, revoked).</summary>
    Invalid,

    /// <summary>A key is stored and verifies; whether it covers this build is <see cref="ProStatus.Covered"/>.</summary>
    Valid,
}

/// <summary>The stored licence and whether it unlocks this build.</summary>
internal sealed class ProStatus
{
    public ProStatus(ProStatusKind kind, Licence? licence = null, string? problem = null, bool covered = false)
    {
        Kind = kind;
        Licence = licence;
        Problem = problem;
        Covered = covered;
    }

    public ProStatusKind Kind { get; }

    /// <summary>The verified licence (only for <see cref="ProStatusKind.Valid"/>).</summary>
    public Licence? Licence { get; }

    /// <summary>The fixed <see cref="LicenceKey"/> reason (only for <see cref="ProStatusKind.Invalid"/>).</summary>
    public string? Problem { get; }

    /// <summary>True when the licence's updates-until date is on or after this build's date.</summary>
    public bool Covered { get; }
}

/// <summary>
/// The one Suite Pro gate. <see cref="IsUnlocked"/> is true exactly when the licence store holds a key
/// that verifies against the public-key table, is not revoked, and whose updates-until date is on or after
/// this build's date. It never reads the activation file and never touches the network.
/// </summary>
internal sealed class Pro
{
    private readonly ILicenceStore store;
    private readonly LicencePublicKeys keys;
    private readonly LicenceRevocations revocations;

    /// <param name="buildDate">This build's date; <c>null</c> (missing or unreadable) keeps Pro locked.</param>
    public Pro(ILicenceStore store, LicencePublicKeys keys, LicenceRevocations revocations, DateOnly? buildDate)
    {
        this.store = store;
        this.keys = keys;
        this.revocations = revocations;
        BuildDate = buildDate;
    }

    /// <summary>The gate for this build: the compiled key table and revocations, and the build date.</summary>
    public static Pro ForBuild(ILicenceStore store) =>
        new(store, LicencePublicKeys.Embedded(), LicenceRevocations.Embedded(), AppVersion.BuildDate);

    public DateOnly? BuildDate { get; }

    /// <summary>True when Pro features may run. Any failure (no store, a store that throws, anything else) is locked.</summary>
    public bool IsUnlocked
    {
        get
        {
            try
            {
                ProStatus status = Status();
                return status.Kind == ProStatusKind.Valid && status.Covered;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Checks a key against this build's table and revocations (no store is touched).</summary>
    public LicenceCheck Check(string? key) => LicenceKey.TryVerify(key, keys, revocations);

    /// <summary>True when this build's date is known and on or before the licence's updates-until date.</summary>
    public bool Covers(Licence licence) => BuildDate is { } date && licence.Covers(date);

    /// <summary>Reads and checks the stored key. Throws what the store throws; an unsupported store throws too.</summary>
    public ProStatus Status()
    {
        if (!store.IsSupported)
        {
            throw new TokenStoreException(UnsupportedTokenStore.NotAvailable);
        }

        string? key = store.Read();
        if (key is null)
        {
            return new ProStatus(ProStatusKind.None);
        }

        LicenceCheck check = Check(key);
        if (check.Licence is not { } licence)
        {
            return new ProStatus(ProStatusKind.Invalid, problem: check.Problem);
        }

        return new ProStatus(ProStatusKind.Valid, licence, covered: Covers(licence));
    }

    /// <summary>
    /// The one line a locked Pro command shows (plain text, no markup): what Pro is and how to unlock it.
    /// </summary>
    public static string LockedHint(string command) =>
        $"{command} needs Suite Pro, the licence for Times and Context. Unlock it with: pro activate <key>";

    /// <summary>
    /// Wraps a Pro command's handler: while Pro is locked it returns <see cref="LockedHint"/> as a red hint
    /// (a failed one-shot) and never runs the handler.
    /// </summary>
    public Func<string, CommandResult> Gate(string command, Func<string, CommandResult> handler) =>
        argument => IsUnlocked ? handler(argument) : CommandResult.Error(Markup.Escape(LockedHint(command)));
}
