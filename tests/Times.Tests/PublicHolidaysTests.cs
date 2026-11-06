namespace Times.Tests;

public sealed class PublicHolidaysTests
{
    // AC-1
    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2038, 4, 25)]
    public void Easter_ReturnsTheGregorianDate(int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), PublicHolidays.Easter(year));
    }

    // AC-2: every 2026 date, day by day.
    [Fact]
    public void NameOf_GermanyNationwide_2026_ExactlyTheNineHolidays()
    {
        Dictionary<DateOnly, string> expected = new()
        {
            [new DateOnly(2026, 1, 1)] = "New Year's Day",
            [new DateOnly(2026, 4, 3)] = "Good Friday",
            [new DateOnly(2026, 4, 6)] = "Easter Monday",
            [new DateOnly(2026, 5, 1)] = "Labour Day",
            [new DateOnly(2026, 5, 14)] = "Ascension Day",
            [new DateOnly(2026, 5, 25)] = "Whit Monday",
            [new DateOnly(2026, 10, 3)] = "German Unity Day",
            [new DateOnly(2026, 12, 25)] = "Christmas Day",
            [new DateOnly(2026, 12, 26)] = "Second Day of Christmas",
        };

        for (DateOnly date = new DateOnly(2026, 1, 1); date.Year == 2026; date = date.AddDays(1))
        {
            Assert.Equal(expected.GetValueOrDefault(date), PublicHolidays.NameOf(date, "Germany", "none"));
        }

        Assert.Null(PublicHolidays.NameOf(new DateOnly(2026, 1, 6), "Germany", "none"));
        Assert.Null(PublicHolidays.NameOf(new DateOnly(2026, 11, 1), "Germany", "none"));
    }

    // AC-2: the nationwide holidays hold in every state too.
    [Fact]
    public void NameOf_NationwideHolidays_HoldInEveryState()
    {
        foreach (string state in PublicHolidays.GermanStates)
        {
            Assert.Equal("Good Friday", PublicHolidays.NameOf(new DateOnly(2026, 4, 3), "Germany", state));
            Assert.Equal("Christmas Day", PublicHolidays.NameOf(new DateOnly(2026, 12, 25), "Germany", state));
        }
    }

    // AC-3
    [Theory]
    [InlineData("2026-06-04", "BY", "Corpus Christi")]
    [InlineData("2026-06-04", "BW", "Corpus Christi")]
    [InlineData("2026-06-04", "HE", "Corpus Christi")]
    [InlineData("2026-06-04", "NW", "Corpus Christi")]
    [InlineData("2026-06-04", "RP", "Corpus Christi")]
    [InlineData("2026-06-04", "SL", "Corpus Christi")]
    [InlineData("2026-06-04", "BE", null)]
    [InlineData("2026-06-04", "SN", null)]
    [InlineData("2026-11-18", "SN", "Day of Repentance and Prayer")]
    [InlineData("2026-11-18", "BY", null)]
    [InlineData("2025-11-19", "SN", "Day of Repentance and Prayer")]
    [InlineData("2026-11-17", "SN", null)]
    [InlineData("2026-04-05", "BB", "Easter Sunday")]
    [InlineData("2026-04-05", "BE", null)]
    [InlineData("2026-04-05", "BY", null)]
    [InlineData("2026-05-24", "BB", "Whit Sunday")]
    [InlineData("2026-05-24", "BY", null)]
    [InlineData("2026-03-08", "BE", "International Women's Day")]
    [InlineData("2026-03-08", "MV", "International Women's Day")]
    [InlineData("2026-03-08", "BY", null)]
    [InlineData("2019-03-08", "BE", "International Women's Day")]
    [InlineData("2018-03-08", "BE", null)]
    [InlineData("2023-03-08", "MV", "International Women's Day")]
    [InlineData("2022-03-08", "MV", null)]
    [InlineData("2026-01-06", "BW", "Epiphany")]
    [InlineData("2026-01-06", "BY", "Epiphany")]
    [InlineData("2026-01-06", "ST", "Epiphany")]
    [InlineData("2026-01-06", "NW", null)]
    [InlineData("2026-08-15", "SL", "Assumption Day")]
    [InlineData("2026-08-15", "BY", null)]
    [InlineData("2026-09-20", "TH", "World Children's Day")]
    [InlineData("2018-09-20", "TH", null)]
    [InlineData("2026-09-20", "SN", null)]
    [InlineData("2026-10-31", "HH", "Reformation Day")]
    [InlineData("2026-10-31", "SN", "Reformation Day")]
    [InlineData("2026-10-31", "BB", "Reformation Day")]
    [InlineData("2026-10-31", "HB", "Reformation Day")]
    [InlineData("2018-10-31", "NI", "Reformation Day")]
    [InlineData("2017-10-31", "HH", null)]
    [InlineData("2017-10-31", "SH", null)]
    [InlineData("2017-10-31", "TH", "Reformation Day")]
    [InlineData("2026-10-31", "BY", null)]
    [InlineData("2026-11-01", "BY", "All Saints' Day")]
    [InlineData("2026-11-01", "NW", "All Saints' Day")]
    [InlineData("2026-11-01", "BE", null)]
    public void NameOf_StateRules(string date, string state, string? expected)
    {
        Assert.Equal(expected, PublicHolidays.NameOf(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), "Germany", state));
    }

    // AC-4
    [Theory]
    [InlineData("none", "BY")]
    [InlineData("France", "none")]
    [InlineData("germany", "none")]
    [InlineData("", "none")]
    [InlineData(null, "BY")]
    public void NameOf_NoOrUnknownCountry_IsNull(string? country, string state)
    {
        Assert.Null(PublicHolidays.NameOf(new DateOnly(2026, 12, 25), country, state));
        Assert.Null(PublicHolidays.NameOf(new DateOnly(2026, 6, 4), country, state));
    }

    // AC-4: an unknown state gives the nationwide holidays only.
    [Theory]
    [InlineData("XX")]
    [InlineData("by")]
    [InlineData("")]
    [InlineData(null)]
    public void NameOf_UnknownState_NationwideOnly(string? state)
    {
        Assert.Null(PublicHolidays.NameOf(new DateOnly(2026, 6, 4), "Germany", state));
        Assert.Equal("Christmas Day", PublicHolidays.NameOf(new DateOnly(2026, 12, 25), "Germany", state));
    }
}
