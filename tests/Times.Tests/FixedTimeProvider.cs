namespace Times.Tests;

/// <summary>A clock frozen at one instant, for tests.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset utcNow;
    private readonly TimeZoneInfo zone;

    public FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo zone)
    {
        this.utcNow = utcNow;
        this.zone = zone;
    }

    public static FixedTimeProvider At20261027() =>
        new(new DateTimeOffset(2026, 10, 27, 18, 30, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

    public override DateTimeOffset GetUtcNow() => utcNow;

    public override TimeZoneInfo LocalTimeZone => zone;
}
