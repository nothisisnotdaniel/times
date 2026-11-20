using Times.Tray;

namespace Times.Tests;

// Job 306, AC-5: the strict parse of tm status --json.
public sealed class TrayStatusTests
{
    internal const string Working =
        "{\"schemaVersion\":1,\"date\":\"2026-10-27\",\"state\":\"working\",\"since\":\"08:15\"," +
        "\"checkpoint\":{\"name\":\"Design\",\"workItem\":1234,\"since\":\"09:00\"}," +
        "\"workedMinutes\":288,\"targetMinutes\":480,\"leftMinutes\":192}";

    [Fact]
    public void ValidDocument_IsRead()
    {
        TrayStatus? status = TrayStatus.Parse(Working);

        Assert.NotNull(status);
        Assert.Equal(new DateOnly(2026, 10, 27), status.Date);
        Assert.Equal(TrayState.Working, status.State);
        Assert.Equal(new TimeOnly(8, 15), status.Since);
        Assert.NotNull(status.Checkpoint);
        Assert.Equal("Design", status.Checkpoint.Name);
        Assert.Equal(1234, status.Checkpoint.WorkItem);
        Assert.Equal(new TimeOnly(9, 0), status.Checkpoint.Since);
        Assert.Equal(288, status.WorkedMinutes);
        Assert.Equal(480, status.TargetMinutes);
        Assert.Equal(192, status.LeftMinutes);
    }

    [Theory]
    [InlineData("out", "Out")]
    [InlineData("break", "Break")]
    [InlineData("absent", "Absent")]
    public void OtherStates_NullSinceAndCheckpoint(string state, string expectedName)
    {
        TrayState expected = Enum.Parse<TrayState>(expectedName);
        string text = $"{{\"schemaVersion\":1,\"date\":\"2026-10-27\",\"state\":\"{state}\",\"since\":null," +
            "\"checkpoint\":null,\"workedMinutes\":0,\"targetMinutes\":480,\"leftMinutes\":-5}";

        TrayStatus? status = TrayStatus.Parse(text);

        Assert.NotNull(status);
        Assert.Equal(expected, status.State);
        Assert.Null(status.Since);
        Assert.Null(status.Checkpoint);
        Assert.Equal(-5, status.LeftMinutes);
    }

    [Fact]
    public void WhatTimesWrites_IsRead()
    {
        DayRecord day = new DayRecord([
            new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0)),
            new DayEntry(EntryKind.Checkpoint, new TimeOnly(9, 0), "Mail", null),
        ]);

        TrayStatus? status = TrayStatus.Parse(StatusJson.Build(new DateOnly(2026, 10, 27), day, TimeSpan.FromHours(8),
            new TimeOnly(10, 0)));

        Assert.NotNull(status);
        Assert.Equal(TrayState.Working, status.State);
        Assert.Null(status.Checkpoint?.WorkItem);
        Assert.Equal(360, status.LeftMinutes);
    }

    public static TheoryData<string> Bad() => new()
    {
        "",
        "not json",
        "[]",
        "null",
        Working[..^1],
        Working.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
        Working.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\""),
        Working.Replace("\"schemaVersion\":1,", ""),
        Working.Replace("\"date\":\"2026-10-27\"", "\"date\":\"27.10.2026\""),
        Working.Replace("\"state\":\"working\"", "\"state\":\"sleeping\""),
        Working.Replace("\"state\":\"working\"", "\"state\":\"Working\""),
        Working.Replace("\"state\":\"working\"", "\"state\":1"),
        Working.Replace("\"since\":\"08:15\"", "\"since\":\"8:15\""),
        Working.Replace("\"since\":\"08:15\",", ""),
        Working.Replace("\"name\":\"Design\"", "\"name\":7"),
        Working.Replace("\"name\":\"Design\"", "\"name\":\"\""),
        Working.Replace("\"workItem\":1234", "\"workItem\":\"1234\""),
        Working.Replace("\"workItem\":1234,", ""),
        Working.Replace("\"since\":\"09:00\"", "\"since\":null"),
        Working.Replace("\"checkpoint\":{\"name\":\"Design\",\"workItem\":1234,\"since\":\"09:00\"}", "\"checkpoint\":[]"),
        Working.Replace("\"state\":\"working\"", "\"state\":\"break\""),
        Working.Replace("\"workedMinutes\":288", "\"workedMinutes\":2.5"),
        Working.Replace("\"targetMinutes\":480", "\"targetMinutes\":null"),
        Working.Replace("\"leftMinutes\":192", "\"leftMinutes\":99999999999"),
        Working.Replace(",\"leftMinutes\":192", ""),
        Working.Replace("{\"schemaVersion\":1,", "{\"schemaVersion\":1,\"schemaVersion\":1,"),
    };

    [Theory]
    [MemberData(nameof(Bad))]
    public void AnythingElse_IsRefused(string text)
    {
        Assert.Null(TrayStatus.Parse(text));
    }

    // Job 307: the document with the three optional properties after leftMinutes.
    private static string WithTail(string arrival, string holiday, string reminder) =>
        Working[..^1] + $",\"arrival\":{arrival},\"holiday\":{holiday},\"reminder\":{reminder}}}";

    // Job 307, AC-3: an older tm leaves them out: no arrival, no holiday, reminder off.
    [Fact]
    public void WithoutTheReminderProperties_ReminderIsOff()
    {
        TrayStatus? status = TrayStatus.Parse(Working);

        Assert.NotNull(status);
        Assert.Null(status.Arrival);
        Assert.Null(status.Holiday);
        Assert.Null(status.Reminder);
    }

    // Job 307, AC-3: present values are read; null stays null.
    [Fact]
    public void ReminderProperties_AreRead()
    {
        TrayStatus? status = TrayStatus.Parse(WithTail("\"08:15\"", "\"Christmas Day\"", "\"09:30\""));
        TrayStatus? empty = TrayStatus.Parse(WithTail("null", "null", "null"));

        Assert.NotNull(status);
        Assert.Equal(new TimeOnly(8, 15), status.Arrival);
        Assert.Equal("Christmas Day", status.Holiday);
        Assert.Equal(new TimeOnly(9, 30), status.Reminder);
        Assert.NotNull(empty);
        Assert.Null(empty.Arrival);
        Assert.Null(empty.Holiday);
        Assert.Null(empty.Reminder);
    }

    // Job 307, AC-3: what Times writes now, with a reminder and an arrival, is read back.
    [Fact]
    public void WhatTimesWrites_WithReminder_IsRead()
    {
        DayRecord day = new DayRecord([new DayEntry(EntryKind.Arrival, new TimeOnly(8, 0))]);

        TrayStatus? status = TrayStatus.Parse(StatusJson.Build(new DateOnly(2026, 12, 25), day, TimeSpan.FromHours(8),
            new TimeOnly(10, 0), "Christmas Day", new TimeOnly(9, 30)));

        Assert.NotNull(status);
        Assert.Equal(new TimeOnly(8, 0), status.Arrival);
        Assert.Equal("Christmas Day", status.Holiday);
        Assert.Equal(new TimeOnly(9, 30), status.Reminder);
    }

    public static TheoryData<string> BadTail() => new()
    {
        WithTail("\"8:15\"", "null", "null"),
        WithTail("815", "null", "null"),
        WithTail("\"\"", "null", "null"),
        WithTail("{}", "null", "null"),
        WithTail("null", "1", "null"),
        WithTail("null", "true", "null"),
        WithTail("null", "[]", "null"),
        WithTail("null", "\"\\ud800\"", "null"),
        WithTail("null", "null", "\"9:30\""),
        WithTail("null", "null", "\"off\""),
        WithTail("null", "null", "930"),
        WithTail("null", "null", "false"),
        Working[..^1] + ",\"arrival\":null,\"holiday\":null,\"reminder\":\"09:30\",\"reminder\":null}",
    };

    // Job 307, AC-3: a present arrival or reminder that is not null or HH:mm, or a holiday that is not null or a string,
    // makes the document not understood; so does a repeated one.
    [Theory]
    [MemberData(nameof(BadTail))]
    public void MistypedReminderProperty_IsRefused(string text)
    {
        Assert.Null(TrayStatus.Parse(text));
    }

    [Fact]
    public void TheErrorText_IsFixed()
    {
        Assert.Equal("Times answered in a way the tray does not understand.", TrayStatus.NotUnderstood);
    }
}
