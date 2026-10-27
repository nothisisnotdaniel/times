using Spectre.Console;

namespace Times;

/// <summary>
/// Entry point for the Times console app. All output goes through <see cref="IAnsiConsole"/>
/// and all clock reads through <see cref="TimeProvider"/>, so both can be faked in tests.
/// </summary>
internal sealed class App(IAnsiConsole console, TimeProvider time)
{
    public int Run(string[] args)
    {
        console.Write(new FigletText("Times").Color(Color.SteelBlue1));

        var now = time.GetLocalNow();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[grey]Zone[/]")
            .AddColumn("[grey]Time[/]")
            .AddRow(Markup.Escape(time.LocalTimeZone.Id), now.ToString("yyyy-MM-dd HH:mm:ss"));

        console.Write(table);
        return 0;
    }
}
