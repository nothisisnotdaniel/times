namespace Times.Tests;

/// <summary>A clock that stays put until a test moves it.</summary>
internal sealed class SettableTimeProvider : TimeProvider
{
    public SettableTimeProvider(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
