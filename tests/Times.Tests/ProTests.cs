using Spectre.Console;
using Spectre.Console.Testing;

namespace Times.Tests;

public sealed class ProTests : IDisposable
{
    private static readonly DateOnly Built = new DateOnly(2026, 10, 27);

    private readonly TestLicences licences = new();
    private readonly TempFolder data = new();
    private readonly TempFolder activation = new();

    public void Dispose()
    {
        licences.Dispose();
        data.Dispose();
        activation.Dispose();
    }

    // AC-8
    [Fact]
    public void Unlocked_ForAValidCoveringKey_AndOnTheUntilDate()
    {
        Assert.True(licences.Pro(new FakeLicenceStore(stored: licences.Token(until: "2027-01-01")), Built).IsUnlocked);
        Assert.True(licences.Pro(new FakeLicenceStore(stored: licences.Token(until: "2026-10-27")), Built).IsUnlocked);
    }

    // AC-7, AC-8
    [Fact]
    public void Locked_WhenTheKeyEndsBeforeTheBuildDate()
    {
        Pro pro = licences.Pro(new FakeLicenceStore(stored: licences.Token(until: "2026-10-26")), Built);

        Assert.False(pro.IsUnlocked);
        ProStatus status = pro.Status();
        Assert.Equal(ProStatusKind.Valid, status.Kind);
        Assert.False(status.Covered);
    }

    // AC-8
    [Fact]
    public void Locked_WithNoKey_UnsupportedStore_ThrowingStore_OrNoBuildDate()
    {
        string token = licences.Token();

        Assert.False(licences.Pro(new FakeLicenceStore(), Built).IsUnlocked);
        Assert.False(licences.Pro(new FakeLicenceStore(supported: false, stored: token), Built).IsUnlocked);
        Assert.False(licences.Pro(new FakeLicenceStore(stored: token) { Throws = true }, Built).IsUnlocked);
        Assert.False(licences.Pro(new UnsupportedLicenceStore(), Built).IsUnlocked);
        Assert.False(licences.Pro(new FakeLicenceStore(stored: token), null).IsUnlocked);
    }

    // AC-8: any exception from the store is locked, not only the store's own.
    [Fact]
    public void Locked_WhenTheStoreThrowsAnything()
    {
        Assert.False(licences.Pro(new ThrowingStore(), Built).IsUnlocked);
    }

    // AC-6, AC-8
    [Fact]
    public void Locked_ForRevokedUnknownOrBrokenKeys()
    {
        Assert.False(licences.Pro(new FakeLicenceStore(stored: licences.Token()), Built, TestLicences.LicenceId).IsUnlocked);
        Assert.False(licences.Pro(new FakeLicenceStore(stored: licences.Token(kid: "other")), Built).IsUnlocked);
        Assert.False(licences.Pro(new FakeLicenceStore(stored: "CTX1-garbage"), Built).IsUnlocked);
        Assert.False(new Pro(new FakeLicenceStore(stored: licences.Token()), LicencePublicKeys.None, LicenceRevocations.None, Built).IsUnlocked);
    }

    // AC-8: the gate never reads the activation file; a missing, corrupt or forged file changes nothing.
    [Fact]
    public void IsUnlocked_DoesNotDependOnTheActivationFile()
    {
        FakeLicenceStore valid = new FakeLicenceStore(stored: licences.Token());
        FakeLicenceStore invalid = new FakeLicenceStore(stored: licences.Token(kid: "other"));
        string file = Path.Combine(activation.Path, LicenceActivationFile.FileName);

        Assert.True(licences.Pro(valid, Built).IsUnlocked);
        Assert.False(licences.Pro(invalid, Built).IsUnlocked);

        File.WriteAllText(file, "{ corrupt");
        Assert.True(licences.Pro(valid, Built).IsUnlocked);
        Assert.False(licences.Pro(invalid, Built).IsUnlocked);

        Licence forged = new Licence("other", TestLicences.LicenceId, "Forged", new DateOnly(2099, 1, 1), Built);
        new LicenceActivationFile(activation.Path).Write(forged);
        Assert.False(licences.Pro(invalid, Built).IsUnlocked);
    }

    // AC-8: build date comes from the assembly metadata of this build.
    [Fact]
    public void ForBuild_UsesTheAssemblyBuildDate()
    {
        Assert.Equal(AppVersion.BuildDate, Pro.ForBuild(new FakeLicenceStore()).BuildDate);
        Assert.NotNull(AppVersion.BuildDate);
    }

    // AC-9
    [Fact]
    public void LockedHint_IsOneLine_SayingWhatProIsAndHowToGetIt()
    {
        string hint = Pro.LockedHint("sync");

        Assert.DoesNotContain('\n', hint);
        Assert.DoesNotContain('\r', hint);
        Assert.StartsWith("sync ", hint, StringComparison.Ordinal);
        Assert.Contains("Suite Pro", hint, StringComparison.Ordinal);
        Assert.Contains("Times and Context", hint, StringComparison.Ordinal);
        Assert.Contains("pro activate <key>", hint, StringComparison.Ordinal);
    }

    // AC-9: a gated command shows the hint in red while locked; the one-shot exits 1. Unlocked, it runs.
    [Fact]
    public void GatedCommand_ShowsTheRedHint_AndTheOneShotFails()
    {
        FakeLicenceStore store = new FakeLicenceStore();
        Pro pro = licences.Pro(store, Built);
        (int exit, string output) = Shot(store, pro, "gated");

        Assert.Equal(1, exit);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)mgated needs Suite Pro, the licence for Times and Context\. Unlock it with: pro activate <key>", output);
        Assert.DoesNotContain("ran", output, StringComparison.Ordinal);

        store.Stored = licences.Token();
        (int unlockedExit, string unlocked) = Shot(store, pro, "gated");
        Assert.Equal(0, unlockedExit);
        Assert.Contains("ran", unlocked, StringComparison.Ordinal);
    }

    private (int Exit, string Output) Shot(FakeLicenceStore store, Pro pro, string line)
    {
        CommandRegistry registry = CommandRegistry.CreateDefault();
        registry.Register("gated", "gz", "A test Pro command", pro.Gate("gated", _ => new CommandResult(hint: new Text("ran"))));
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 200;
        int exit = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), registry,
            licenceStore: store, pro: pro, activationFolder: activation.Path).Run([line]);
        return (exit, console.Output);
    }

    private sealed class ThrowingStore : ILicenceStore
    {
        public bool IsSupported => true;

        public string? WriteRefusal => null;

        public string? Read() => throw new InvalidOperationException("boom");

        public void Write(string key) => throw new InvalidOperationException("boom");

        public bool Delete() => throw new InvalidOperationException("boom");
    }
}
