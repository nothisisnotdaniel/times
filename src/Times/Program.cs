using Spectre.Console;
using Times;

// An empty app-data path must stay empty, so App's folder check catches it instead of writing to "Times".
string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
string dataFolder = string.IsNullOrEmpty(appData) ? string.Empty : Path.Combine(appData, "Times");
return new App(AnsiConsole.Console, TimeProvider.System, new DayStore(dataFolder)).Run(args);
