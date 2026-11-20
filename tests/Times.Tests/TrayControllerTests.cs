using Times.Tray;

namespace Times.Tests;

// Job 306, AC-7 to AC-9. The runner is scripted, the clock's timers fire by hand; each wait is on a task or a call
// count, bounded by WaitBound so a broken controller fails instead of hanging.
public sealed class TrayControllerTests : IDisposable
{
    private static readonly TimeSpan WaitBound = TimeSpan.FromSeconds(30);
    private const string Tm = "/opt/times/tm";

    private readonly FakeProcessRunner runner = new();
    private readonly SettableTimeProvider time = new(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
    private readonly TrayController controller;

    public TrayControllerTests()
    {
        controller = new TrayController(runner, () => Tm, time);
    }

    public void Dispose() => controller.Dispose();

    private static string Status(string state, string since = "\"08:15\"") =>
        $"{{\"schemaVersion\":1,\"date\":\"2026-10-27\",\"state\":\"{state}\",\"since\":{since}," +
        "\"checkpoint\":null,\"workedMinutes\":0,\"targetMinutes\":480,\"leftMinutes\":480}";

    private static async Task Done(Task task) => await task.WaitAsync(WaitBound);

    private async Task Started()
    {
        await Done(controller.Start());
        Assert.Equal("Working since 8:15", controller.Model.InfoRows[0]);
    }

    private static bool AnyAction(TrayMenuModel model) =>
        new[] { TrayAction.Arrive, TrayAction.Break, TrayAction.BreakEnd, TrayAction.Leave }.Any(model.CanRun);

    // AC-9
    [Fact]
    public async Task Start_RefreshesWithStatusJson()
    {
        Assert.Equal([TrayMenuModel.LoadingRow], controller.Model.InfoRows);

        await Started();

        (string fileName, string[] arguments) = Assert.Single(runner.Calls);
        Assert.Equal(Tm, fileName);
        Assert.Equal(["status", "--json"], arguments);
        Assert.Equal(TrayIconState.In, controller.Model.Icon);
    }

    // AC-9
    [Fact]
    public async Task Timer_RefreshesEvery60Seconds()
    {
        await Started();

        ManualTimer timer = Assert.Single(time.Timers);
        Assert.Equal([TimeSpan.FromSeconds(60)], timer.DueTimes);
        Assert.Equal(TimeSpan.FromSeconds(60), timer.Period);

        time.UtcNow += TimeSpan.FromSeconds(60);
        timer.Fire();
        await Done(controller.RefreshCompleted);
        time.UtcNow += TimeSpan.FromSeconds(60);
        timer.Fire();
        await Done(controller.RefreshCompleted);

        Assert.Equal(3, runner.Calls.Count);
    }

    // AC-9
    [Fact]
    public async Task MenuOpened_Refreshes()
    {
        await Started();

        await Done(controller.MenuOpened());

        Assert.Equal(["status --json", "status --json"], runner.CommandLines);
    }

    // AC-9
    [Fact]
    public async Task RunningRefresh_IsNotStartedAgain()
    {
        TaskCompletionSource<ProcessResult> hold = runner.Hold();
        Task first = controller.Start();
        await Done(runner.WhenCalls(1));

        Task second = controller.Refresh();
        Task third = controller.MenuOpened();
        time.Timers[0].Fire();

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Single(runner.Calls);
        hold.SetResult(ProcessResult.Exited(0, TrayStatusTests.Working));
        await Done(first);
        Assert.Single(runner.Calls);
        Assert.Equal("Working since 8:15", controller.Model.InfoRows[0]);
    }

    // AC-7
    [Theory]
    [InlineData("out", "Arrive", "arrive")]
    [InlineData("working", "Break", "break")]
    [InlineData("break", "BreakEnd", "breakend")]
    [InlineData("working", "Leave", "leave")]
    public async Task Action_RunsItsCommand_ThenRefreshes(string state, string actionName, string command)
    {
        TrayAction action = Enum.Parse<TrayAction>(actionName);
        runner.Answer = arguments => ProcessResult.Exited(0, arguments == "status --json" ? Status(state) : string.Empty);
        await Done(controller.Start());
        Assert.True(controller.Model.CanRun(action));

        await Done(controller.Run(action));

        Assert.Equal(["status --json", command, "status --json"], runner.CommandLines);
        Assert.All(runner.Calls, c => Assert.Equal(Tm, c.FileName));
    }

    // AC-7: a disabled action does nothing.
    [Fact]
    public async Task DisabledAction_RunsNothing()
    {
        await Started();

        await Done(controller.Run(TrayAction.Arrive));

        Assert.Equal(["status --json"], runner.CommandLines);
    }

    // AC-7
    [Fact]
    public async Task WhileAnActionRuns_Loading_NoActions_SecondClickIgnored()
    {
        await Started();
        TaskCompletionSource<ProcessResult> hold = runner.Hold();

        Task action = controller.Run(TrayAction.Break);
        await Done(runner.WhenCalls(2));

        Assert.Equal(TrayMenuModel.LoadingRow, controller.Model.InfoRows[0]);
        Assert.False(AnyAction(controller.Model));
        Task again = controller.Run(TrayAction.Break);
        Task other = controller.Run(TrayAction.Leave);
        Task refresh = controller.Refresh();
        Assert.Same(action, again);
        Assert.Same(action, other);
        Assert.Same(action, refresh);
        Assert.Equal(["status --json", "break"], runner.CommandLines);

        hold.SetResult(ProcessResult.Exited(0, string.Empty));
        await Done(action);

        Assert.Equal(["status --json", "break", "status --json"], runner.CommandLines);
        Assert.Equal("Working since 8:15", controller.Model.InfoRows[0]);
    }

    // AC-7: an action clicked during a refresh waits for it and keeps Loading until its own refresh.
    [Fact]
    public async Task ActionDuringARefresh_WaitsForIt()
    {
        await Started();
        TaskCompletionSource<ProcessResult> hold = runner.Hold();
        Task refresh = controller.MenuOpened();
        await Done(runner.WhenCalls(2));

        Task action = controller.Run(TrayAction.Leave);
        Assert.Equal(TrayMenuModel.LoadingRow, controller.Model.InfoRows[0]);
        hold.SetResult(ProcessResult.Exited(0, TrayStatusTests.Working));
        await Done(refresh);
        await Done(action);

        Assert.Equal(["status --json", "status --json", "leave", "status --json"], runner.CommandLines);
        Assert.NotEqual(TrayMenuModel.LoadingRow, controller.Model.InfoRows[0]);
    }

    private static readonly (ProcessResult Result, string Row)[] FailureCases =
    [
        (ProcessResult.Exited(3, "red line"), "Times is open in a terminal. Use it there or close it."),
        (ProcessResult.Failed(ProcessOutcome.TimedOut), "Times did not answer."),
        (ProcessResult.Failed(ProcessOutcome.FailedToStart), "Times (tm) not found. Install it with dotnet tool install -g Times."),
        (ProcessResult.Failed(ProcessOutcome.TooMuchOutput), "Times answered in a way the tray does not understand."),
        (ProcessResult.Exited(2, "x"), "Times cannot read its data folder (exit 2)."),
        (ProcessResult.Exited(4, "x"), "Times cannot read its data folder (exit 4)."),
        (ProcessResult.Exited(134, "x"), "Times cannot read its data folder (exit 134)."),
        (ProcessResult.Exited(0, "{\"secret\":\"raw text\"}"), "Times answered in a way the tray does not understand."),
    ];

    public static TheoryData<int> Failures() => new(Enumerable.Range(0, FailureCases.Length));

    // AC-8 (and AC-5: the raw text never shows).
    [Theory]
    [MemberData(nameof(Failures))]
    public async Task RefreshFailure_IsOneRow_NoActions(int index)
    {
        (ProcessResult result, string row) = FailureCases[index];
        runner.Answer = _ => result;

        await Done(controller.Start());

        Assert.Equal([row], controller.Model.InfoRows);
        Assert.False(AnyAction(controller.Model));
        Assert.Equal(TrayIconState.Out, controller.Model.Icon);
        Assert.DoesNotContain("raw text", controller.Model.Tooltip, StringComparison.Ordinal);
    }

    // AC-8
    [Fact]
    public async Task TmNotFound_SaysHowToInstall_AndRunsNothing()
    {
        using TrayController missing = new TrayController(runner, () => null, time);

        await Done(missing.Start());

        Assert.Equal(["Times (tm) not found. Install it with dotnet tool install -g Times."], missing.Model.InfoRows);
        Assert.Empty(runner.Calls);
    }

    // AC-8: exit 1 on an action shows its first non-empty line, cleaned and cut; no refresh hides it.
    [Fact]
    public async Task ActionExitOne_ShowsTheFirstLine_Cleaned()
    {
        string message = "\u001b[31mbreak: you are\u202E not working " + new string('x', 200) + "\u001b[0m";
        runner.Answer = arguments => arguments == "status --json"
            ? ProcessResult.Exited(0, TrayStatusTests.Working)
            : ProcessResult.Exited(1, "\n  \n" + message + "\nsecond line\n");
        await Started();

        await Done(controller.Run(TrayAction.Break));

        string row = Assert.Single(controller.Model.InfoRows);
        Assert.StartsWith("break: you are not working xxx", row, StringComparison.Ordinal);
        Assert.Equal(120, row.Length);
        Assert.EndsWith("…", row, StringComparison.Ordinal);
        Assert.False(AnyAction(controller.Model));
        Assert.Equal(["status --json", "break"], runner.CommandLines);
    }

    // AC-8: the next successful refresh clears the error.
    [Fact]
    public async Task NextSuccessfulRefresh_ClearsTheError()
    {
        runner.Answer = _ => ProcessResult.Exited(3, string.Empty);
        await Done(controller.Start());
        Assert.Equal([TrayController.Locked], controller.Model.InfoRows);

        runner.Answer = _ => ProcessResult.Exited(0, TrayStatusTests.Working);
        await Done(controller.MenuOpened());

        Assert.Equal("Working since 8:15", controller.Model.InfoRows[0]);
        Assert.True(controller.Model.CanRun(TrayAction.Break));
    }

    // AC-9
    [Fact]
    public async Task Dispose_StopsTheTimer_NothingRunsAfterIt()
    {
        await Started();
        int changes = 0;
        controller.Changed += (_, _) => Interlocked.Increment(ref changes);

        controller.Dispose();
        time.Timers[0].Fire();
        await Done(controller.Refresh());
        await Done(controller.MenuOpened());
        await Done(controller.Run(TrayAction.Break));

        Assert.True(time.Timers[0].Disposed);
        Assert.Single(runner.Calls);
        Assert.Equal(0, changes);
    }

    // AC-9: Start twice makes one timer.
    [Fact]
    public async Task Start_Twice_OneTimer()
    {
        await Started();

        await Done(controller.Start());

        Assert.Single(time.Timers);
        Assert.Single(runner.Calls);
    }

    // AC-9: Changed is raised after each new model.
    [Fact]
    public async Task Changed_IsRaised()
    {
        int changes = 0;
        controller.Changed += (_, _) => Interlocked.Increment(ref changes);

        await Started();
        await Done(controller.Run(TrayAction.Break));

        Assert.Equal(3, Volatile.Read(ref changes));
    }
}
