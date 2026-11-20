namespace Times.Tray;

/// <summary>
/// Keeps the tray's <see cref="Model"/> in step with <c>tm</c>: refreshes with <c>tm status --json</c> at
/// <see cref="Start"/>, every <see cref="RefreshInterval"/> (a <see cref="TimeProvider"/> timer), when the menu opens
/// and after every action, and runs the actions. Only one operation runs at a time: a refresh asked for while one runs
/// joins it, and a second action while one runs is ignored. <see cref="Changed"/> is raised on a worker thread.
/// After each refresh that <c>tm</c> answered, <see cref="CheckInReminder.IsDue"/> is checked against the clock and the
/// <see cref="IIdleSource"/>; <see cref="ReminderDue"/> is raised at most once per date while the tray runs.
/// </summary>
internal sealed class TrayController : IDisposable
{
    /// <summary>How often the status is refreshed without the user doing anything.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    public const string NotFound = "Times (tm) not found. Install it with dotnet tool install -g Times.";
    public const string Locked = "Times is open in a terminal. Use it there or close it.";
    public const string NoAnswer = "Times did not answer.";

    private static readonly string[] StatusArguments = ["status", "--json"];

    private readonly IProcessRunner runner;
    private readonly Func<string?> locate;
    private readonly TimeProvider time;
    private readonly IIdleSource idle;
    private readonly object gate = new();
    private ITimer? timer;
    private Task current = Task.CompletedTask;
    private bool actionRunning;
    private bool started;
    private bool disposed;
    private DateOnly? remindedOn;
    private TrayMenuModel model = TrayMenuModel.Initial;

    /// <param name="locate">Finds <c>tm</c> before every run (<see cref="TmLocator.Find()"/>), <c>null</c> if it is missing.</param>
    /// <param name="idle">Tells how long the user has been away from keyboard and mouse.</param>
    public TrayController(IProcessRunner runner, Func<string?> locate, TimeProvider time, IIdleSource idle)
    {
        this.runner = runner;
        this.locate = locate;
        this.time = time;
        this.idle = idle;
    }

    /// <summary>Raised after <see cref="Model"/> changed, never after <see cref="Dispose"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised on a worker thread when the check-in reminder is due, at most once per date while the tray runs, after
    /// <see cref="Changed"/>; never after <see cref="Dispose"/>. The shell shows the notice.
    /// </summary>
    public event EventHandler<CheckInNotice>? ReminderDue;

    public TrayMenuModel Model
    {
        get
        {
            lock (gate)
            {
                return model;
            }
        }
    }

    /// <summary>The operation that runs or ran last (a refresh, or an action with its refresh).</summary>
    public Task RefreshCompleted
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>Refreshes once and starts the timer; a second call does nothing.</summary>
    public Task Start()
    {
        lock (gate)
        {
            if (started || disposed)
            {
                return current;
            }

            started = true;
            timer = time.CreateTimer(_ => Refresh(), null, RefreshInterval, RefreshInterval);
        }

        return Refresh();
    }

    /// <summary>Called when the menu opens: refreshes.</summary>
    public Task MenuOpened() => Refresh();

    /// <summary>Refreshes the status, or returns the operation already running. Nothing runs after <see cref="Dispose"/>.</summary>
    public Task Refresh()
    {
        lock (gate)
        {
            if (disposed || !current.IsCompleted)
            {
                return current;
            }

            current = Task.Run(RefreshCoreAsync);
            return current;
        }
    }

    /// <summary>
    /// Runs the action's <c>tm</c> command, then refreshes. Ignored while another action runs, after
    /// <see cref="Dispose"/>, or when the model has the action disabled. Shows <see cref="TrayMenuModel.LoadingRow"/>
    /// until it is done; a refresh that is running is waited for first.
    /// </summary>
    public Task Run(TrayAction action) => RunAction(action, Arguments(action), null);

    /// <summary>
    /// Books the arrival at the time <paramref name="notice"/> was shown (<c>tm arrive H:mm</c>), then refreshes, like
    /// <see cref="Run"/> with <see cref="TrayAction.Arrive"/>. Runs nothing when the clock's date is not the notice's,
    /// when Arrive is disabled (already arrived), while another action runs, or after <see cref="Dispose"/>.
    /// </summary>
    public Task ArriveFromReminder(CheckInNotice notice) =>
        RunAction(TrayAction.Arrive, ["arrive", CheckInReminder.Time(notice.Time)], notice.Date);

    private Task RunAction(TrayAction action, string[] arguments, DateOnly? onlyOn)
    {
        lock (gate)
        {
            if (disposed || actionRunning || !model.CanRun(action)
                || (onlyOn is { } date && DateOnly.FromDateTime(time.GetLocalNow().DateTime) != date))
            {
                return current;
            }

            actionRunning = true;
            Task previous = current;
            model = model.Loading();
            current = Task.Run(() => ActionCoreAsync(previous, arguments));
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return RefreshCompleted;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
        }
    }

    private async Task RefreshCoreAsync()
    {
        (TrayMenuModel next, TrayStatus? status) = await StatusAsync().ConfigureAwait(false);
        Publish(next, status, fromAction: false);
    }

    private async Task ActionCoreAsync(Task previous, string[] arguments)
    {
        try
        {
            await previous.ConfigureAwait(false);
            string? error = await RunTm(arguments).ConfigureAwait(false);
            (TrayMenuModel next, TrayStatus? status) = error is null
                ? await StatusAsync().ConfigureAwait(false)
                : (TrayMenuModel.FromError(error), null);
            Publish(next, status, fromAction: true);
        }
        finally
        {
            lock (gate)
            {
                actionRunning = false;
            }
        }
    }

    // Sets the model and raises Changed, unless disposed. A refresh never replaces an action's Loading row. With a
    // status that was published, then checks the reminder and raises ReminderDue once per date.
    private void Publish(TrayMenuModel next, TrayStatus? status, bool fromAction)
    {
        lock (gate)
        {
            if (disposed || (actionRunning && !fromAction))
            {
                return;
            }

            model = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        if (status is not null && DueNotice(status) is { } notice)
        {
            ReminderDue?.Invoke(this, notice);
        }
    }

    // The notice when the reminder is due now and was not raised for its date yet (then marks the date), or null.
    private CheckInNotice? DueNotice(TrayStatus status)
    {
        DateTime localNow = time.GetLocalNow().DateTime;
        if (!CheckInReminder.IsDue(status, localNow, idle.SinceLastInput()))
        {
            return null;
        }

        CheckInNotice notice = CheckInReminder.Notice(localNow);
        lock (gate)
        {
            if (disposed || remindedOn == notice.Date)
            {
                return null;
            }

            remindedOn = notice.Date;
        }

        return notice;
    }

    // The model for tm status --json, with the parsed status when tm answered with one.
    private async Task<(TrayMenuModel Model, TrayStatus? Status)> StatusAsync()
    {
        string? tm = locate();
        if (tm is null)
        {
            return (TrayMenuModel.FromError(NotFound), null);
        }

        ProcessResult result = await runner.RunAsync(tm, StatusArguments).ConfigureAwait(false);
        if (Failure(result) is { } error)
        {
            return (TrayMenuModel.FromError(error), null);
        }

        return TrayStatus.Parse(result.Output) is { } status
            ? (TrayMenuModel.FromStatus(status), status)
            : (TrayMenuModel.FromError(TrayStatus.NotUnderstood), null);
    }

    // Runs tm with the arguments; the error row, or null on exit 0.
    private async Task<string?> RunTm(string[] arguments)
    {
        string? tm = locate();
        if (tm is null)
        {
            return NotFound;
        }

        ProcessResult result = await runner.RunAsync(tm, arguments).ConfigureAwait(false);
        return Failure(result);
    }

    /// <summary>The error row for a run, or <c>null</c> when it exited 0.</summary>
    internal static string? Failure(ProcessResult result) => result.Outcome switch
    {
        ProcessOutcome.FailedToStart => NotFound,
        ProcessOutcome.TimedOut => NoAnswer,
        ProcessOutcome.TooMuchOutput => TrayStatus.NotUnderstood,
        _ => result.ExitCode switch
        {
            0 => null,
            3 => Locked,
            1 => FirstLine(result.Output) ?? DataFolderError(1),
            int code => DataFolderError(code),
        },
    };

    private static string DataFolderError(int code) => $"Times cannot read its data folder (exit {code}).";

    // The first line that is not empty once cleaned, cut to TrayText.MaxErrorLength, or null.
    private static string? FirstLine(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            string clean = TrayText.Clean(line, TrayText.MaxErrorLength);
            if (clean.Length > 0)
            {
                return clean;
            }
        }

        return null;
    }

    private static string[] Arguments(TrayAction action) => action switch
    {
        TrayAction.Arrive => ["arrive"],
        TrayAction.Break => ["break"],
        TrayAction.BreakEnd => ["breakend"],
        _ => ["leave"],
    };
}
