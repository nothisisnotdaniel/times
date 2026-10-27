namespace Times.Tests;

public class TimeInputTests
{
    // AC-5
    [Theory]
    [InlineData("8:15", 8, 15)]
    [InlineData("08:15", 8, 15)]
    [InlineData("0:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    public void TryParse_AcceptsHmm(string text, int hour, int minute)
    {
        Assert.True(TimeInput.TryParse(text, out TimeOnly time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    // AC-5
    [Theory]
    [InlineData("24:00")]
    [InlineData("8:60")]
    [InlineData("8:5")]
    [InlineData("8")]
    [InlineData("8.15")]
    [InlineData(":15")]
    [InlineData("-1:00")]
    [InlineData("8:15:00")]
    [InlineData("８:15")]
    [InlineData("abc")]
    [InlineData("8:15 x")]
    [InlineData("")]
    [InlineData(" 8:15")]
    [InlineData("008:15")]
    [InlineData("8:١٥")]
    public void TryParse_RejectsEverythingElse(string text)
    {
        Assert.False(TimeInput.TryParse(text, out TimeOnly time));
        Assert.Equal(default, time);
    }
}
