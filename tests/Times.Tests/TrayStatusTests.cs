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

    [Fact]
    public void TheErrorText_IsFixed()
    {
        Assert.Equal("Times answered in a way the tray does not understand.", TrayStatus.NotUnderstood);
    }
}
