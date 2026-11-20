using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Times.Tests;

public sealed class OneShotTests : IDisposable
{
    private readonly TempFolder data = new();
    private readonly TempFolder other = new();

    public void Dispose()
    {
        data.Dispose();
        other.Dispose();
    }

    private string TodayFile(string folder) => Path.Combine(folder, "2026-10-27.json");

    private static (int Exit, string Output) Shot(string folder, params string[] args)
    {
        // No input is queued: reading a line would throw instead of hanging.
        TestConsole console = new TestConsole().Interactive();
        console.Profile.Width = 120;
        int exit = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder)).Run(args);
        return (exit, console.Output);
    }

    private static void Session(string folder, params string[] lines)
    {
        TestConsole console = new TestConsole().Interactive();
        foreach (string line in lines)
        {
            console.Input.PushTextWithEnter(line);
        }

        console.Input.PushTextWithEnter("quit");
        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(folder)).Run([]));
    }

    // AC-1
    [Fact]
    public void Arrive_BooksOnTodaysFile_LikeTheSession()
    {
        (int exit, _) = Shot(data.Path, "arrive", "8:15");
        Session(other.Path, "arrive 8:15");

        Assert.Equal(0, exit);
        Assert.Equal(File.ReadAllText(TodayFile(other.Path)), File.ReadAllText(TodayFile(data.Path)));
    }

    // AC-2
    [Fact]
    public void SeveralArgs_AreJoined_AliasAndSlashWork_DoubleSlashIsUnknown()
    {
        Assert.Equal(0, Shot(data.Path, "addproject", "1234", "Design").Exit);
        (int cpExit, string cpOut) = Shot(data.Path, "checkpoint", "8:30", "#1", "Design review");
        Assert.True(cpExit == 0, cpOut);
        Session(other.Path, "addproject 1234 Design", "checkpoint 8:30 #1 Design review");
        Assert.Equal(File.ReadAllText(TodayFile(other.Path)), File.ReadAllText(TodayFile(data.Path)));

        using TempFolder alias = new();
        Assert.Equal(0, Shot(alias.Path, "a", "9:00").Exit);
        using TempFolder slash = new();
        Assert.Equal(0, Shot(slash.Path, "/arrive", "9:00").Exit);
        Assert.Equal(File.ReadAllText(TodayFile(alias.Path)), File.ReadAllText(TodayFile(slash.Path)));

        using TempFolder bad = new();
        (int exit, string output) = Shot(bad.Path, "//a", "9:00");
        Assert.Equal(1, exit);
        Assert.Contains("Unknown command", output);
        Assert.False(File.Exists(TodayFile(bad.Path)));
    }

    // AC-3
    [Fact]
    public void Leave_BooksAtTheInjectedTime()
    {
        Shot(data.Path, "arrive", "8:15");

        Assert.Equal(0, Shot(data.Path, "leave").Exit);

        string json = File.ReadAllText(TodayFile(data.Path));
        Assert.Contains("18:30", json);
    }

    // AC-4
    [Fact]
    public void Status_PrintsTheDayView_AndWritesNothingWhenTodayHasNoFile()
    {
        (int empty, _) = Shot(data.Path, "status");
        Assert.Equal(0, empty);
        Assert.False(File.Exists(TodayFile(data.Path)));

        Shot(data.Path, "arrive", "8:15");
        (int exit, string output) = Shot(data.Path, "status");
        Assert.Equal(0, exit);
        Assert.Contains("08:15", output);
        Assert.Contains("AR", output);
    }

    // AC-5
    [Theory]
    [InlineData("nonsense")]
    [InlineData("arrive 25:00")]
    public void Errors_ReturnOne_PrintRed_SaveNothing(string line)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;

        int exit = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run(line.Split(' '));

        Assert.Equal(1, exit);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m", console.Output);
        Assert.False(File.Exists(TodayFile(data.Path)));
    }

    [Fact]
    public void AbsenceDay_RefusesBooking_WithOne()
    {
        Shot(data.Path, "absence", "sick");
        string before = File.ReadAllText(TodayFile(data.Path));

        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        int exit = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run(["arrive", "8:00"]);
        string output = console.Output;

        Assert.Equal(1, exit);
        Assert.Matches(@"\x1b\[(31|91|38;5;9)m", output);
        Assert.Contains("absence", output);
        Assert.Equal(before, File.ReadAllText(TodayFile(data.Path)));
    }

    // AC-6
    [Fact]
    public void Output_IsPlain()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        App app = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), mouse: new MouseReporting(console));

        Assert.Equal(0, app.Run(["arrive", "8:15"]));
        Assert.Equal(0, app.Run(["help"]));
        Assert.Equal(0, app.Run(["status"]));

        string output = console.Output;
        Assert.DoesNotContain("\u001b[?1000h", output);
        Assert.DoesNotContain("\u001b[?2004h", output);
        Assert.DoesNotContain("Type help for commands", output);
        Assert.DoesNotContain("│ > ", output);
        Assert.DoesNotContain("\u001b[2J", output);
        Assert.DoesNotMatch(@"\x1b\[H", output);
        Assert.DoesNotMatch(@"\x1b\[\d*;\d*H", output);
        Assert.Contains("08:15", output);
    }

    // AC-6: the same console does emit a clear in the session, so the check above can fail.
    [Fact]
    public void Session_EmitsAClear_SoTheOneShotCheckIsMeaningful()
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Input.PushTextWithEnter("quit");

        new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]);

        Assert.Matches(@"\x1b\[(2J|H|\d+;\d+H)", console.Output);
    }

    // AC-7
    [Theory]
    [InlineData("quit")]
    [InlineData("settings")]
    [InlineData("prev")]
    [InlineData("next")]
    [InlineData("today")]
    [InlineData("update")]
    [InlineData("import /tmp/x")]
    [InlineData("devops connect contoso")]
    [InlineData("dv connect contoso")]
    public void SessionOnly_IsRefused_ChangesNothing(string line)
    {
        (int exit, string output) = Shot(data.Path, line.Split(' '));

        Assert.Equal(1, exit);
        Assert.Contains("needs the interactive session. Run times without arguments.", output);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data.Path));
    }

    [Fact]
    public void Help_IsAllowed()
    {
        (int exit, string output) = Shot(data.Path, "help");

        Assert.Equal(0, exit);
        Assert.Contains("arrive", output);
        Assert.DoesNotContain("status", output);
    }

    // AC-8
    [Fact]
    public void DataFolderRules_AreShared()
    {
        Assert.Equal(2, Shot("relative", "arrive", "8:00").Exit);

        using InstanceLock held = new InstanceLock(data.Path);
        Assert.Equal(InstanceLockStatus.Acquired, held.TryAcquire().Status);
        Assert.Equal(3, Shot(data.Path, "arrive", "8:00").Exit);

        Assert.Equal(0, Shot(data.Path, "--version").Exit);
        Assert.Equal(0, Shot("relative", "arrive", "--version").Exit);
    }

    [Fact]
    public void LockFailure_ExitsFour()
    {
        string file = Path.Combine(other.Path, "afile");
        File.WriteAllText(file, "x");
        string folder = Path.Combine(file, "sub");

        Assert.Equal(4, Shot(folder, "arrive", "8:00").Exit);
    }

    [Theory]
    [InlineData("--Version")]
    [InlineData("-v")]
    public void DashArguments_StillStartTheSession(string arg)
    {
        TestConsole console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([arg]));
        Assert.Contains("Type help for commands", console.Output);
    }

    // AC-9
    [Fact]
    public void NoArguments_StartsTheSession()
    {
        TestConsole console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("quit");

        Assert.Equal(0, new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path)).Run([]));
        Assert.Contains("Type help for commands", console.Output);
    }

    [Fact]
    public void OneShot_EnablesNoMouse_NoLiveClock_NoUpdateCheck()
    {
        using TestSigner signer = new();
        FakeReleaseSource source = new([]);
        UpdateService service = new(source, signer.Signature, null, new UpdateApplier(new FakeLauncher(), windows: false), "1.0.0");
        TestConsole console = new TestConsole().Interactive();
        ScriptedEvents events = new();
        App app = new(console, FixedTimeProvider.At20261027(), new DayStore(data.Path), mouse: new MouseReporting(console),
            updates: service, events: events, liveClock: true);

        Assert.Equal(0, app.Run(["arrive", "8:15"]));

        Assert.Equal(0, source.ListCalls);
        Assert.Empty(source.Downloads);
        Assert.Equal(0, events.Waits);
        Assert.False(events.TryTake(out _));
        Assert.DoesNotContain("\u001b[?1000h", console.Output);
        Assert.DoesNotContain("\u001b[?1000l", console.Output);
    }

    // AC-10
    [Fact]
    public void Lock_IsReleasedAfterAOneShot()
    {
        Assert.Equal(0, Shot(data.Path, "arrive", "8:15").Exit);
        Assert.Equal(0, Shot(data.Path, "leave").Exit);
        Assert.Equal(InstanceLockStatus.Acquired, new InstanceLock(data.Path).TryAcquire().Status);
    }

    // AC-9 (128): one-shot commands never clear the screen, draw the layout or poll the size.
    [Theory]
    [InlineData("status")]
    [InlineData("arrive", "8:15")]
    public void OneShot_HasNoClear_AndNeverAsksForAKey(params string[] args)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = 120;
        HookedInput input = new HookedInput(console.Input);

        int exit = new App(new InputOverrideConsole(console, input), FixedTimeProvider.At20261027(), new DayStore(data.Path),
            watchResize: true).Run(args);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("\u001b[2J", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("╭", console.Output, StringComparison.Ordinal);
        Assert.Equal(0, input.KeyAvailableCalls);
    }

    // --- pro (302) -----------------------------------------------------------------------------------

    private static readonly DateOnly ProBuilt = new DateOnly(2026, 10, 27);

    private const string Red = @"\x1b\[(31|91|38;5;9)m";
    private const string Green = @"\x1b\[(32|92|38;5;2)m";
    private const string Yellow = @"\x1b\[(33|93|38;5;11)m";
    private const string Grey = @"\x1b\[(90|38;5;8|38;5;244)m";

    private string ActivationFolder => Path.Combine(other.Path, "SuitePro");

    private string ActivationFile => Path.Combine(ActivationFolder, "activation.json");

    // One pro one-shot with ANSI colours, no update service, token store or DevOps client (so no network at all).
    private (int Exit, string Output) ProShot(FakeLicenceStore store, Pro pro, params string[] args)
    {
        TestConsole console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 240;
        int exit = new App(console, FixedTimeProvider.At20261027(), new DayStore(data.Path),
            licenceStore: store, pro: pro, activationFolder: ActivationFolder).Run(args);
        Assert.DoesNotContain("   at ", console.Output, StringComparison.Ordinal);
        return (exit, console.Output);
    }

    // Visible lines of the output, ANSI sequences removed, blank lines dropped.
    private static List<string> PlainLines(string output) =>
        Regex.Replace(output, @"\x1b\[[0-9;?]*[A-Za-z]", string.Empty)
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .ToList();

    // AC-3
    [Fact]
    public void ProActivate_ValidKey_StoresTheExactToken_WritesTheFile_PrintsOneGreenLine()
    {
        using TestLicences licences = new TestLicences();
        FakeLicenceStore store = new FakeLicenceStore();
        string token = licences.Token(name: "Ada Lovelace", until: "2027-12-31");

        (int exit, string output) = ProShot(store, licences.Pro(store, ProBuilt), "pro", "activate", token);

        Assert.Equal(0, exit);
        Assert.Equal([token], store.Writes);
        Assert.Equal(token, store.Stored);
        Assert.Equal(
            "{\"format\":1,\"licenceId\":\"" + TestLicences.LicenceId + "\",\"licensee\":\"Ada Lovelace\",\"updatesUntil\":\"2027-12-31\"}",
            File.ReadAllText(ActivationFile));
        Assert.Equal(["Suite Pro activated for Ada Lovelace · updates until 2027-12-31"], PlainLines(output));
        Assert.Matches(Green + "Suite Pro activated for Ada Lovelace", output);
        Assert.DoesNotContain(token, output, StringComparison.Ordinal);
        Assert.False(File.Exists(TodayFile(data.Path)));
    }

    // AC-3
    [Fact]
    public void ProActivate_OnUnix_File0600_Folder0700()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TestLicences licences = new TestLicences();
        FakeLicenceStore store = new FakeLicenceStore();

        (int exit, _) = ProShot(store, licences.Pro(store, ProBuilt), "po", "activate", licences.Token());

        Assert.Equal(0, exit);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ActivationFile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(ActivationFolder));
    }

    // AC-4: bad, unknown-key and revoked keys: one red line with the reason, never the key, nothing changed.
    [Theory]
    [InlineData("garbage", LicenceKey.Malformed)]
    [InlineData("tampered", LicenceKey.BadSignature)]
    [InlineData("unknown", LicenceKey.UnknownKey)]
    [InlineData("revoked", LicenceKey.Revoked)]
    public void ProActivate_UnusableKey_RedReason_ExitOne_StoreAndFileUnchanged(string kind, string reason)
    {
        using TestLicences licences = new TestLicences();
        using TestLicences stranger = new TestLicences();
        string earlier = licences.Token(lid: "BBECAwQFBgcICQoLDA0ODw", name: "Earlier");
        FakeLicenceStore store = new FakeLicenceStore(stored: earlier);
        Directory.CreateDirectory(ActivationFolder);
        File.WriteAllText(ActivationFile, "earlier file");
        string key = kind switch
        {
            "garbage" => "CTX1-not-a-key-SECRETISH",
            "tampered" => stranger.Token(),
            "unknown" => licences.Token(kid: "other"),
            _ => licences.Token(),
        };

        (int exit, string output) = ProShot(store, licences.Pro(store, ProBuilt, TestLicences.LicenceId), "pro", "activate", key);

        Assert.Equal(1, exit);
        Assert.Equal([$"pro: this licence key cannot be used: {reason}. Nothing was stored."], PlainLines(output));
        Assert.Matches(Red + "pro: this licence key cannot be used", output);
        Assert.DoesNotContain(key, output, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETISH", output, StringComparison.Ordinal);
        Assert.Empty(store.Writes);
        Assert.Equal(0, store.Deletes);
        Assert.Equal(earlier, store.Stored);
        Assert.Equal("earlier file", File.ReadAllText(ActivationFile));
    }

    // AC-4
    [Fact]
    public void ProActivate_WithoutAKey_ShowsARedUsageHint()
    {
        using TestLicences licences = new TestLicences();
        FakeLicenceStore store = new FakeLicenceStore();

        (int exit, string output) = ProShot(store, licences.Pro(store, ProBuilt), "pro", "activate");

        Assert.Equal(1, exit);
        Assert.Matches(Red + "pro activate: paste your licence key after it: ", output);
        Assert.Contains("pro activate <key>", PlainLines(output).Single(), StringComparison.Ordinal);
        Assert.Equal(0, store.Calls);
        Assert.False(Directory.Exists(ActivationFolder));
    }

    // AC-5
    [Fact]
    public void ProActivate_UnsupportedStoreOrRefusal_RefusesWithOneRedLine_WritesNothing()
    {
        using TestLicences licences = new TestLicences();
        string token = licences.Token();
        FakeLicenceStore unsupported = new FakeLicenceStore(supported: false);
        FakeLicenceStore refusing = new FakeLicenceStore { WriteRefusal = "Not through dotnet." };

        (int exit, string output) = ProShot(unsupported, licences.Pro(unsupported, ProBuilt), "pro", "activate", token);
        (int refusedExit, string refused) = ProShot(refusing, licences.Pro(refusing, ProBuilt), "pro", "activate", token);

        Assert.Equal(1, exit);
        Assert.Single(PlainLines(output));
        Assert.Matches(Red + "pro: No credential store is available on this system\\. Nothing was stored\\.", output);
        Assert.Equal(1, refusedExit);
        Assert.Equal(["pro: Not through dotnet. Nothing was stored."], PlainLines(refused));
        Assert.Matches(Red + "pro: Not through dotnet", refused);
        Assert.Equal(0, unsupported.Calls);
        Assert.Equal(0, refusing.Calls);
        Assert.False(Directory.Exists(ActivationFolder));
    }

    // AC-5: the store write fails: nothing else happens.
    [Fact]
    public void ProActivate_StoreWriteFails_RedLine_NoFile()
    {
        using TestLicences licences = new TestLicences();
        FakeLicenceStore store = new FakeLicenceStore { Throws = true };

        (int exit, string output) = ProShot(store, licences.Pro(store, ProBuilt), "pro", "activate", licences.Token());

        Assert.Equal(1, exit);
        Assert.Equal([$"pro: {FakeLicenceStore.Failure} Nothing was stored."], PlainLines(output));
        Assert.False(Directory.Exists(ActivationFolder));
    }

    // AC-5: the file write fails after the store write: the store entry is removed again.
    [Fact]
    public void ProActivate_FileWriteFails_RemovesTheStoreEntryAgain()
    {
        using TestLicences licences = new TestLicences();
        FakeLicenceStore store = new FakeLicenceStore();
        File.WriteAllText(ActivationFolder, "a file where the folder should be");

        (int exit, string output) = ProShot(store, licences.Pro(store, ProBuilt), "pro", "activate", licences.Token());

        Assert.Equal(1, exit);
        Assert.Equal(["pro: the activation file could not be written. Nothing was stored."], PlainLines(output));
        Assert.Single(store.Writes);
        Assert.Equal(1, store.Deletes);
        Assert.Null(store.Stored);
    }

    // AC-6
    [Fact]
    public void ProStatus_ValidKey_ShowsLicenseeAndUpdatesUntil()
    {
        using TestLicences licences = new TestLicences();
        FakeLicenceStore store = new FakeLicenceStore(stored: licences.Token(name: "Ada Lovelace", until: "2027-12-31"));

        (int exit, string output) = ProShot(store, licences.Pro(store, ProBuilt), "pro", "status");
        (int bareExit, string bare) = ProShot(store, licences.Pro(store, ProBuilt), "pro");

        Assert.Equal(0, exit);
        Assert.Equal(["Suite Pro: Ada Lovelace · updates until 2027-12-31"], PlainLines(output));
        Assert.Equal(0, bareExit);
        Assert.Equal(PlainLines(output), PlainLines(bare));
        Assert.Empty(store.Writes);
    }

    // AC-6
    [Fact]
    public void ProStatus_NoKey_GreyNoteWithHowToActivate()
    {
        using TestLicences licences = new TestLicences();
        FakeLicenceStore store = new FakeLicenceStore();

        (int exit, string output) = ProShot(store, licences.Pro(store, ProBuilt), "po", "status");

        Assert.Equal(0, exit);
        Assert.Equal(["No Pro licence activated. Activate one with pro activate <key>."], PlainLines(output));
        Assert.Matches(Grey + "No Pro licence activated\\.", output);
    }

    // AC-6: a stored key that no longer verifies is a red line, and Pro stays locked.
    [Theory]
    [InlineData("revoked", LicenceKey.Revoked)]
    [InlineData("unknown", LicenceKey.UnknownKey)]
    public void ProStatus_StoredKeyNoLongerVerifies_RedLine_Locked(string kind, string reason)
    {
        using TestLicences licences = new TestLicences();
        FakeLicenceStore store = new FakeLicenceStore(stored: kind == "revoked" ? licences.Token() : licences.Token(kid: "old"));
        Pro pro = licences.Pro(store, ProBuilt, TestLicences.LicenceId);

        (int exit, string output) = ProShot(store, pro, "pro", "status");

        Assert.Equal(1, exit);
        Assert.Equal([$"pro: the stored licence key cannot be used: {reason}. Pro stays locked."], PlainLines(output));
        Assert.Matches(Red + "pro: the stored licence key", output);
        Assert.False(pro.IsUnlocked);
    }

    // AC-7: a key that ends before the build date is still saved, with a yellow line; status says the same.
    [Fact]
    public void ProActivate_KeyOlderThanTheBuild_SavesIt_WithAYellowLine()
    {
        using TestLicences licences = new TestLicences();
        FakeLicenceStore store = new FakeLicenceStore();
        Pro pro = licences.Pro(store, ProBuilt);
        string token = licences.Token(until: "2026-10-26");
        const string Note = "! This Times version (built 2026-10-27) is newer than this licence covers (updates until 2026-10-26); Pro stays locked in it.";

        (int exit, string output) = ProShot(store, pro, "pro", "activate", token);
        (int statusExit, string status) = ProShot(store, pro, "pro", "status");

        Assert.Equal(0, exit);
        Assert.Equal(token, store.Stored);
        Assert.True(File.Exists(ActivationFile));
        Assert.Equal(["Suite Pro activated for Ada Lovelace · updates until 2026-10-26", Note], PlainLines(output));
        Assert.Matches(Yellow + "! This Times version", output);
        Assert.Equal(0, statusExit);
        Assert.Equal(["Suite Pro: Ada Lovelace · updates until 2026-10-26", Note], PlainLines(status));
        Assert.Matches(Yellow + "! This Times version", status);
        Assert.False(pro.IsUnlocked);
    }

    // AC-10
    [Fact]
    public void ProDeactivate_RemovesStoreEntryAndFile_ThenLocked()
    {
        using TestLicences licences = new TestLicences();
        FakeLicenceStore store = new FakeLicenceStore();
        Pro pro = licences.Pro(store, ProBuilt);
        Assert.Equal(0, ProShot(store, pro, "pro", "activate", licences.Token()).Exit);
        Assert.True(pro.IsUnlocked);

        (int exit, string output) = ProShot(store, pro, "pro", "deactivate");

        Assert.Equal(0, exit);
        Assert.Equal(["Suite Pro licence removed from this computer."], PlainLines(output));
        Assert.Null(store.Stored);
        Assert.False(File.Exists(ActivationFile));
        Assert.False(pro.IsUnlocked);
    }

    // AC-10
    [Fact]
    public void ProDeactivate_NothingActivated_GreyNote_ExitZero()
    {
        using TestLicences licences = new TestLicences();
        FakeLicenceStore store = new FakeLicenceStore();

        (int exit, string output) = ProShot(store, licences.Pro(store, ProBuilt), "pro", "deactivate");

        Assert.Equal(0, exit);
        Assert.Equal(["No Pro licence was activated."], PlainLines(output));
        Assert.Matches(Grey + "No Pro licence was activated\\.", output);
        Assert.Equal(1, store.Deletes);
    }

    // A mistyped sub-command is never echoed (it may be a pasted key).
    [Fact]
    public void Pro_UnknownSubCommand_IsNotEchoed()
    {
        using TestLicences licences = new TestLicences();
        FakeLicenceStore store = new FakeLicenceStore();
        string token = licences.Token();

        (int exit, string output) = ProShot(store, licences.Pro(store, ProBuilt), "pro", token);

        Assert.Equal(1, exit);
        Assert.DoesNotContain(token, output, StringComparison.Ordinal);
        Assert.Equal(0, store.Calls);
    }

    // The App never takes a licence store without an activation folder, so no test writes the real application data.
    [Fact]
    public void App_LicenceStoreWithoutActivationFolder_IsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            new App(new TestConsole(), FixedTimeProvider.At20261027(), new DayStore(data.Path), licenceStore: new FakeLicenceStore()));
    }

    // Job 303 ------------------------------------------------------------------------------

    private static (int Exit, string Output) JsonShot(string folder, int width, InstanceLock? instanceLock, params string[] args)
    {
        TestConsole console = new TestConsole().Interactive().EmitAnsiSequences();
        console.Profile.Width = width;
        int exit = new App(console, FixedTimeProvider.At20261027(), new DayStore(folder), instanceLock: instanceLock).Run(args);
        return (exit, console.Output);
    }

    private static string HoursCp(string time, string name, int? workItem = null) =>
        workItem is { } w
            ? $"{{\"kind\":\"checkpoint\",\"time\":\"{time}\",\"name\":{JsonSerializer.Serialize(name)},\"workItem\":{w}}}"
            : $"{{\"kind\":\"checkpoint\",\"time\":\"{time}\",\"name\":{JsonSerializer.Serialize(name)}}}";

    private void WriteHoursDay(string date, params string[] entries) =>
        File.WriteAllText(Path.Combine(data.Path, date + ".json"), "{\"entries\":[" + string.Join(',', entries) + "]}");

    private void WriteHoursDays()
    {
        WriteHoursDay("2026-10-20", "{\"kind\":\"arrival\",\"time\":\"08:00\"}",
            HoursCp("09:00", "A rather long checkpoint name that is far wider than forty columns", 1234),
            HoursCp("10:07", "Mail"), "{\"kind\":\"leaving\",\"time\":\"11:00\"}");
        File.WriteAllText(Path.Combine(data.Path, "projects.json"),
            "{\"projects\":[{\"workItem\":1234,\"title\":\"A quicklist title that is also longer than forty columns\"}]}");
    }

    private static readonly string[] TopLevel =
        ["schemaVersion", "from", "to", "roundedToMinutes", "skippedFiles", "days", "workItems", "totalMinutes"];

    // AC-1
    [Fact]
    public void ReportJson_PrintsOnlyOneJsonObject_UnwrappedAtWidth40()
    {
        WriteHoursDays();

        (int exit, string output) = JsonShot(data.Path, 40, null, "report", "--from", "2026-10-01", "--to", "2026-10-31", "--json");

        Assert.True(exit == 0, output);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
        Assert.StartsWith("{", output, StringComparison.Ordinal);
        Assert.EndsWith("}\n", output, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.Equal(TopLevel, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-10-01", root.GetProperty("from").GetString());
        Assert.Equal("2026-10-31", root.GetProperty("to").GetString());
        Assert.Equal(0, root.GetProperty("roundedToMinutes").GetInt32());
        Assert.Equal("A quicklist title that is also longer than forty columns",
            root.GetProperty("workItems")[0].GetProperty("title").GetString());
        Assert.Equal(120, root.GetProperty("totalMinutes").GetInt32());
    }

    // AC-1, AC-3: the flags in another order, the alias, and the rounding step from settings.json.
    [Fact]
    public void ReportJson_FlagsInAnyOrder_AliasAndRounding()
    {
        WriteHoursDays();
        File.WriteAllText(Path.Combine(data.Path, "settings.json"), "{\"settings\":{\"report-rounding\":\"15 min\"}}");

        (int exit, string output) = JsonShot(data.Path, 120, null, "rp", "--json", "--to", "2026-10-31", "--from", "2026-10-01");

        Assert.True(exit == 0, output);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal(15, document.RootElement.GetProperty("roundedToMinutes").GetInt32());
        // 1:07 rounds to 1:00 for #1234, 0:53 to 1:00 for Mail.
        Assert.Equal(60, document.RootElement.GetProperty("workItems")[0].GetProperty("minutes").GetInt32());
        Assert.Equal(60, document.RootElement.GetProperty("workItems")[1].GetProperty("minutes").GetInt32());
    }

    // AC-4
    [Fact]
    public void ReportJson_BrokenFileAndEmptyRange_ExitZero()
    {
        File.WriteAllText(Path.Combine(data.Path, "2026-10-21.json"), "{ broken");

        (int exit, string output) = Shot(data.Path, "report", "--from", "2026-10-01", "--to", "2026-10-31", "--json");
        (int emptyExit, string empty) = Shot(data.Path, "report", "--from", "2026-11-01", "--to", "2026-11-30", "--json");

        Assert.Equal(0, exit);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal(1, document.RootElement.GetProperty("skippedFiles").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("days").GetArrayLength());
        Assert.Equal(0, emptyExit);
        using JsonDocument none = JsonDocument.Parse(empty);
        Assert.Equal(0, none.RootElement.GetProperty("days").GetArrayLength());
        Assert.Equal(0, none.RootElement.GetProperty("workItems").GetArrayLength());
        Assert.Equal(0, none.RootElement.GetProperty("totalMinutes").GetInt32());
    }

    // AC-5
    [Fact]
    public void ReportFlags_WithoutJson_PrintTheSameTable()
    {
        WriteHoursDays();

        (int exit, string positional) = Shot(data.Path, "report", "2026-10-01", "2026-10-31");
        (int flagExit, string flags) = Shot(data.Path, "report", "--from", "2026-10-01", "--to", "2026-10-31");
        (int swappedExit, string swapped) = Shot(data.Path, "report", "--to", "2026-10-31", "--from", "2026-10-01");

        Assert.Equal(0, exit);
        Assert.Equal(0, flagExit);
        Assert.Equal(0, swappedExit);
        Assert.Contains("Total", positional, StringComparison.Ordinal);
        Assert.Equal(positional, flags);
        Assert.Equal(positional, swapped);
    }

    // AC-5
    [Theory]
    [InlineData("--from", "2026-10-01", "--json")]
    [InlineData("--to", "2026-10-31", "--json")]
    [InlineData("--from", "2026-10-01", "--to", "--json")]
    [InlineData("--from", "2026-10-01", "--from", "2026-10-02", "--to", "2026-10-31", "--json")]
    [InlineData("--from", "2026-10-01", "--to", "2026-10-31", "--to", "2026-10-31", "--json")]
    [InlineData("--from", "2026-10-01", "--to", "2026-10-31", "--json", "--json")]
    [InlineData("--from", "2026-10-01", "--to", "2026-10-31", "--csv")]
    [InlineData("2026-10-01", "2026-10-31", "--json")]
    [InlineData("2026-10-01", "--to", "2026-10-31", "--json")]
    [InlineData("--from", "2026-10-01", "--to", "2026-10-31", "extra", "--json")]
    [InlineData("--from", "2026-13-01", "--to", "2026-10-31", "--json")]
    [InlineData("--from", "2026-10-31", "--to", "2026-10-01", "--json")]
    public void ReportFlags_BadForms_FailWithARedHint(params string[] flags)
    {
        (int exit, string output) = JsonShot(data.Path, 200, null, ["report", .. flags]);

        Assert.Equal(1, exit);
        Assert.False(output.StartsWith('{'), output);
        Assert.Contains("report: ", output, StringComparison.Ordinal);
        Assert.Contains("\u001b[", output, StringComparison.Ordinal);
    }

    // AC-5
    [Fact]
    public void ReportJson_FolderThatCannotBeListed_FailsWithARedHint()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using InstanceLock sideLock = new InstanceLock(other.Path);
        UnixFileMode mode = File.GetUnixFileMode(data.Path);
        File.SetUnixFileMode(data.Path, UnixFileMode.None);
        try
        {
            try
            {
                Directory.EnumerateFileSystemEntries(data.Path).ToList();
                // Running as root: access can't be denied.
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            (int exit, string output) = JsonShot(data.Path, 200, sideLock,
                "report", "--from", "2026-10-01", "--to", "2026-10-31", "--json");

            Assert.Equal(1, exit);
            Assert.False(output.StartsWith('{'), output);
            Assert.Contains("report: the data folder cannot be listed", output, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", output, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(data.Path, mode);
        }
    }

    // AC-7
    [Fact]
    public void ReportJson_NeverChangesTheDataFolder()
    {
        WriteHoursDays();
        WriteHoursDay("2026-10-27", "{\"kind\":\"arrival\",\"time\":\"17:00\"}", HoursCp("17:10", "Say \"hi\" \\ <b>&</b> Grüße"));
        Dictionary<string, byte[]> before = Snapshot(data.Path);

        (int exit, string output) = Shot(data.Path, "report", "--from", "2026-10-01", "--to", "2026-10-31", "--json");

        Assert.Equal(0, exit);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Contains(document.RootElement.GetProperty("workItems").EnumerateArray(),
            r => r.GetProperty("title").GetString() == "Say \"hi\" \\ <b>&</b> Grüße");
        Dictionary<string, byte[]> after = Snapshot(data.Path);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (KeyValuePair<string, byte[]> pair in before)
        {
            Assert.Equal(pair.Value, after[pair.Key]);
        }
    }

    private static Dictionary<string, byte[]> Snapshot(string folder) =>
        Directory.EnumerateFileSystemEntries(folder).ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes, StringComparer.Ordinal);

    // AC-8
    [Fact]
    public void Readme_DocumentsEveryJsonProperty()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Times.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string readme = File.ReadAllText(Path.Combine(directory.FullName, "README.md"));
        string manual = File.ReadAllText(Path.Combine(directory.FullName, "MANUAL.md"));
        string[] nested =
        [
            "days[].date", "days[].absence", "days[].arrival", "days[].leaving", "days[].breaks", "days[].breakMinutes",
            "days[].workMinutes", "workItems[].workItem", "workItems[].title", "workItems[].minutes", "workItems[].open",
            "workItems[].checkpoints",
        ];
        foreach (string name in TopLevel.Concat(nested))
        {
            Assert.Contains("| `" + name + "` |", readme, StringComparison.Ordinal);
        }

        Assert.Contains("`start`", readme, StringComparison.Ordinal);
        Assert.Contains("`end`", readme, StringComparison.Ordinal);
        Assert.Contains("times report --from 2026-11-01 --to 2026-11-30 --json", manual, StringComparison.Ordinal);
    }
}
