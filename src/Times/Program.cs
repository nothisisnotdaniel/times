using Spectre.Console;
using Times;

string dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Times");
return new App(AnsiConsole.Console, TimeProvider.System, new DayStore(dataFolder)).Run(args);
