using Spectre.Console;

namespace Times.Tests;

/// <summary>
/// A background event queue for tests: it releases the events given for the n-th call of
/// <see cref="WaitForKeyOrEvent"/> (1-based) at that call, never blocks and never calls
/// <c>keyAvailable</c>. Every wait is logged, so tests can check the order of waits and applies.
/// </summary>
internal sealed class ScriptedEvents : IBackgroundEvents
{
    private readonly Queue<BackgroundEvent> queue = new();
    private readonly Dictionary<int, List<BackgroundEvent>> scripted = [];

    /// <summary>How often <see cref="WaitForKeyOrEvent"/> was called.</summary>
    public int Waits { get; private set; }

    /// <summary>"wait" for each wait; tests add their own entries (for example from an event's apply).</summary>
    public List<string> Log { get; } = [];

    /// <summary>Called with the wait's number at every wait, after its events were released.</summary>
    public Action<int>? OnWait { get; set; }

    /// <summary>Releases <paramref name="events"/>, in order, at the <paramref name="call"/>-th wait.</summary>
    public ScriptedEvents At(int call, params BackgroundEvent[] events)
    {
        if (!scripted.TryGetValue(call, out List<BackgroundEvent>? list))
        {
            list = [];
            scripted[call] = list;
        }

        list.AddRange(events);
        return this;
    }

    public void Post(BackgroundEvent e) => queue.Enqueue(e);

    public bool TryTake(out BackgroundEvent? e) => queue.TryDequeue(out e);

    public void WaitForKeyOrEvent(Func<bool> keyAvailable)
    {
        Waits++;
        Log.Add("wait");
        if (scripted.TryGetValue(Waits, out List<BackgroundEvent>? list))
        {
            foreach (BackgroundEvent e in list)
            {
                queue.Enqueue(e);
            }
        }

        OnWait?.Invoke(Waits);
    }
}

/// <summary>Wraps a console's input: counts <c>IsKeyAvailable</c> calls and runs a hook before each key is returned.</summary>
internal sealed class HookedInput : IAnsiConsoleInput
{
    private readonly IAnsiConsoleInput inner;

    public HookedInput(IAnsiConsoleInput inner)
    {
        this.inner = inner;
    }

    public int KeyAvailableCalls { get; private set; }

    public int KeysRead { get; private set; }

    /// <summary>Called with the key's number (1-based) right before the key is handed out.</summary>
    public Action<int>? BeforeKey { get; set; }

    /// <summary>
    /// Called with the call's number (1-based) at every <c>IsKeyAvailable</c>; a non-null result is
    /// returned instead of asking the inner input (for example "no key" while it changes the size).
    /// </summary>
    public Func<int, bool?>? OnKeyAvailable { get; set; }

    public bool IsKeyAvailable()
    {
        KeyAvailableCalls++;
        if (OnKeyAvailable?.Invoke(KeyAvailableCalls) is { } answer)
        {
            return answer;
        }

        return inner.IsKeyAvailable();
    }

    public ConsoleKeyInfo? ReadKey(bool intercept)
    {
        ConsoleKeyInfo? key = inner.ReadKey(intercept);
        if (key is not null)
        {
            KeysRead++;
            BeforeKey?.Invoke(KeysRead);
        }

        return key;
    }

    public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken) =>
        Task.FromResult(ReadKey(intercept));
}

/// <summary>
/// Wraps another queue and records, for every posted event, what its apply returned and the output length
/// at that moment (via <see cref="OnApply"/>), so tests can see what a producer's events did.
/// </summary>
internal sealed class ObservedEvents : IBackgroundEvents
{
    private readonly IBackgroundEvents inner;

    public ObservedEvents(IBackgroundEvents inner)
    {
        this.inner = inner;
    }

    /// <summary>What each applied event returned, in order (a throwing event adds nothing).</summary>
    public List<bool> Results { get; } = [];

    /// <summary>Called right before each posted event is applied.</summary>
    public Action? OnApply { get; set; }

    public void Post(BackgroundEvent e) => inner.Post(new BackgroundEvent(() =>
    {
        OnApply?.Invoke();
        bool changed = e.Apply();
        Results.Add(changed);
        return changed;
    }));

    public bool TryTake(out BackgroundEvent? e) => inner.TryTake(out e);

    public void WaitForKeyOrEvent(Func<bool> keyAvailable) => inner.WaitForKeyOrEvent(keyAvailable);
}
