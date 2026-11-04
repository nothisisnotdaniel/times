using System.Diagnostics;
using System.Text.Json;

namespace Times.Tests;

public sealed class UpdateApplierTests : IDisposable
{
    private readonly TempFolder temp = new();

    public void Dispose() => temp.Dispose();

    private static SemVersion V(string text)
    {
        Assert.True(SemVersion.TryParse(text, out SemVersion version));
        return version;
    }

    private static ToolInstall Install(string toolsFolder, bool global)
    {
        string dll = Path.Combine(toolsFolder, ".store", "times", "1.0.0", "times", "1.0.0", "tools", "net10.0", "any", "Times.dll");
        ToolInstall? install = ToolInstall.Detect(dll, global ? toolsFolder : null);
        Assert.NotNull(install);
        return install;
    }

    private StagedUpdate Staged(string version, string? folder = null)
    {
        string path = folder ?? Path.Combine(temp.Path, "times-update-1");
        Directory.CreateDirectory(path);
        return new StagedUpdate(V(version), path, Path.Combine(path, $"Times.{version}.nupkg"));
    }

    // AC-9: Unix: nohup sh -c '<fixed script>' sh <pid> <log> <staged> <dotnet tool update args>, paths only positional.
    [Fact]
    public void HelperCall_Unix_PassesPathsOnlyAsPositionalArguments()
    {
        StagedUpdate staged = Staged("1.2.0");
        string data = Path.Combine(temp.Path, "data");
        UpdateApplier applier = new UpdateApplier(new FakeLauncher(), windows: false);

        ProcessCall call = applier.HelperCall(staged, Install(Path.Combine(temp.Path, "tools"), global: true), data, 4242);

        Assert.Equal("nohup", call.FileName);
        Assert.Null(call.CommandLine);
        Assert.Equal(
            ["sh", "-c", UpdateApplier.UnixScript, "sh", "4242", Path.Combine(data, "update.log"), staged.Folder,
                "Times", "-g", "--add-source", staged.Folder, "--version", "1.2.0"],
            call.Arguments);
        Assert.DoesNotContain(temp.Path, UpdateApplier.UnixScript, StringComparison.Ordinal);
    }

    // AC-9: --tool-path for a tool-path install, --prerelease for a prerelease.
    [Fact]
    public void HelperCall_Unix_ToolPathAndPrerelease()
    {
        StagedUpdate staged = Staged("1.2.0-develop.7");
        string tools = Path.Combine(temp.Path, "my tools");
        UpdateApplier applier = new UpdateApplier(new FakeLauncher(), windows: false);

        ProcessCall call = applier.HelperCall(staged, Install(tools, global: false), temp.Path, 1);

        Assert.Equal(
            ["Times", "--tool-path", tools, "--add-source", staged.Folder, "--version", "1.2.0-develop.7", "--prerelease"],
            call.Arguments.Skip(7));
    }

    // AC-9: the fixed script waits for the PID, runs dotnet tool update with the arguments as given,
    // appends everything to the log, reads nothing from stdin and deletes the staged folder. A fake
    // dotnet on PATH records its arguments: no real dotnet tool update runs, and odd paths stay data.
    [Fact]
    public void UnixScript_RunsTheUpdateAndCleansUp()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string bin = Directory.CreateDirectory(Path.Combine(temp.Path, "bin")).FullName;
        string recorded = Path.Combine(temp.Path, "args.txt");
        File.WriteAllText(Path.Combine(bin, "dotnet"), "#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\"; done > \"$RECORD\"\necho fake dotnet output\nexit 3\n");
        File.SetUnixFileMode(Path.Combine(bin, "dotnet"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string odd = Path.Combine(temp.Path, "a b $(touch pwned) `id` ; 'q' \"d\"");
        StagedUpdate staged = Staged("1.2.0", odd);
        File.WriteAllText(staged.PackagePath, "pkg");
        string data = Directory.CreateDirectory(Path.Combine(temp.Path, "da ta")).FullName;
        ProcessCall call = new UpdateApplier(new FakeLauncher(), windows: false)
            .HelperCall(staged, Install(Path.Combine(temp.Path, "tools"), global: false), data, ExitedPid());

        // Run what nohup would run (sh and its arguments), and wait here for the test.
        ProcessStartInfo info = new ProcessStartInfo(call.Arguments[0]) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in call.Arguments.Skip(1))
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH");
        info.Environment["RECORD"] = recorded;
        using (Process process = Process.Start(info)!)
        {
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(20000));
            Assert.Equal(string.Empty, stdout + stderr);
        }

        Assert.Equal(["tool", "update", .. call.Arguments.Skip(7)], File.ReadAllLines(recorded));
        string log = File.ReadAllText(Path.Combine(data, "update.log"));
        Assert.Contains("fake dotnet output", log, StringComparison.Ordinal);
        Assert.Contains("exit code 3", log, StringComparison.Ordinal);
        Assert.False(Directory.Exists(odd));
        Assert.False(File.Exists(Path.Combine(temp.Path, "pwned")));
        Assert.False(File.Exists("pwned"));
    }

    // A PID that has exited, so the script's wait ends at once.
    private static int ExitedPid()
    {
        using Process process = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!;
        process.WaitForExit();
        return process.Id;
    }

    // AC-9: Windows: cmd /c, with the paths quoted (they were checked before), the wait, the log and the clean-up.
    [Fact]
    public void HelperCall_Windows_UsesCmd()
    {
        StagedUpdate staged = new StagedUpdate(V("1.2.0-beta.1"), @"C:\Users\Dan L\AppData\Local\Temp\times-update-x", @"C:\Users\Dan L\AppData\Local\Temp\times-update-x\Times.1.2.0-beta.1.nupkg");
        ToolInstall install = new ToolInstall(global: true, @"C:\Users\Dan L\.dotnet\tools");
        UpdateApplier applier = new UpdateApplier(new FakeLauncher(), windows: true);

        ProcessCall call = applier.HelperCall(staged, install, @"C:\Users\Dan L\AppData\Roaming\Times", 77);

        Assert.Equal("cmd", call.FileName);
        Assert.Equal(["/d", "/s", "/c"], call.Arguments.Take(3));
        string script = call.Arguments[3];
        Assert.Equal("/d /s /c \"" + script + "\"", call.CommandLine);
        Assert.Contains("tasklist /fi \"PID eq 77\"", script, StringComparison.Ordinal);
        Assert.Contains("(1,1,30)", script, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet tool update Times -g --add-source \"C:\\Users\\Dan L\\AppData\\Local\\Temp\\times-update-x\" --version 1.2.0-beta.1 --prerelease <nul >>\"" + Path.Combine(@"C:\Users\Dan L\AppData\Roaming\Times", "update.log") + "\" 2>&1",
            script, StringComparison.Ordinal);
        Assert.EndsWith("rmdir /s /q \"C:\\Users\\Dan L\\AppData\\Local\\Temp\\times-update-x\"", script, StringComparison.Ordinal);
    }

    // AC-9: Windows refuses paths with characters cmd treats specially, or control characters.
    [Theory]
    [InlineData("\"")]
    [InlineData("%")]
    [InlineData("!")]
    [InlineData("^")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("\n")]
    [InlineData("\u0007")]
    public void RefusePaths_Windows_RefusesSpecialCharacters(string character)
    {
        UpdateApplier windows = new UpdateApplier(new FakeLauncher(), windows: true);
        UpdateApplier unix = new UpdateApplier(new FakeLauncher(), windows: false);
        string path = @"C:\Users\x" + character + "y";

        Assert.NotNull(windows.RefusePaths(@"C:\fine", path));
        Assert.Null(unix.RefusePaths(@"C:\fine", path));
    }

    // AC-9
    [Fact]
    public void RefusePaths_Windows_AllowsOrdinaryPaths()
    {
        UpdateApplier windows = new UpdateApplier(new FakeLauncher(), windows: true);

        Assert.Null(windows.RefusePaths(@"C:\Users\Dan L\AppData\Roaming\Times", @"C:\Program Files (x86)\tools", @"D:\a-b_c.d\ü"));
    }

    // AC-9: Apply writes update-pending.json first, then starts the helper exactly once.
    [Fact]
    public void Apply_WritesPendingThenLaunchesOnce()
    {
        StagedUpdate staged = Staged("1.2.0");
        string data = Path.Combine(temp.Path, "data");
        bool pendingBeforeLaunch = false;
        PendingCheckingLauncher launcher = new PendingCheckingLauncher(Path.Combine(data, "update-pending.json"), seen => pendingBeforeLaunch = seen);
        UpdateApplier applier = new UpdateApplier(launcher, windows: false);
        ToolInstall install = Install(Path.Combine(temp.Path, "tools"), global: true);
        DateTimeOffset now = new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.Zero);

        string? failed = applier.Apply(staged, install, data, 4242, now);

        Assert.Null(failed);
        Assert.True(pendingBeforeLaunch);
        ProcessCall call = Assert.Single(launcher.Calls);
        Assert.Equal(applier.HelperCall(staged, install, data, 4242), call, new CallComparer());
        using JsonDocument pending = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "update-pending.json")));
        Assert.Equal("1.2.0", pending.RootElement.GetProperty("version").GetString());
        Assert.Equal(now, pending.RootElement.GetProperty("time").GetDateTimeOffset());
    }

    // AC-9: a helper that cannot start is reported, not thrown.
    [Fact]
    public void Apply_LauncherFails_ReturnsTheReason()
    {
        FakeLauncher launcher = new FakeLauncher { Failure = new System.ComponentModel.Win32Exception("no sh") };
        UpdateApplier applier = new UpdateApplier(launcher, windows: false);

        string? failed = applier.Apply(Staged("1.2.0"), Install(Path.Combine(temp.Path, "tools"), true), temp.Path, 1, DateTimeOffset.UnixEpoch);

        Assert.Equal("no sh", failed);
    }

    // AC-11: the pending file is read and deleted; the same version means it worked.
    [Theory]
    [InlineData("1.2.0", "1.2.0", "[grey]Updated to 1.2.0.[/]")]
    [InlineData("1.2.0", "1.1.0", "[red]Update to 1.2.0 failed; see update.log in the data folder.[/]")]
    [InlineData("1.2.0-develop.3", "unknown", "[red]Update to 1.2.0-develop.3 failed; see update.log in the data folder.[/]")]
    public void ReadOutcome_ReportsAndDeletes(string target, string current, string expected)
    {
        string path = Path.Combine(temp.Path, "update-pending.json");
        File.WriteAllText(path, "{\"version\":\"" + target + "\",\"time\":\"2026-11-04T09:30:00+00:00\"}");

        Assert.Equal(expected, UpdateApplier.ReadOutcome(temp.Path, current));
        Assert.False(File.Exists(path));
        Assert.Null(UpdateApplier.ReadOutcome(temp.Path, current));
    }

    // AC-11: a broken pending file is deleted and ignored.
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"version\":5}")]
    [InlineData("{\"version\":\"[red]x[/]\"}")]
    [InlineData("{\"time\":\"2026-11-04\"}")]
    public void ReadOutcome_BrokenFile_IsDeletedAndIgnored(string content)
    {
        string path = Path.Combine(temp.Path, "update-pending.json");
        File.WriteAllText(path, content);

        Assert.Null(UpdateApplier.ReadOutcome(temp.Path, "1.0.0"));
        Assert.False(File.Exists(path));
    }

    // AC-11: an oversized pending file is read through CappedFile, deleted and ignored.
    [Fact]
    public void ReadOutcome_TooLarge_IsDeletedAndIgnored()
    {
        string path = Path.Combine(temp.Path, "update-pending.json");
        File.WriteAllText(path, "{\"version\":\"1.0.0\",\"pad\":\"" + new string('x', 10000) + "\"}");

        Assert.Null(UpdateApplier.ReadOutcome(temp.Path, "1.0.0"));
        Assert.False(File.Exists(path));
    }

    // AC-11: no pending file, or a folder in its place, is nothing.
    [Fact]
    public void ReadOutcome_MissingOrNotAFile_IsNothing()
    {
        Assert.Null(UpdateApplier.ReadOutcome(temp.Path, "1.0.0"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "update-pending.json"));
        Assert.Null(UpdateApplier.ReadOutcome(temp.Path, "1.0.0"));
    }

    private sealed class PendingCheckingLauncher : IProcessLauncher
    {
        private readonly string pending;
        private readonly Action<bool> seen;

        public PendingCheckingLauncher(string pending, Action<bool> seen)
        {
            this.pending = pending;
            this.seen = seen;
        }

        public List<ProcessCall> Calls { get; } = [];

        public void Start(ProcessCall call)
        {
            seen(File.Exists(pending));
            Calls.Add(call);
        }
    }

    private sealed class CallComparer : IEqualityComparer<ProcessCall>
    {
        public bool Equals(ProcessCall? x, ProcessCall? y) =>
            x is not null && y is not null && x.FileName == y.FileName && x.CommandLine == y.CommandLine && x.Arguments.SequenceEqual(y.Arguments);

        public int GetHashCode(ProcessCall obj) => obj.FileName.GetHashCode(StringComparison.Ordinal);
    }
}
