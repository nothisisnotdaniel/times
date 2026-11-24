using System.Globalization;

namespace Times.LicenceIssuer;

internal sealed partial class MacKeychainSigningKeyStore
{
    // Unlike the useful time types, universal tag 14 retains the separators and
    // designators of X.680 Table 7. X.690 11.9 further constrains their spelling.
    private static void ValidateTime(ReadOnlySpan<byte> value)
    {
        bool recurring = value.StartsWith("R"u8);
        if (recurring)
        {
            int slash = value.IndexOf((byte)'/');
            Require(slash >= 1);
            ReadOnlySpan<byte> count = value[1..slash];
            Require(count.IsEmpty || Digits(count) && (count.Length == 1 || count[0] != '0'));
            value = value[(slash + 1)..];
        }

        int separator = value.IndexOf((byte)'/');
        ReadOnlySpan<byte> first = separator < 0 ? value : value[..separator];
        ReadOnlySpan<byte> last = separator < 0 ? default : value[(separator + 1)..];
        if (first.StartsWith("P"u8))
        {
            ValidateDuration(first[1..]);
            if (separator >= 0)
            {
                ValidateTimePoint(last, out _);
            }
            return;
        }

        (DateForm, int, int, int) start = ValidateTimePoint(first, out ReadOnlySpan<byte> startZone);
        if (separator < 0)
        {
            Require(!recurring);
            return;
        }
        if (last.StartsWith("P"u8))
        {
            ValidateDuration(last[1..]);
            return;
        }

        Require(ValidateTimePoint(last, out ReadOnlySpan<byte> endZone, startZone) == start);
        // Both endpoints have the same time properties. A repeated numeric
        // offset must be omitted at the end. UTC retains its Z designator.
        if (startZone.IsEmpty || startZone.SequenceEqual("Z"u8))
        {
            Require(endZone.SequenceEqual(startZone));
        }
        else
        {
            Require(endZone.IsEmpty || !endZone.SequenceEqual("Z"u8) && !endZone.SequenceEqual(startZone));
        }
    }

    private enum DateForm { None, Century, Year, Month, CalendarDay, OrdinalDay, Week, Weekday }

    private static (DateForm Date, int YearKind, int ClockLength, int FractionDigits) ValidateTimePoint(
        ReadOnlySpan<byte> value, out ReadOnlySpan<byte> zone, scoped ReadOnlySpan<byte> inheritedZone = default)
    {
        zone = default;
        int separator = value.IndexOf((byte)'T');
        bool clockOnly = value.Length >= 2 && Digits(value[..2])
            && (value.Length == 2 || value[2] is (byte)':' or (byte)'.' or (byte)'Z' or (byte)'+' or (byte)'-');
        (DateForm date, int yearKind) = (DateForm.None, 0);
        DateOnly? localDay = null;
        if (!clockOnly)
        {
            (date, yearKind) = ValidateTimeDate(separator < 0 ? value : value[..separator], out localDay);
            if (separator < 0)
            {
                return (date, yearKind, 0, 0);
            }
            Require(date is DateForm.CalendarDay or DateForm.OrdinalDay or DateForm.Weekday);
            value = value[(separator + 1)..];
        }

        int zoneStart = value.IndexOfAny((byte)'Z', (byte)'+', (byte)'-');
        if (zoneStart >= 0)
        {
            zone = value[zoneStart..];
            value = value[..zoneStart];
        }
        ReadOnlySpan<byte> effectiveZone = zone.IsEmpty ? inheritedZone : zone;
        int? offset = effectiveZone.IsEmpty ? null : UtcOffsetMinutes(effectiveZone);

        int point = value.IndexOf((byte)'.');
        ReadOnlySpan<byte> clock = point < 0 ? value : value[..point];
        ReadOnlySpan<byte> fraction = point < 0 ? default : value[(point + 1)..];
        Require(point < 0 || Digits(fraction));
        Require(clock.Length is 2 or 5 or 8);
        int hour = Number(clock[..2]);
        int minute = 0, second = 0;
        if (clock.Length >= 5)
        {
            Require(clock[2] == ':');
            minute = Number(clock.Slice(3, 2));
        }
        if (clock.Length == 8)
        {
            Require(clock[5] == ':');
            second = Number(clock[6..]);
        }
        Require(hour <= 24 && minute <= 59 && second <= 60);
        Require(hour < 24 || minute == 0 && second == 0 && !fraction.ContainsAnyExcept((byte)'0'));
        if (second == 60 && offset is int minutes)
        {
            int utcMinute = hour * 60 + minute - minutes;
            Require(utcMinute is -1 or 1439);
            if (localDay is DateOnly day)
            {
                // At UTC minute -1 the supplied local date is already the next day.
                DateOnly followingUtcDay = utcMinute == -1 ? day : day.AddDays(1);
                Require(followingUtcDay.Day == 1);
            }
        }
        return (date, yearKind, clock.Length, fraction.Length);
    }

    private static int UtcOffsetMinutes(ReadOnlySpan<byte> zone)
    {
        if (zone.SequenceEqual("Z"u8))
        {
            return 0;
        }
        Require(zone[0] is (byte)'+' or (byte)'-' && zone.Length is 3 or 6);
        int minutes = Number(zone.Slice(1, 2)) * 60;
        if (zone.Length == 6)
        {
            Require(zone[3] == ':');
            int remainder = Number(zone[4..]);
            Require(remainder is >= 1 and <= 59);
            minutes += remainder;
        }
        Require(zone[0] == '+' ? minutes <= 16 * 60 : minutes is > 0 and <= 15 * 60);
        return zone[0] == '+' ? minutes : -minutes;
    }

    private static (DateForm Form, int YearKind) ValidateTimeDate(ReadOnlySpan<byte> date, out DateOnly? localDay)
    {
        localDay = null;
        bool signed = date.StartsWith("+"u8) || date.StartsWith("-"u8);
        bool negative = date.StartsWith("-"u8);
        bool century = date.EndsWith("C"u8);
        ReadOnlySpan<byte> body = date[(signed ? 1 : 0)..(century ? ^1 : ^0)];
        int dash = body.IndexOf((byte)'-');
        ReadOnlySpan<byte> year = dash < 0 ? body : body[..dash];
        int width = century ? 2 : 4;
        Require(Digits(year) && (!century || dash < 0));
        bool expanded = year.Length > width;
        Require(expanded ? signed && year[0] != '0' : year.Length == width && (!signed || negative));
        Require(!negative || year.ContainsAnyExcept((byte)'0'));

        // Calendar and ISO week rules repeat every 400 years, even for years
        // outside the range of DateTime. Only the last four digits are needed.
        int tail = Number(year[^Math.Min(4, year.Length)..]);
        int yearKind = expanded ? year.Length : negative ? -1 : tail < (century ? 15 : 1582) ? -2 : 0;
        int cycleYear = 800 + (negative ? -tail : tail) % 400;
        if (dash < 0)
        {
            return (century ? DateForm.Century : DateForm.Year, yearKind);
        }
        ReadOnlySpan<byte> rest = body[(dash + 1)..];
        if (rest.StartsWith("W"u8))
        {
            Require(rest.Length is 3 or 5);
            int week = Number(rest.Slice(1, 2));
            Require(week >= 1 && week <= ISOWeek.GetWeeksInYear(cycleYear));
            if (rest.Length == 3)
            {
                return (DateForm.Week, yearKind);
            }
            Require(rest[3] == '-' && rest[4] is >= (byte)'1' and <= (byte)'7');
            localDay = DateOnly.FromDateTime(ISOWeek.ToDateTime(cycleYear, week, (DayOfWeek)((rest[4] - '0') % 7)));
            return (DateForm.Weekday, yearKind);
        }
        if (rest.Length == 3)
        {
            int day = Number(rest);
            Require(day >= 1 && day <= (DateTime.IsLeapYear(cycleYear) ? 366 : 365));
            localDay = new DateOnly(cycleYear, 1, 1).AddDays(day - 1);
            return (DateForm.OrdinalDay, yearKind);
        }
        Require(rest.Length is 2 or 5);
        int month = Number(rest[..2]);
        Require(month is >= 1 and <= 12);
        if (rest.Length == 2)
        {
            return (DateForm.Month, yearKind);
        }
        int dayOfMonth = Number(rest[3..]);
        Require(rest[2] == '-' && dayOfMonth >= 1 && dayOfMonth <= DateTime.DaysInMonth(cycleYear, month));
        localDay = new DateOnly(cycleYear, month, dayOfMonth);
        return (DateForm.CalendarDay, yearKind);
    }

    private static bool Digits(ReadOnlySpan<byte> text) =>
        !text.IsEmpty && !text.ContainsAnyExceptInRange((byte)'0', (byte)'9');

    private static int Number(ReadOnlySpan<byte> text)
    {
        Require(Digits(text));
        int number = 0;
        foreach (byte digit in text)
        {
            number = number * 10 + digit - '0';
        }
        return number;
    }
}
