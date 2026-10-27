namespace Times;

/// <summary>Parses the time a user types after <c>/arrive</c> or <c>/leave</c>.</summary>
internal static class TimeInput
{
    /// <summary>The format shown in hints.</summary>
    public const string Format = "H:mm";

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
