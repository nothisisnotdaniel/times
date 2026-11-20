namespace Times.Tests;

public sealed class CalendarMeetingsTests
{
    private static readonly DateOnly Today = new DateOnly(2026, 11, 20);

    private static readonly ProjectList Projects = new ProjectList([new Project(1234, "Design"), new Project(77, "Support")]);

    private static CalendarAppointment At(string start, string? title, bool allDay = false) =>
        new CalendarAppointment(DateTimeOffset.ParseExact(start, CalendarMeetings.StartFormat, System.Globalization.CultureInfo.InvariantCulture),
            allDay, title);

    private static MeetingRow Single(string? title, ProjectList? projects = null) =>
        Assert.Single(CalendarMeetings.Offer([At("2026-11-20T10:00:00+01:00", title)], Today, projects ?? Projects, DayRecord.Empty));

    // ---- Parse ----

    // AC-2, AC-3: the contract, with other properties ignored and a missing or null title as null.
    [Fact]
    public void Parse_ValidInput_KeepsContextsOrder()
    {
        IReadOnlyList<CalendarAppointment>? list = CalendarMeetings.Parse(
            "{\"calendar\":\"ok\",\"appointments\":[" +
            "{\"start\":\"2026-11-20T14:00:00+01:00\",\"allDay\":false,\"title\":\"Review\",\"end\":\"x\"}," +
            "{\"start\":\"2026-11-20T09:30:00+01:00\",\"allDay\":true,\"title\":null}," +
            "{\"start\":\"2026-11-20T08:00:00-05:00\",\"allDay\":false}]}");

        Assert.NotNull(list);
        Assert.Equal(3, list.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.FromHours(1)), list[0].Start);
        Assert.Equal(TimeSpan.FromHours(1), list[0].Start.Offset);
        Assert.Equal("Review", list[0].Title);
        Assert.False(list[0].AllDay);
        Assert.True(list[1].AllDay);
        Assert.Null(list[1].Title);
        Assert.Null(list[2].Title);
        Assert.Equal(TimeSpan.FromHours(-5), list[2].Start.Offset);
    }

    // AC-2: anything that is not the contract is null.
    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"appointments\":{}}")]
    [InlineData("{\"appointments\":[1]}")]
    [InlineData("{\"appointments\":[{\"allDay\":false,\"title\":\"x\"}]}")]
    [InlineData("{\"appointments\":[{\"start\":\"2026-11-20 10:00\",\"allDay\":false,\"title\":\"x\"}]}")]
    [InlineData("{\"appointments\":[{\"start\":\"2026-11-20T10:00:00Z\",\"allDay\":false,\"title\":\"x\"}]}")]
    [InlineData("{\"appointments\":[{\"start\":20261120,\"allDay\":false,\"title\":\"x\"}]}")]
    [InlineData("{\"appointments\":[{\"start\":\"2026-11-20T10:00:00+01:00\",\"allDay\":\"no\",\"title\":\"x\"}]}")]
    [InlineData("{\"appointments\":[{\"start\":\"2026-11-20T10:00:00+01:00\",\"title\":\"x\"}]}")]
    [InlineData("{\"appointments\":[{\"start\":\"2026-11-20T10:00:00+01:00\",\"allDay\":false,\"title\":5}]}")]
    public void Parse_NotTheContract_IsNull(string json)
    {
        Assert.Null(CalendarMeetings.Parse(json));
    }

    // AC-2: an empty list is valid.
    [Fact]
    public void Parse_NoAppointments_IsEmpty()
    {
        IReadOnlyList<CalendarAppointment>? list = CalendarMeetings.Parse("{\"appointments\":[]}");

        Assert.NotNull(list);
        Assert.Empty(list);
    }

    // ---- Offer ----

    // AC-3: all-day, yesterday's and tomorrow's starts are left out; the date is the wall clock in its own offset.
    [Fact]
    public void Offer_OnlyTodaysTimedMeetings()
    {
        IReadOnlyList<MeetingRow> rows = CalendarMeetings.Offer(
        [
            At("2026-11-20T09:00:00+01:00", "All day", allDay: true),
            At("2026-11-19T23:30:00+01:00", "Yesterday"),
            At("2026-11-21T00:30:00+01:00", "Tomorrow"),
            At("2026-11-20T23:30:00-08:00", "Late in its own zone"),
            At("2026-11-20T00:15:00+09:00", "Early in its own zone"),
        ], Today, Projects, DayRecord.Empty);

        Assert.Equal(["Early in its own zone", "Late in its own zone"], rows.Select(r => r.Name));
        Assert.Equal([new TimeOnly(0, 15), new TimeOnly(23, 30)], rows.Select(r => r.Time));
    }

    // AC-3: sorted by start, ties keep Context's order, numbered 1..n; seconds are dropped.
    [Fact]
    public void Offer_SortsByStart_TiesKeepOrder()
    {
        IReadOnlyList<MeetingRow> rows = CalendarMeetings.Offer(
        [
            At("2026-11-20T14:00:00+01:00", "C"),
            At("2026-11-20T09:00:45+01:00", "A"),
            At("2026-11-20T09:00:45+01:00", "B"),
        ], Today, Projects, DayRecord.Empty);

        Assert.Equal(["A", "B", "C"], rows.Select(r => r.Name));
        Assert.Equal([1, 2, 3], rows.Select(r => r.Number!.Value));
        Assert.All(rows, r => Assert.Equal(MeetingStatus.Offered, r.Status));
        Assert.Equal(new TimeOnly(9, 0), rows[0].Time);
    }

    // AC-4: a matching #<work item> token picks the project and is removed, at the start, in the middle or at the end.
    [Theory]
    [InlineData("Sprint review #1234", "Sprint review")]
    [InlineData("#1234 Sprint review", "Sprint review")]
    [InlineData("Sprint #1234 review", "Sprint review")]
    [InlineData("Sprint review #9 #1234", "Sprint review #9")]
    public void Offer_MatchingToken_SetsTheWorkItem(string title, string name)
    {
        MeetingRow row = Single(title);

        Assert.Equal(name, row.Name);
        Assert.Equal(1234, row.Project?.WorkItem);
        Assert.Equal(1234, row.ToEntry().WorkItem);
    }

    // AC-4: the first matching token wins; a later one stays in the name.
    [Fact]
    public void Offer_FirstMatchingToken_Wins()
    {
        MeetingRow row = Single("Sync #77 #1234");

        Assert.Equal("Sync #1234", row.Name);
        Assert.Equal(77, row.Project?.WorkItem);
    }

    // AC-4: a title that is only the token takes the project's title.
    [Fact]
    public void Offer_OnlyTheToken_UsesTheProjectTitle()
    {
        MeetingRow row = Single("#1234");

        Assert.Equal("Design", row.Name);
        Assert.Equal(1234, row.Project?.WorkItem);
    }

    // AC-4: no matching project, not a whole token, too many digits, or no quicklist: title unchanged, no work item.
    [Theory]
    [InlineData("Sprint review #4321")]
    [InlineData("Sprint review #1234,")]
    [InlineData("Sprint review (#1234)")]
    [InlineData("Sprint review #0000001234x")]
    [InlineData("Sprint review #00000001234")]
    [InlineData("Sprint review #")]
    public void Offer_NoMatchingToken_KeepsTheTitle(string title)
    {
        MeetingRow row = Single(title);

        Assert.Equal(title, row.Name);
        Assert.Null(row.Project);
    }

    // AC-4: leading zeros still name the work item (1-9 digits).
    [Fact]
    public void Offer_LeadingZeros_StillMatch()
    {
        Assert.Equal(1234, Single("Review #001234").Project?.WorkItem);
    }

    // AC-4: without a readable quicklist (null) no project is picked.
    [Fact]
    public void Offer_NoProjects_PicksNone()
    {
        MeetingRow row = Assert.Single(CalendarMeetings.Offer([At("2026-11-20T10:00:00+01:00", "Review #1234")], Today, null, DayRecord.Empty));

        Assert.Equal("Review #1234", row.Name);
        Assert.Null(row.Project);
    }

    // AC-5: titles that fail the typed-name checks are not offered, with a fixed reason and no name.
    [Theory]
    [InlineData(null, CalendarMeetings.NoTitle)]
    [InlineData("   ", CalendarMeetings.NoTitle)]
    [InlineData("Evil ‮gnissap", CalendarMeetings.InvisibleCharacters)]
    [InlineData("Zero​width #1234", CalendarMeetings.InvisibleCharacters)]
    [InlineData("Line\nbreak", CalendarMeetings.ControlCharacters)]
    public void Offer_InvalidTitle_IsNotOffered(string? title, string reason)
    {
        MeetingRow row = Single(title);

        Assert.Equal(MeetingStatus.NotOffered, row.Status);
        Assert.Equal(reason, row.Reason);
        Assert.Null(row.Name);
        Assert.Null(row.Number);
    }

    // AC-5: a lone surrogate is a broken character (kept out of InlineData, which cannot carry it).
    [Fact]
    public void Offer_LoneSurrogate_IsNotOffered()
    {
        MeetingRow row = Single("Broken " + '\uD800');

        Assert.Equal(MeetingStatus.NotOffered, row.Status);
        Assert.Equal(CalendarMeetings.InvisibleCharacters, row.Reason);
    }

    // AC-5: the length check runs on the raw title, before the token is removed.
    [Fact]
    public void Offer_LongTitle_IsNotOffered_EvenIfTheTokenWouldShortenIt()
    {
        MeetingRow tooLong = Single(new string('x', 201));
        MeetingRow withToken = Single(new string('x', 195) + " #1234");
        MeetingRow exactly = Single(new string('x', 200));

        Assert.Equal(MeetingStatus.NotOffered, tooLong.Status);
        Assert.Equal(CalendarMeetings.TooLong, tooLong.Reason);
        Assert.Equal(MeetingStatus.NotOffered, withToken.Status);
        Assert.Equal(MeetingStatus.Offered, exactly.Status);
    }

    // AC-5, AC-3: not-offered rows take no number; the others are still numbered 1..n.
    [Fact]
    public void Offer_NotOffered_TakesNoNumber()
    {
        IReadOnlyList<MeetingRow> rows = CalendarMeetings.Offer(
        [
            At("2026-11-20T09:00:00+01:00", null),
            At("2026-11-20T10:00:00+01:00", "Standup"),
        ], Today, Projects, DayRecord.Empty);

        Assert.Equal([null, 1], rows.Select(r => r.Number));
    }

    // AC-6: an offer equal to a checkpoint already on the day (time, name, work item) is already booked, with no number.
    [Fact]
    public void Offer_Duplicate_IsAlreadyBooked()
    {
        DayRecord day = new DayRecord(
        [
            new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
            new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 0), "Sprint review", 1234),
            new DayEntry(EntryKind.Checkpoint, new TimeOnly(11, 0), "Standup", null),
        ]);

        IReadOnlyList<MeetingRow> rows = CalendarMeetings.Offer(
        [
            At("2026-11-20T09:00:00+01:00", "Sprint review #1234"),
            At("2026-11-20T10:00:00+01:00", "Sprint review #1234"),
            At("2026-11-20T11:00:00+01:00", "Standup #77"),
            At("2026-11-20T11:00:00+01:00", "Standup"),
        ], Today, Projects, day);

        Assert.Equal([MeetingStatus.AlreadyBooked, MeetingStatus.Offered, MeetingStatus.Offered, MeetingStatus.AlreadyBooked],
            rows.Select(r => r.Status));
        Assert.Equal([null, 1, 2, null], rows.Select(r => r.Number));
    }
}
