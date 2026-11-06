using Spectre.Console;
using Times;

// An empty app-data path stays empty, so App's folder check catches it instead of writing to "Times".
DataFolder dataFolder = DataFolder.Resolve(
    Environment.GetEnvironmentVariable(DataFolder.VariableName),
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

// On Windows the console input mode turns mouse input on, and wheel records reach the line editor as
// the same keys a Unix terminal sends. Without a real console (redirected input) nothing is swapped.
IAnsiConsole console = AnsiConsole.Console;
IConsoleInputMode? inputMode = null;
if (OperatingSystem.IsWindows())
{
    WindowsConsoleMode windowsMode = new WindowsConsoleMode();
    if (windowsMode.Get() is not null)
    {
        inputMode = windowsMode;
        console = new InputOverrideConsole(console, new WindowsConsoleInput());
    }
}

// Ctrl+C and any other way out also turn mouse reporting off, so the shell is left as it was.
MouseReporting mouse = new MouseReporting(console, inputMode);
Console.CancelKeyPress += (_, _) => mouse.Disable();
AppDomain.CurrentDomain.ProcessExit += (_, _) => mouse.Disable();

// Updates come from the GitHub releases and are checked against the embedded key; the helper runs after quit.
UpdateService updates = new UpdateService(
    new GitHubReleaseSource(AppVersion.Current),
    UpdateSignature.Embedded(),
    ToolInstall.Current(),
    new UpdateApplier(new ProcessLauncher(), OperatingSystem.IsWindows()),
    AppVersion.Current);

// The Azure DevOps token lives only in the OS credential store (Keychain or Credential Manager).
App app = new App(console, TimeProvider.System, new DayStore(dataFolder.Path),
    folderFromEnvironment: dataFolder.FromEnvironment, mouse: mouse, updates: updates,
    tokenStore: TokenStore.ForCurrentPlatform(), devOpsClient: credential => new AzureDevOpsClient(credential));
return app.Run(args);
