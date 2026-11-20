using Times.Tray;

namespace Times.Tests;

// Job 307, AC-4 and AC-7: when the check-in reminder is due, and what its notice says. Pure: fixed values only.
public sealed class CheckInReminderTests
{
    // Tuesday.
    private static readonly DateOnly Workday = new(2026, 10, 27);
    private static readonly DateTime Due = new(2026, 10, 27, 9, 32, 0);
    private static readonly TimeSpan Active = TimeSpan.FromMinutes(1);

    private static TrayStatus Status(DateOnly? date = null, TrayState state = TrayState.Out, TimeOnly? arrival = null,
        string? holiday = null, TimeOnly? reminder = null, bool reminderOff = false) =>
        new(date ?? Workday, state, null, null, 0, 480, 480, arrival, holiday,
            reminderOff ? null : reminder ?? new TimeOnly(9, 30));

    [Fact]
    public void AllConditionsHold_IsDue()
    {
        Assert.True(CheckInReminder.IsDue(Status(), Due, Active));
    }

    [Fact]
    public void ExactlyAtTheReminderTime_IsDue()
    {
        Assert.True(CheckInReminder.IsDue(Status(), new DateTime(2026, 10, 27, 9, 30, 0), Active));
    }

    [Fact]
    public void UnknownIdleTime_CountsAsActive()
    {
        Assert.True(CheckInReminder.IsDue(Status(), Due, null));
    }

    [Fact]
    public void JustUnderFiveMinutesIdle_IsDue()
    {
        Assert.True(CheckInReminder.IsDue(Status(), Due, TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(2026, 10, 30)] // Friday
    [InlineData(2026, 10, 26)] // Monday
    public void EveryWorkday_IsDue(int year, int month, int day)
    {
        DateOnly date = new DateOnly(year, month, day);

        Assert.True(CheckInReminder.IsDue(Status(date), date.ToDateTime(new TimeOnly(10, 0)), Active));
    }

    [Theory]
    [InlineData(2026, 10, 31)] // Saturday
    [InlineData(2026, 11, 1)] // Sunday
    public void Weekend_IsNotDue(int year, int month, int day)
    {
        DateOnly date = new DateOnly(year, month, day);

        Assert.False(CheckInReminder.IsDue(Status(date), date.ToDateTime(new TimeOnly(10, 0)), Active));
    }

    [Theory]
    [InlineData("Absent")]
    [InlineData("Working")]
    [InlineData("Break")]
    public void NotOut_IsNotDue(string state)
    {
        Assert.False(CheckInReminder.IsDue(Status(state: Enum.Parse<TrayState>(state)), Due, Active));
    }

    [Fact]
    public void Holiday_IsNotDue()
    {
        Assert.False(CheckInReminder.IsDue(Status(holiday: "Reformation Day"), Due, Active));
    }

    [Fact]
    public void AnArrival_IsNotDue()
    {
        Assert.False(CheckInReminder.IsDue(Status(arrival: new TimeOnly(8, 0)), Due, Active));
    }

    [Fact]
    public void ReminderOff_IsNotDue()
    {
        Assert.False(CheckInReminder.IsDue(Status(reminderOff: true), Due, Active));
    }

    [Fact]
    public void BeforeTheReminderTime_IsNotDue()
    {
        Assert.False(CheckInReminder.IsDue(Status(), new DateTime(2026, 10, 27, 9, 29, 59), Active));
        Assert.False(CheckInReminder.IsDue(Status(), new DateTime(2026, 10, 27, 9, 29, 0), Active));
    }

    [Fact]
    public void FiveMinutesIdle_IsNotDue()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), CheckInReminder.ActiveWithin);
        Assert.False(CheckInReminder.IsDue(Status(), Due, TimeSpan.FromMinutes(5)));
        Assert.False(CheckInReminder.IsDue(Status(), Due, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void StatusOfAnotherDate_IsNotDue()
    {
        Assert.False(CheckInReminder.IsDue(Status(new DateOnly(2026, 10, 26)), Due, Active));
    }

    [Fact]
    public void Notice_IsTheDateAndTheMinute()
    {
        CheckInNotice notice = CheckInReminder.Notice(new DateTime(2026, 10, 27, 9, 32, 45));

        Assert.Equal(Workday, notice.Date);
        Assert.Equal(new TimeOnly(9, 32), notice.Time);
    }

    // AC-7
    [Fact]
    public void Text_WhenTheShellCanBook_NamesTheTime()
    {
        CheckInNotice notice = new CheckInNotice(Workday, new TimeOnly(9, 32));

        Assert.Equal("No arrival booked yet today. Click to book arrival at 9:32.", CheckInReminder.Text(notice, canBook: true));
    }

    // AC-7
    [Fact]
    public void Text_WhenTheShellCannotBook_PointsToTheMenu()
    {
        CheckInNotice notice = new CheckInNotice(Workday, new TimeOnly(9, 32));

        Assert.Equal("No arrival booked yet today. Book it from the Times menu.", CheckInReminder.Text(notice, canBook: false));
    }

    // AC-7: the osascript arguments hold only the fixed macOS text and title.
    [Fact]
    public void MacScriptArguments_AreTheFixedScript()
    {
        Assert.Equal(
            ["-e", "display notification \"No arrival booked yet today. Book it from the Times menu.\" with title \"Times\""],
            CheckInReminder.MacScriptArguments());
    }
}
