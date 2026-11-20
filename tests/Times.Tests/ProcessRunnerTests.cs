using Times.Tray;

namespace Times.Tests;

// Job 306, AC-4: the only tests that start real processes. The portable one runs the test host's own dotnet; the
// argument, environment, cap and timeout checks use /bin/sh and are skipped on Windows. Waits are on the result only.
public sealed class ProcessRunnerTests
{
    // Bounds a whole run, so a broken runner fails the test instead of hanging it.
    private static readonly TimeSpan RunBound = TimeSpan.FromSeconds(60);

    private static async Task<ProcessResult> Run(ProcessRunner runner, string fileName, params string[] arguments) =>
        await runner.RunAsync(fileName, arguments).WaitAsync(RunBound);

    private static async Task<ProcessResult> Shell(string script, params string[] arguments) =>
        await Run(new ProcessRunner(), "/bin/sh", ["-c", script, "sh", .. arguments]);

    // The dotnet the tests run under: DOTNET_HOST_PATH as set by dotnet test, else this process when it is dotnet.
    private static string? Dotnet()
    {
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host) && File.Exists(host))
        {
            return host;
        }

        string? process = Environment.ProcessPath;
        return process is not null && Path.GetFileNameWithoutExtension(process) == "dotnet" ? process : null;
    }

    [Fact]
    public async Task Dotnet_Version_ExitsZero_WithItsOutput()
    {
        string? dotnet = Dotnet();
        Assert.NotNull(dotnet);

        ProcessResult result = await Run(new ProcessRunner(), dotnet, "--version");

        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"^\d+\.\d+\.\d+", result.Output);
    }

    [Fact]
    public async Task MissingProgram_FailedToStart()
    {
        ProcessResult result = await Run(new ProcessRunner(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.Equal(ProcessOutcome.FailedToStart, result.Outcome);
    }

    [Fact]
    public async Task Arguments_ReachTheProgramAsGiven_NoShellInBetween()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessResult result = await Shell("printf '%s|' \"$@\"", "a b", "$(echo hi)", "x;y", "*", "\"q\"");

        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.Equal("a b|$(echo hi)|x;y|*|\"q\"|", result.Output);
    }

    [Fact]
    public async Task Environment_IsInherited()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A name of its own, so no other test can see or change it.
        string name = "TIMES_TRAY_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "inherited");
        try
        {
            ProcessResult result = await Shell($"printf %s \"${name}\"");

            Assert.Equal("inherited", result.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task ExitCode_AndOutput_AreReturned_StderrIsDropped()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessResult result = await Shell("echo out; echo err >&2; exit 3");

        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out\n", result.Output);
    }

    [Fact]
    public async Task ExactlyTheCap_IsRead()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessResult result = await Shell($"head -c {ProcessRunner.MaxOutputBytes} /dev/zero | tr '\\0' a");

        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.Equal(ProcessRunner.MaxOutputBytes, result.Output.Length);
    }

    [Fact]
    public async Task MoreThanTheCap_IsAFailure()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessResult result = await Shell($"head -c {ProcessRunner.MaxOutputBytes + 1} /dev/zero | tr '\\0' a; sleep 30");

        Assert.Equal(ProcessOutcome.TooMuchOutput, result.Outcome);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task Hanging_IsKilled_TimedOut()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A short timeout instead of the 10 s default; the child would sleep far longer than RunBound.
        ProcessRunner runner = new ProcessRunner(TimeSpan.FromMilliseconds(300), TimeProvider.System);

        ProcessResult result = await Run(runner, "/bin/sh", "-c", "sleep 600 & wait");

        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public void DefaultTimeout_IsTenSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), ProcessRunner.DefaultTimeout);
        Assert.Equal(64 * 1024, ProcessRunner.MaxOutputBytes);
    }
}
