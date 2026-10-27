using Spectre.Console.Testing;

namespace Times.Tests;

public class AppTests
{
    [Fact]
    public void Run_PrintsLocalTimeAndZone()
    {
        var console = new TestConsole();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 10, 27, 18, 30, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

        var exitCode = new App(console, time).Run([]);

        Assert.Equal(0, exitCode);
        Assert.Contains("UTC", console.Output);
        Assert.Contains("2026-10-27 18:30:00", console.Output);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => zone;
    }
}
