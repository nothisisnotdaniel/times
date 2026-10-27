using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Times;

/// <summary>Renders the current day. The current day starts at today, read from <see cref="TimeProvider"/>.</summary>
internal sealed class DayView(TimeProvider time)
{
    public DateOnly Current { get; } = DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    public static string Heading(DateOnly day) => day.ToString("dddd, yyyy-MM-dd", CultureInfo.InvariantCulture);

    public IRenderable Render() =>
        new Rows(
            new Rule($"[steelblue1]{Markup.Escape(Heading(Current))}[/]").LeftJustified().RuleStyle(Style.Parse("grey")),
            new Markup("[grey]No entries yet.[/]"));
}
