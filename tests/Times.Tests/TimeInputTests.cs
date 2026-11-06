namespace Times.Tests;

public class TimeInputTests
{
    public static TheoryData<string, int, int> AcceptedHmm => new()
    {
        { "8:15", 8, 15 },
        { "08:15", 8, 15 },
        { "0:00", 0, 0 },
        { "23:59", 23, 59 },
    };

    public static TheoryData<string> RefusedHmm => new()
    {
        "24:00", "8:60", "8:5", "8", "8.15", ":15", "-1:00", "8:15:00", "８:15", "abc", "8:15 x", "", " 8:15", "008:15", "8:١٥",
    };

    // AC-5
    [Theory]
    [MemberData(nameof(AcceptedHmm))]
    public void TryParse_AcceptsHmm(string text, int hour, int minute)
    {
        Assert.True(TimeInput.TryParse(text, out TimeOnly time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    // AC-5
    [Theory]
    [MemberData(nameof(RefusedHmm))]
    public void TryParse_RejectsEverythingElse(string text)
    {
        Assert.False(TimeInput.TryParse(text, out TimeOnly time));
        Assert.Equal(default, time);
    }

    // Relative times (job 103) --------------------------------------------------------------

    private static readonly TimeOnly At1030 = new(10, 30);

    // AC-1 (103)
    [Theory]
    [InlineData("now", 10, 30)]
    [InlineData("-10m", 10, 20)]
    [InlineData("+5m", 10, 35)]
    [InlineData("-0m", 10, 30)]
    [InlineData("+0m", 10, 30)]
    [InlineData("-0090m", 9, 0)]
    [InlineData("-630m", 0, 0)]
    [InlineData("+809m", 23, 59)]
    public void Parse_AcceptsNowAndRelativeMinutes(string text, int hour, int minute)
    {
        Assert.Equal(TimeInputResult.Valid, TimeInput.Parse(text, At1030, out TimeOnly time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    // AC-1 (103)
    [Fact]
    public void Parse_DropsSecondsOfNow()
    {
        Assert.Equal(TimeInputResult.Valid, TimeInput.Parse("now", new TimeOnly(10, 30, 59), out TimeOnly now));
        Assert.Equal(new TimeOnly(10, 30), now);
        Assert.Equal(TimeInputResult.Valid, TimeInput.Parse("-1m", new TimeOnly(10, 30, 59), out TimeOnly before));
        Assert.Equal(new TimeOnly(10, 29), before);
    }

    // AC-2 (103)
    [Theory]
    [InlineData("-m")]
    [InlineData("+m")]
    [InlineData("10m")]
    [InlineData("-10")]
    [InlineData("-10M")]
    [InlineData("- 10m")]
    [InlineData("-10m ")]
    [InlineData(" -10m")]
    [InlineData("--10m")]
    [InlineData("+-5m")]
    [InlineData("-10h")]
    [InlineData("-12345m")]
    [InlineData("-１0m")]
    [InlineData("-1٠m")]
    [InlineData("NOW")]
    [InlineData("Now")]
    [InlineData("nowx")]
    [InlineData(" now")]
    [InlineData("")]
    public void Parse_RefusesMalformedRelativeTimes(string text)
    {
        Assert.Equal(TimeInputResult.Malformed, TimeInput.Parse(text, At1030, out TimeOnly time));
        Assert.Equal(default, time);
    }

    // AC-3 (103)
    [Theory]
    [InlineData("-6m", 0, 5)]
    [InlineData("+2m", 23, 58)]
    [InlineData("-9999m", 10, 30)]
    [InlineData("+9999m", 10, 30)]
    public void Parse_RefusesCrossingMidnight_AsItsOwnResult(string text, int hour, int minute)
    {
        Assert.Equal(TimeInputResult.CrossesMidnight, TimeInput.Parse(text, new TimeOnly(hour, minute), out TimeOnly time));
        Assert.Equal(default, time);
    }

    // AC-3 (103)
    [Fact]
    public void Parse_AcceptsTheEdgesOfTheDay()
    {
        Assert.Equal(TimeInputResult.Valid, TimeInput.Parse("-5m", new TimeOnly(0, 5), out TimeOnly start));
        Assert.Equal(new TimeOnly(0, 0), start);
        Assert.Equal(TimeInputResult.Valid, TimeInput.Parse("+1m", new TimeOnly(23, 58), out TimeOnly end));
        Assert.Equal(new TimeOnly(23, 59), end);
    }

    // AC-4 (103)
    [Theory]
    [MemberData(nameof(AcceptedHmm))]
    public void Parse_AcceptsHmm_AsTryParseDoes(string text, int hour, int minute)
    {
        Assert.Equal(TimeInputResult.Valid, TimeInput.Parse(text, At1030, out TimeOnly time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    // AC-4 (103)
    [Theory]
    [MemberData(nameof(RefusedHmm))]
    public void Parse_RefusesWhatTryParseRefuses(string text)
    {
        Assert.Equal(TimeInputResult.Malformed, TimeInput.Parse(text, At1030, out TimeOnly time));
        Assert.Equal(default, time);
    }

    // AC-7 (103)
    [Theory]
    [InlineData("-99999m", true)]
    [InlineData("+1m", true)]
    [InlineData("-m", false)]
    [InlineData("-1h", false)]
    [InlineData("now", false)]
    [InlineData("-1x m", false)]
    public void LooksRelative_MatchesSignDigitsM(string text, bool expected)
    {
        Assert.Equal(expected, TimeInput.LooksRelative(text));
    }
}
