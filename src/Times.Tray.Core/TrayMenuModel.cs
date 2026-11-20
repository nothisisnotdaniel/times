namespace Times.Tray;

/// <summary>Which of the two tray icons shows.</summary>
internal enum TrayIconState
{
    /// <summary>Checked in: working or on a break.</summary>
    In,

    /// <summary>Not checked in, absent, loading the first time or an error.</summary>
    Out,
}

/// <summary>An action row; each runs the <c>tm</c> one-shot of the same name with no time, so "now".</summary>
internal enum TrayAction
{
    Arrive,
    Break,
    BreakEnd,
    Leave,
}

/// <summary>
/// What the tray menu shows, as plain data the shell maps onto the native menu: the disabled info rows on top, which
/// action rows are enabled (Refresh and Quit always are), the icon and the tooltip. Built only from cleaned text.
/// </summary>
internal sealed class TrayMenuModel
{
    /// <summary>The first info row while a command runs.</summary>
    public const string LoadingRow = "Loading…";

    private readonly TrayAction[] enabled;

    private TrayMenuModel(IReadOnlyList<string> infoRows, TrayAction[] enabled, TrayIconState icon, string tooltip)
    {
        InfoRows = infoRows;
        this.enabled = enabled;
        Icon = icon;
        Tooltip = tooltip;
    }

    /// <summary>The disabled rows that show the state; never empty.</summary>
    public IReadOnlyList<string> InfoRows { get; }

    public TrayIconState Icon { get; }

    /// <summary><c>Times · &lt;first info row&gt;</c>, then <c> · &lt;left row&gt;</c> when there is one.</summary>
    public string Tooltip { get; }

    /// <summary>The model before the first answer: <see cref="LoadingRow"/> and no action.</summary>
    public static TrayMenuModel Initial { get; } = new([LoadingRow], [], TrayIconState.Out, "Times · " + LoadingRow);

    /// <summary>True if the action's row is enabled.</summary>
    public bool CanRun(TrayAction action) => enabled.Contains(action);

    /// <summary>The model for a status <c>tm</c> reported.</summary>
    public static TrayMenuModel FromStatus(TrayStatus status)
    {
        List<string> rows = [StateRow(status)];
        if (status.Checkpoint is { } checkpoint)
        {
            string name = TrayText.Clean(checkpoint.Name, TrayText.MaxNameLength);
            string item = checkpoint.WorkItem is { } workItem ? $" (#{workItem})" : string.Empty;
            rows.Add($"Checkpoint: {name}{item} since {TrayText.Time(checkpoint.Since)}");
        }

        string left = status.LeftMinutes < 0
            ? $"Over target: {TrayText.Duration(status.LeftMinutes)}"
            : $"Left today: {TrayText.Duration(status.LeftMinutes)}";
        rows.Add(left);

        TrayAction[] actions = status.State switch
        {
            TrayState.Out => [TrayAction.Arrive],
            TrayState.Working => [TrayAction.Break, TrayAction.Leave],
            TrayState.Break => [TrayAction.BreakEnd],
            _ => [],
        };
        TrayIconState icon = status.State is TrayState.Working or TrayState.Break ? TrayIconState.In : TrayIconState.Out;
        return new TrayMenuModel(rows, actions, icon, $"Times · {rows[0]} · {left}");
    }

    /// <summary>An error in place of the status: one info row, no action, the out icon.</summary>
    public static TrayMenuModel FromError(string message) =>
        new([message], [], TrayIconState.Out, "Times · " + message);

    /// <summary>This model while a command runs: the first row is <see cref="LoadingRow"/>, no action, same icon.</summary>
    public TrayMenuModel Loading()
    {
        List<string> rows = [LoadingRow, .. InfoRows.Skip(1)];
        string tooltip = "Times · " + LoadingRow;
        if (rows.Count > 1 && (rows[^1].StartsWith("Left today: ", StringComparison.Ordinal)
            || rows[^1].StartsWith("Over target: ", StringComparison.Ordinal)))
        {
            tooltip += " · " + rows[^1];
        }

        return new TrayMenuModel(rows, [], Icon, tooltip);
    }

    private static string StateRow(TrayStatus status)
    {
        string since = status.Since is { } time ? " since " + TrayText.Time(time) : string.Empty;
        return status.State switch
        {
            TrayState.Working => "Working" + since,
            TrayState.Break => "On a break" + since,
            TrayState.Absent => "Absent today",
            _ => "Not checked in",
        };
    }
}
