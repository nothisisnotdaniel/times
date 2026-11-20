using Times.Tray;

namespace Times.Tests;

/// <summary>
/// A scripted <see cref="IProcessRunner"/>: records each call and answers from <see cref="Answer"/>; a test can hold
/// calls open with <see cref="Hold"/> until it completes the returned source.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly object gate = new();
    private readonly List<(string FileName, string[] Arguments)> calls = [];
    private readonly Queue<TaskCompletionSource<ProcessResult>> held = new();

    /// <summary>Answers a call by its arguments joined with spaces; the default answers like a working day.</summary>
    public Func<string, ProcessResult> Answer { get; set; } = arguments => arguments == "status --json"
        ? ProcessResult.Exited(0, TrayStatusTests.Working)
        : ProcessResult.Exited(0, string.Empty);

    /// <summary>Every call so far, in order.</summary>
    public List<(string FileName, string[] Arguments)> Calls
    {
        get
        {
            lock (gate)
            {
                return calls.ToList();
            }
        }
    }

    /// <summary>The joined arguments of every call.</summary>
    public List<string> CommandLines => Calls.Select(c => string.Join(' ', c.Arguments)).ToList();

    /// <summary>Makes the next call wait until the returned source is completed.</summary>
    public TaskCompletionSource<ProcessResult> Hold()
    {
        TaskCompletionSource<ProcessResult> source =
            new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            held.Enqueue(source);
        }

        return source;
    }

    /// <summary>Completes when the runner has received <paramref name="count"/> calls.</summary>
    public Task WhenCalls(int count) => WaitUntil(() => Calls.Count >= count);

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        TaskCompletionSource<ProcessResult>? source;
        lock (gate)
        {
            calls.Add((fileName, arguments.ToArray()));
            held.TryDequeue(out source);
            Monitor.PulseAll(gate);
        }

        return source?.Task ?? Task.FromResult(Answer(string.Join(' ', arguments)));
    }

    private Task WaitUntil(Func<bool> condition) => Task.Run(() =>
    {
        lock (gate)
        {
            while (!condition())
            {
                Monitor.Wait(gate);
            }
        }
    });
}
