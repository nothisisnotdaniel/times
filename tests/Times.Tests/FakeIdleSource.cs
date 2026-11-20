using Times.Tray;

namespace Times.Tests;

/// <summary>An <see cref="IIdleSource"/> a test sets by hand; starts active (no idle time).</summary>
internal sealed class FakeIdleSource : IIdleSource
{
    private readonly object gate = new();
    private TimeSpan? since = TimeSpan.Zero;

    /// <summary>What <see cref="SinceLastInput"/> answers.</summary>
    public TimeSpan? Since
    {
        get
        {
            lock (gate)
            {
                return since;
            }
        }

        set
        {
            lock (gate)
            {
                since = value;
            }
        }
    }

    public TimeSpan? SinceLastInput() => Since;
}
