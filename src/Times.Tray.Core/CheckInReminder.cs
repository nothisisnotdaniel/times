using System.Globalization;

namespace Times.Tray;

/// <summary>A check-in reminder that was shown: the date it is for and the minute it was shown, the arrival it books.</summary>
internal sealed class CheckInNotice
{
    public CheckInNotice(DateOnly date, TimeOnly time)
    {
        Date = date;
        Time = time;
    }

    public DateOnly Date { get; }

    /// <summary>The minute the notice was shown, to the minute (no seconds).</summary>
    public TimeOnly Time { get; }
}

/// <summary>
/// When the tray reminds the user to book the arrival, and what the notice says (pure). The reminder is due on a
/// Monday to Friday that is today, still <see cref="TrayState.Out"/> with no arrival and no public holiday, from the
/// <c>checkin-reminder</c> time on, while the user is active (input within <see cref="ActiveWithin"/>, or unknown).
/// </summary>
internal static class CheckInReminder
{
    /// <summary>The user counts as active when the last input is less than this long ago.</summary>
    public static readonly TimeSpan ActiveWithin = TimeSpan.FromMinutes(5);

    /// <summary>The first sentence of every notice.</summary>
    public const string Lead = "No arrival booked yet today.";

    /// <summary>The second sentence where the notice cannot book (macOS).</summary>
    public const string FromMenu = " Book it from the Times menu.";

    /// <summary>The title the notice shows.</summary>
    public const string Title = "Times";

    /// <summary>True when the reminder is due for <paramref name="status"/> at <paramref name="localNow"/>.</summary>
    public static bool IsDue(TrayStatus status, DateTime localNow, TimeSpan? sinceLastInput)
    {
        DateOnly today = DateOnly.FromDateTime(localNow);
        return status.Date == today
            && today.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && status.State == TrayState.Out
            && status.Arrival is null
            && status.Holiday is null
            && status.Reminder is { } from
            && TimeOnly.FromDateTime(localNow) >= from
            && (sinceLastInput is null || sinceLastInput.Value < ActiveWithin);
    }

    /// <summary>The notice for <paramref name="localNow"/>: its date and minute.</summary>
    public static CheckInNotice Notice(DateTime localNow) =>
        new(DateOnly.FromDateTime(localNow), new TimeOnly(localNow.Hour, localNow.Minute));

    /// <summary>
    /// The notice's text: <see cref="Lead"/>, then how to book: a click on it at the notice's time when
    /// <paramref name="canBook"/> (Windows), or <see cref="FromMenu"/>.
    /// </summary>
    public static string Text(CheckInNotice notice, bool canBook) =>
        canBook ? $"{Lead} Click to book arrival at {Time(notice.Time)}." : Lead + FromMenu;

    /// <summary>
    /// The <c>/usr/bin/osascript</c> arguments that show the macOS notice: one fixed AppleScript, holding only the
    /// fixed text of <see cref="Text"/> (no booking from a click, so no time) and <see cref="Title"/>.
    /// </summary>
    public static IReadOnlyList<string> MacScriptArguments() =>
        ["-e", $"display notification \"{Lead}{FromMenu}\" with title \"{Title}\""];

    /// <summary>The time as <c>tm arrive</c> takes it: <c>H:mm</c>, invariant.</summary>
    public static string Time(TimeOnly time) => time.ToString("H:mm", CultureInfo.InvariantCulture);
}
