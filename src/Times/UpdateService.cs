using Spectre.Console;

namespace Times;

/// <summary>What a check left behind: the hint markup to show (or <c>null</c> for none) and the staged package, if any.</summary>
internal sealed record UpdateOutcome
{
    public UpdateOutcome(string? hint, StagedUpdate? staged = null)
    {
        Hint = hint;
        Staged = staged;
    }

    public static UpdateOutcome Nothing { get; } = new(hint: null);

    /// <summary>One line of markup for the hint row (external text already escaped), or <c>null</c>.</summary>
    public string? Hint { get; }

    public StagedUpdate? Staged { get; }
}

/// <summary>
/// Ties the update parts together for <see cref="App"/>: check the channel (<see cref="UpdateChecker"/>),
/// download and verify (<see cref="UpdateDownloader"/>) and, at quit, start the helper
/// (<see cref="UpdateApplier"/>). Every failure ends as a grey hint; nothing here throws.
/// </summary>
internal sealed class UpdateService
{
    public const string NoKeyHint = "[grey]Update skipped: this build has no signing key.[/]";

    public const string NotInstalledHint = "[grey]Times is not installed as a dotnet tool; update it by hand.[/]";

    private readonly IReleaseSource source;
    private readonly UpdateSignature signature;
    private readonly Func<DirectoryInfo>? createFolder;
    private readonly Func<Func<Task<UpdateOutcome>>, Task<UpdateOutcome>> background;

    /// <param name="install">How this copy is installed, or <c>null</c> when it is not a dotnet tool.</param>
    /// <param name="runningVersion">The version of this copy (<see cref="AppVersion.Current"/>).</param>
    /// <param name="createFolder">Makes the staging folder; <c>null</c> for <see cref="UpdateDownloader.CreateStagingFolder"/>.</param>
    /// <param name="background">Runs the start-up check; <c>null</c> for <see cref="Task.Run(Func{Task})"/>. Tests may run it inline.</param>
    public UpdateService(IReleaseSource source, UpdateSignature signature, ToolInstall? install, UpdateApplier applier,
        string runningVersion, Func<DirectoryInfo>? createFolder = null, Func<Func<Task<UpdateOutcome>>, Task<UpdateOutcome>>? background = null)
    {
        this.source = source;
        this.signature = signature;
        Install = install;
        Applier = applier;
        RunningVersion = runningVersion;
        this.createFolder = createFolder;
        this.background = background ?? (work => Task.Run(work));
    }

    public ToolInstall? Install { get; }

    public UpdateApplier Applier { get; }

    public string RunningVersion { get; }

    /// <summary>Starts the start-up check in the background. The task never faults.</summary>
    public Task<UpdateOutcome> StartBackground(string mode, string channel, string dataFolder)
    {
        try
        {
            return background(() => CheckAsync(mode, channel, dataFolder, command: false, CancellationToken.None));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new UpdateOutcome(Skipped(ex.Message)));
        }
    }

    /// <summary>
    /// Checks <paramref name="channel"/> and, for the <c>update</c> command or an install mode, downloads,
    /// verifies and stages the package. Never throws.
    /// </summary>
    /// <param name="command">True for the <c>update</c> command: it always stages and says when nothing is newer.</param>
    public async Task<UpdateOutcome> CheckAsync(string mode, string channel, string dataFolder, bool command, CancellationToken cancellation)
    {
        try
        {
            if (!SemVersion.TryParse(RunningVersion, out SemVersion running))
            {
                return command ? new UpdateOutcome(Skipped("this copy has no release version")) : UpdateOutcome.Nothing;
            }

            UpdateCandidate? candidate;
            try
            {
                candidate = await UpdateChecker.CheckAsync(source, RunningVersion, channel, cancellation).ConfigureAwait(false);
            }
            catch (UpdateException ex)
            {
                return new UpdateOutcome(Skipped(ex.Message));
            }

            if (candidate is null)
            {
                return command
                    ? new UpdateOutcome($"[grey]Times is up to date ({Markup.Escape(running.ToString())}).[/]")
                    : UpdateOutcome.Nothing;
            }

            string version = Markup.Escape(candidate.Version.ToString());
            if (!command && mode == SettingsRegistry.UpdateNotifyOnly)
            {
                return new UpdateOutcome($"[steelblue1]Times {version} is available. Type update to install it when you quit.[/]");
            }

            return await StageAsync(candidate, dataFolder, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new UpdateOutcome(Skipped(ex.Message));
        }
    }

    private async Task<UpdateOutcome> StageAsync(UpdateCandidate candidate, string dataFolder, CancellationToken cancellation)
    {
        if (!signature.HasKeys)
        {
            return new UpdateOutcome(NoKeyHint);
        }

        if (Install is null)
        {
            return new UpdateOutcome(NotInstalledHint);
        }

        string? refused = Applier.RefusePaths(dataFolder, Install.Folder, Path.GetTempPath());
        if (refused is not null)
        {
            return new UpdateOutcome(UpdateSkipped(refused));
        }

        StagedUpdate staged;
        try
        {
            staged = await new UpdateDownloader(source, signature, createFolder).DownloadAsync(candidate, cancellation).ConfigureAwait(false);
        }
        catch (UpdateException ex)
        {
            return new UpdateOutcome(UpdateSkipped(ex.Message));
        }

        refused = Applier.RefusePaths(staged.Folder);
        if (refused is not null)
        {
            UpdateDownloader.DeleteFolder(staged.Folder);
            return new UpdateOutcome(UpdateSkipped(refused));
        }

        return new UpdateOutcome($"[grey]Times {Markup.Escape(staged.Version.ToString())} is ready and installs when you quit.[/]", staged);
    }

    private static string Skipped(string reason) => $"[grey]Update check skipped: {Markup.Escape(reason)}.[/]";

    private static string UpdateSkipped(string reason) => $"[grey]Update skipped: {Markup.Escape(reason)}.[/]";
}
