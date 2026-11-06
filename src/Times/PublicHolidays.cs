namespace Times;

/// <summary>
/// Works out public holidays from fixed dates and the Easter date, with no network access and no data files.
/// Only Germany is known; its current rules apply to every year, except where a rule has a start year.
/// </summary>
internal static class PublicHolidays
{
    /// <summary>The country value for Germany (<see cref="SettingsRegistry.HolidayCountryName"/>).</summary>
    public const string Germany = "Germany";

    /// <summary>The value meaning no country, or (for the state) nationwide holidays only.</summary>
    public const string None = "none";

    /// <summary>The German states, by their two-letter code, in the order the settings panel lists them.</summary>
    public static readonly IReadOnlyList<string> GermanStates =
        ["BB", "BE", "BW", "BY", "HB", "HE", "HH", "MV", "NI", "NW", "RP", "SH", "SL", "SN", "ST", "TH"];

    /// <summary>Easter Sunday of <paramref name="year"/> in the Gregorian calendar (anonymous Gregorian computus).</summary>
    public static DateOnly Easter(int year)
    {
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = ((19 * a) + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        int m = (a + (11 * h) + (22 * l)) / 451;
        int month = (h + l - (7 * m) + 114) / 31;
        int day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>
    /// The English name of the public holiday on <paramref name="date"/> in <paramref name="country"/> and
    /// <paramref name="state"/>, or <c>null</c>. Both are compared ordinally; an unknown country (or <see cref="None"/>)
    /// has no holidays, and a state not in <see cref="GermanStates"/> gives the nationwide holidays only.
    /// </summary>
    public static string? NameOf(DateOnly date, string? country, string? state)
    {
        if (!string.Equals(country, Germany, StringComparison.Ordinal))
        {
            return null;
        }

        return Nationwide(date) ?? GermanState(date, state ?? None);
    }

    private static string? Nationwide(DateOnly date)
    {
        string? fixedDate = (date.Month, date.Day) switch
        {
            (1, 1) => "New Year's Day",
            (5, 1) => "Labour Day",
            (10, 3) => "German Unity Day",
            (12, 25) => "Christmas Day",
            (12, 26) => "Second Day of Christmas",
            _ => null,
        };
        if (fixedDate is not null)
        {
            return fixedDate;
        }

        return EasterOffset(date) switch
        {
            -2 => "Good Friday",
            1 => "Easter Monday",
            39 => "Ascension Day",
            50 => "Whit Monday",
            _ => null,
        };
    }

    private static string? GermanState(DateOnly date, string state)
    {
        if (!GermanStates.Contains(state, StringComparer.Ordinal))
        {
            return null;
        }

        int year = date.Year;
        switch (date.Month, date.Day)
        {
            case (1, 6) when In(state, "BW", "BY", "ST"):
                return "Epiphany";
            case (3, 8) when (state == "BE" && year >= 2019) || (state == "MV" && year >= 2023):
                return "International Women's Day";
            case (8, 15) when state == "SL":
                return "Assumption Day";
            case (9, 20) when state == "TH" && year >= 2019:
                return "World Children's Day";
            case (10, 31) when In(state, "BB", "MV", "SN", "ST", "TH") || (In(state, "HB", "HH", "NI", "SH") && year >= 2018):
                return "Reformation Day";
            case (11, 1) when In(state, "BW", "BY", "NW", "RP", "SL"):
                return "All Saints' Day";
            case (11, >= 16 and <= 22) when state == "SN" && date.DayOfWeek == DayOfWeek.Wednesday:
                return "Day of Repentance and Prayer";
            default:
                break;
        }

        return EasterOffset(date) switch
        {
            0 when state == "BB" => "Easter Sunday",
            49 when state == "BB" => "Whit Sunday",
            60 when In(state, "BW", "BY", "HE", "NW", "RP", "SL") => "Corpus Christi",
            _ => null,
        };
    }

    // Days from Easter Sunday of the date's year to the date.
    private static int EasterOffset(DateOnly date) => date.DayNumber - Easter(date.Year).DayNumber;

    private static bool In(string state, params string[] states) => states.Contains(state, StringComparer.Ordinal);
}
