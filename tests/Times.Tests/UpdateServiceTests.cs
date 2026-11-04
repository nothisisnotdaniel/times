namespace Times.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly TempFolder temp = new();
    private readonly TestSigner signer = new();

    public void Dispose()
    {
        signer.Dispose();
        temp.Dispose();
    }

    private string Tools => Path.Combine(temp.Path, "tools");

    private string StageFolder => Path.Combine(temp.Path, "stage", "times-update-x");

    private UpdateService Service(FakeReleaseSource source, UpdateSignature? signature = null, bool installed = true,
        string running = "1.0.0", bool windows = false, string? stageFolder = null) =>
        new UpdateService(source, signature ?? signer.Signature, installed ? new ToolInstall(global: true, Tools) : null,
            new UpdateApplier(new FakeLauncher(), windows), running,
            () => Directory.CreateDirectory(stageFolder ?? StageFolder), work => work());

    private Task<UpdateOutcome> Check(UpdateService service, string mode = SettingsRegistry.UpdateNotifyOnly,
        string channel = UpdateChecker.Stable, bool command = false) =>
        service.CheckAsync(mode, channel, temp.Path, command, CancellationToken.None);

    // AC-6: notify only names the version in steelblue and downloads nothing.
    [Fact]
    public async Task Startup_NotifyOnly_TellsAndDownloadsNothing()
    {
        FakeReleaseSource withRelease = Source("1.2.0");

        UpdateOutcome outcome = await Check(Service(withRelease));

        Assert.Equal("[steelblue1]Times 1.2.0 is available. Type update to install it when you quit.[/]", outcome.Hint);
        Assert.Null(outcome.Staged);
        Assert.Empty(withRelease.Downloads);
    }

    // AC-6: an install mode stages the package (grey hint).
    [Theory]
    [InlineData(SettingsRegistry.UpdateInstallOnQuit)]
    [InlineData(SettingsRegistry.UpdateInstallAndRestart)]
    public async Task Startup_InstallModes_Stage(string mode)
    {
        FakeReleaseSource full = Source("1.2.0");

        UpdateOutcome outcome = await Check(Service(full), mode);

        Assert.Equal("[grey]Times 1.2.0 is ready and installs when you quit.[/]", outcome.Hint);
        Assert.NotNull(outcome.Staged);
        Assert.Equal(StageFolder, outcome.Staged.Folder);
        Assert.True(File.Exists(outcome.Staged.PackagePath));
    }

    // AC-6: list failures are grey "Update check skipped: <reason>.", never red, never thrown.
    [Theory]
    [InlineData("GitHub cannot be reached (offline?)")]
    [InlineData("the request timed out")]
    [InlineData("GitHub answered HTTP 500")]
    [InlineData("GitHub's rate limit was reached")]
    [InlineData("GitHub's answer is not a release list")]
    public async Task Startup_ListFailure_IsGreySkip(string reason)
    {
        FakeReleaseSource source = new FakeReleaseSource([]) { ListFailure = new UpdateException(reason) };

        UpdateOutcome outcome = await Check(Service(source), SettingsRegistry.UpdateInstallOnQuit);

        Assert.Equal($"[grey]Update check skipped: {reason}.[/]", outcome.Hint);
        Assert.Null(outcome.Staged);
    }

    // AC-6: an unexpected exception becomes a grey skip too.
    [Fact]
    public async Task Startup_UnexpectedException_IsGreySkip()
    {
        FakeReleaseSource source = new FakeReleaseSource([]) { ListFailure = new InvalidOperationException("boom [x]") };

        UpdateOutcome outcome = await Check(Service(source));

        Assert.Equal("[grey]Update check skipped: boom [[x]].[/]", outcome.Hint);
    }

    // AC-6: a background runner that throws gives a grey skip, not an exception.
    [Fact]
    public async Task StartBackground_RunnerThrows_IsGreySkip()
    {
        UpdateService service = new UpdateService(new FakeReleaseSource([]), signer.Signature, null,
            new UpdateApplier(new FakeLauncher(), false), "1.0.0", null, _ => throw new InvalidOperationException("no threads"));

        UpdateOutcome outcome = await service.StartBackground(SettingsRegistry.UpdateNotifyOnly, UpdateChecker.Stable, temp.Path);

        Assert.Equal("[grey]Update check skipped: no threads.[/]", outcome.Hint);
    }

    // AC-6: nothing newer at start shows nothing.
    [Fact]
    public async Task Startup_NothingNewer_IsSilent()
    {
        FakeReleaseSource full = Source("1.0.0");

        UpdateOutcome outcome = await Check(Service(full));

        Assert.Null(outcome.Hint);
    }

    // AC-7: the command says when nothing is newer, and stages in notify only mode too.
    [Fact]
    public async Task Command_UpToDate_AndStagesInNotifyOnly()
    {
        FakeReleaseSource empty = new FakeReleaseSource([]);
        Assert.Equal("[grey]Times is up to date (1.0.0).[/]", (await Check(Service(empty), command: true)).Hint);

        FakeReleaseSource full = Source("1.2.0");
        UpdateOutcome outcome = await Check(Service(full), SettingsRegistry.UpdateNotifyOnly, command: true);

        Assert.NotNull(outcome.Staged);
        Assert.Equal("1.2.0", outcome.Staged.Version.ToString());
    }

    // AC-3: an unparsable running version runs no check; the command says so.
    [Fact]
    public async Task UnparsableRunningVersion_NoCheck()
    {
        FakeReleaseSource source = new FakeReleaseSource([]);

        UpdateOutcome startup = await Check(Service(source, running: "unknown"));
        UpdateOutcome command = await Check(Service(source, running: "unknown"), command: true);

        Assert.Null(startup.Hint);
        Assert.Equal("[grey]Update check skipped: this copy has no release version.[/]", command.Hint);
        Assert.Equal(0, source.ListCalls);
    }

    // AC-13: with no key, the check still notifies, but nothing is staged or downloaded.
    [Fact]
    public async Task NoKey_NotifiesButStagesNothing()
    {
        FakeReleaseSource full = Source("1.2.0");
        UpdateService service = Service(full, UpdateSignature.None);

        Assert.Equal("[steelblue1]Times 1.2.0 is available. Type update to install it when you quit.[/]", (await Check(service)).Hint);
        UpdateOutcome install = await Check(service, SettingsRegistry.UpdateInstallOnQuit);
        UpdateOutcome command = await Check(service, command: true);

        Assert.Equal(UpdateService.NoKeyHint, install.Hint);
        Assert.Equal("[grey]Update skipped: this build has no signing key.[/]", command.Hint);
        Assert.Null(command.Staged);
        Assert.Empty(full.Downloads);
        Assert.False(Directory.Exists(StageFolder));
    }

    // AC-8: not an installed tool: nothing staged, the by-hand hint.
    [Fact]
    public async Task NotInstalled_StagesNothing()
    {
        FakeReleaseSource full = Source("1.2.0");

        UpdateOutcome outcome = await Check(Service(full, installed: false), command: true);

        Assert.Equal("[grey]Times is not installed as a dotnet tool; update it by hand.[/]", outcome.Hint);
        Assert.Null(outcome.Staged);
        Assert.Empty(full.Downloads);
    }

    // AC-5: a bad package is a grey "Update skipped" and leaves no folder.
    [Fact]
    public async Task BadSignature_SkipsAndCleansUp()
    {
        using TestSigner other = new TestSigner();
        FakeReleaseSource full = new FakeReleaseSource([]);
        full.Releases.Add(other.SignedRelease(full, "1.2.0"));

        UpdateOutcome outcome = await Check(Service(full), command: true);

        Assert.StartsWith("[grey]Update skipped: ", outcome.Hint, StringComparison.Ordinal);
        Assert.Null(outcome.Staged);
        Assert.False(Directory.Exists(StageFolder));
    }

    // AC-9: on Windows, a staged folder with a cmd special character is refused (grey) and deleted.
    [Fact]
    public async Task Windows_RefusedStagingFolder_IsDeleted()
    {
        string odd = Path.Combine(temp.Path, "stage", "times-update-%x%");
        FakeReleaseSource full = Source("1.2.0");

        UpdateOutcome outcome = await Check(Service(full, windows: true, stageFolder: odd), command: true);

        Assert.StartsWith("[grey]Update skipped: ", outcome.Hint, StringComparison.Ordinal);
        Assert.Null(outcome.Staged);
        Assert.False(Directory.Exists(odd));
    }

    // A source that lists one signed release and serves its assets.
    private FakeReleaseSource Source(string version)
    {
        FakeReleaseSource source = new FakeReleaseSource([]);
        source.Releases.Add(signer.SignedRelease(source, version));
        return source;
    }
}
