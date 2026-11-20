using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Times.Tray;

/// <summary>How a run of <c>tm</c> ended.</summary>
internal enum ProcessOutcome
{
    /// <summary>The process exited; <see cref="ProcessResult.ExitCode"/> and <see cref="ProcessResult.Output"/> hold what it gave.</summary>
    Exited,

    /// <summary>It did not exit in time and was killed with its process tree.</summary>
    TimedOut,

    /// <summary>It wrote more than <see cref="ProcessRunner.MaxOutputBytes"/> to stdout and was killed.</summary>
    TooMuchOutput,

    /// <summary>It could not be started at all.</summary>
    FailedToStart,
}

/// <summary>The result of one run.</summary>
internal sealed class ProcessResult
{
    public ProcessResult(ProcessOutcome outcome, int exitCode, string output)
    {
        Outcome = outcome;
        ExitCode = exitCode;
        Output = output;
    }

    public ProcessOutcome Outcome { get; }

    /// <summary>The exit code, only meaningful for <see cref="ProcessOutcome.Exited"/>.</summary>
    public int ExitCode { get; }

    /// <summary>The stdout text (UTF-8), empty unless <see cref="ProcessOutcome.Exited"/>.</summary>
    public string Output { get; }

    public static ProcessResult Exited(int exitCode, string output) => new(ProcessOutcome.Exited, exitCode, output);

    public static ProcessResult Failed(ProcessOutcome outcome) => new(outcome, -1, string.Empty);
}

/// <summary>Runs a program with an argument list and waits for its result.</summary>
internal interface IProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> with <paramref name="arguments"/>; never throws for the program's own failures.</summary>
    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments);
}

/// <summary>
/// Starts the program directly (no shell), each argument through <see cref="ProcessStartInfo.ArgumentList"/>, with the
/// tray's own environment (so <c>TIMES_DATA_DIR</c> reaches <c>tm</c>), stdin closed and no window. Reads at most
/// <see cref="MaxOutputBytes"/> of stdout (more is <see cref="ProcessOutcome.TooMuchOutput"/>), drops stderr, and kills
/// the process tree after the timeout (<see cref="ProcessOutcome.TimedOut"/>).
/// </summary>
internal sealed class ProcessRunner : IProcessRunner
{
    /// <summary>The most stdout bytes read; one more is a failure.</summary>
    public const int MaxOutputBytes = 64 * 1024;

    /// <summary>How long a run may take before it is killed.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan timeout;
    private readonly TimeProvider time;

    public ProcessRunner()
        : this(DefaultTimeout, TimeProvider.System)
    {
    }

    public ProcessRunner(TimeSpan timeout, TimeProvider time)
    {
        this.timeout = timeout;
        this.time = time;
    }

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                return ProcessResult.Failed(ProcessOutcome.FailedToStart);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return ProcessResult.Failed(ProcessOutcome.FailedToStart);
        }

        process.StandardInput.Close();
        Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);

        // Killing the tree closes its pipes, so the read below ends on a timeout too.
        bool timedOut = false;
        using CancellationTokenSource deadline = new CancellationTokenSource(timeout, time);
        using CancellationTokenRegistration onTimeout = deadline.Token.Register(() =>
        {
            timedOut = true;
            Kill(process);
        });

        byte[] buffer = new byte[MaxOutputBytes + 1];
        int total = 0;
        try
        {
            Stream stdout = process.StandardOutput.BaseStream;
            while (total < buffer.Length)
            {
                int read = await stdout.ReadAsync(buffer.AsMemory(total)).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // A killed process may break the pipe; the outcome is decided below.
        }

        if (total > MaxOutputBytes)
        {
            Kill(process);
            await WaitQuietly(process, stderr).ConfigureAwait(false);
            return ProcessResult.Failed(ProcessOutcome.TooMuchOutput);
        }

        await WaitQuietly(process, stderr).ConfigureAwait(false);
        await onTimeout.DisposeAsync().ConfigureAwait(false);
        if (timedOut)
        {
            return ProcessResult.Failed(ProcessOutcome.TimedOut);
        }

        return ProcessResult.Exited(process.ExitCode, Encoding.UTF8.GetString(buffer, 0, total));
    }

    // Waits for the exit (a killed process exits at once) and for stderr to drain; errors are ignored.
    private static async Task WaitQuietly(Process process, Task stderr)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Nothing more to collect.
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}
