namespace Times.Tests;

/// <summary>A clock frozen at one instant, for tests.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
{
    public static FixedTimeProvider At20261027() =>
        new(new DateTimeOffset(2026, 10, 27, 18, 30, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

    public override DateTimeOffset GetUtcNow() => utcNow;

    public override TimeZoneInfo LocalTimeZone => zone;
}
