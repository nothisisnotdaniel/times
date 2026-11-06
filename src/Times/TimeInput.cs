namespace Times;

/// <summary>The outcome of <see cref="TimeInput.Parse"/>.</summary>
internal enum TimeInputResult
{
    /// <summary>The text is a time on the same day.</summary>
    Valid,

    /// <summary>The text is not a time in any accepted form.</summary>
    Malformed,

    /// <summary>The text is a well-formed relative time whose result falls before 0:00 or after 23:59.</summary>
    CrossesMidnight,
}

/// <summary>Parses the time a user types after a booking command such as <c>arrive</c> or <c>leave</c>.</summary>
internal static class TimeInput
{
    /// <summary>The format shown in hints.</summary>
    public const string Format = "H:mm";

    /// <summary>Every accepted form, as hints list them.</summary>
    public const string Forms = "H:mm, now, -<n>m or +<n>m";

    /// <summary>The accepted forms in short, with examples, as descriptions show them.</summary>
    public const string ShortForms = "H:mm|now|-10m|+5m";

    /// <summary>The most digits a relative offset may have.</summary>
    public const int MaxOffsetDigits = 4;

    /// <summary>
    /// Parses <c>now</c>, <c>-&lt;n&gt;m</c>, <c>+&lt;n&gt;m</c> (1 to 4 ASCII digits, worked out from
    /// <paramref name="now"/> with its seconds dropped) or, failing those, <c>H:mm</c> as
    /// <see cref="TryParse(string, out TimeOnly)"/> does. Exact and case-sensitive; a relative result
    /// outside the same day is <see cref="TimeInputResult.CrossesMidnight"/>, never wrapped.
    /// </summary>
    public static TimeInputResult Parse(string text, TimeOnly now, out TimeOnly time)
    {
        time = default;
        int current = (now.Hour * 60) + now.Minute;
        if (text == "now")
        {
            time = new TimeOnly(now.Hour, now.Minute);
            return TimeInputResult.Valid;
        }

        if (IsRelative(text))
        {
            int offset = Digits(text, 1, text.Length - 2);
            int minutes = text[0] == '-' ? current - offset : current + offset;
            if (minutes < 0 || minutes > (24 * 60) - 1)
            {
                return TimeInputResult.CrossesMidnight;
            }

            time = new TimeOnly(minutes / 60, minutes % 60);
            return TimeInputResult.Valid;
        }

        return TryParse(text, out time) ? TimeInputResult.Valid : TimeInputResult.Malformed;
    }

    /// <summary>
    /// Whether <paramref name="text"/> looks like a relative time of any length: a sign, one or more
    /// ASCII digits, then <c>m</c>. Used to tell a mistyped time from a checkpoint name.
    /// </summary>
    public static bool LooksRelative(string text)
    {
        if (text.Length < 3 || (text[0] != '-' && text[0] != '+') || text[^1] != 'm')
        {
            return false;
        }

        for (int i = 1; i < text.Length - 1; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRelative(string text) =>
        LooksRelative(text) && text.Length - 2 <= MaxOffsetDigits;

    /// <summary>
    /// Parses <c>H:mm</c> or <c>HH:mm</c>: an hour from 0 to 23 with one or two ASCII digits, a
    /// colon, and a minute from 00 to 59 with exactly two ASCII digits. Nothing else is allowed:
    /// no spaces, seconds, signs or non-ASCII digits.
    /// </summary>
    public static bool TryParse(string text, out TimeOnly time)
    {
        time = default;
        int colon = text.IndexOf(':');
        if (colon < 1 || colon > 2 || text.Length != colon + 3)
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (i != colon && !char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        int hour = Digits(text, 0, colon);
        int minute = Digits(text, colon + 1, 2);
        if (hour > 23 || minute > 59)
        {
            return false;
        }

        time = new TimeOnly(hour, minute);
        return true;
    }

    private static int Digits(string text, int start, int count)
    {
        int value = 0;
        for (int i = start; i < start + count; i++)
        {
            value = (value * 10) + (text[i] - '0');
        }

        return value;
    }
}
