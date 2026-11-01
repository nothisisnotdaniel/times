using System.Diagnostics;

namespace Times.Tests;

/// <summary>Helpers for tests that need a FIFO, and for calls that might block on one.</summary>
internal static class SpecialFiles
{
    /// <summary>Creates a FIFO at <paramref name="path"/> with <c>mkfifo</c>. Not for Windows.</summary>
    public static void MakeFifo(string path)
    {
        ProcessStartInfo start = new ProcessStartInfo("mkfifo") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        using Process process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"mkfifo failed with exit code {process.ExitCode}.");
        }
    }

    /// <summary>Whether <paramref name="path"/> itself (not followed) is a FIFO.</summary>
    public static bool IsFifo(string path)
    {
        ProcessStartInfo start = new ProcessStartInfo("ls") { UseShellExecute = false, RedirectStandardOutput = true };
        start.ArgumentList.Add("-ld");
        start.ArgumentList.Add(path);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.StartsWith('p');
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a worker thread and fails the test if it has not finished
    /// within <paramref name="timeout"/>, so a blocked call cannot stall the run. Returns the
    /// exception it threw, or null.
    /// </summary>
    public static Exception? RunWithin(TimeSpan timeout, Action action)
    {
        Exception? thrown = null;
        Task task = Task.Run(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });
        Assert.True(task.Wait(timeout), $"The call did not finish within {timeout.TotalSeconds} s.");
        return thrown;
    }
}
