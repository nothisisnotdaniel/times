namespace Times.Tests;

/// <summary>
/// A Context command line for tests: records every argument list and answers <c>todos</c>, <c>capture</c> and
/// <c>current</c> with scripted runs, or <c>null</c> (not installed). Never starts a process.
/// </summary>
internal sealed class FakeContextCli : IContextCli
{
    private readonly ContextRun? todos;
    private readonly ContextRun? capture;
    private readonly ContextRun? current;

    public FakeContextCli(ContextRun? todos, ContextRun? capture = null, ContextRun? current = null)
    {
        this.todos = todos;
        this.capture = capture ?? new ContextRun(0, string.Empty, timedOut: false);
        this.current = current;
    }

    /// <summary>A Context whose <c>current --json</c> answers with <paramref name="current"/>.</summary>
    public static FakeContextCli Current(ContextRun current) => new FakeContextCli(null, null, current);

    /// <summary>A Context that is not installed: every run returns <c>null</c>.</summary>
    public static FakeContextCli Missing() => new FakeContextCli(null, null, missing: true);

    private FakeContextCli(ContextRun? todos, ContextRun? capture, bool missing)
    {
        this.todos = todos;
        this.capture = missing ? null : capture;
    }

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public ContextRun? Run(IReadOnlyList<string> arguments)
    {
        Calls.Add(arguments.ToArray());
        if (arguments.Count > 0 && arguments[0] == "current")
        {
            return current;
        }

        return arguments.Count > 0 && arguments[0] == "capture" ? capture : todos;
    }

    /// <summary>A successful <c>todos --json</c> run with <paramref name="json"/> as its output.</summary>
    public static ContextRun Ok(string json) => new ContextRun(0, json, timedOut: false);
}
