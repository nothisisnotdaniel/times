using Spectre.Console;
using Times;

// An empty app-data path stays empty, so App's folder check catches it instead of writing to "Times".
DataFolder dataFolder = DataFolder.Resolve(
    Environment.GetEnvironmentVariable(DataFolder.VariableName),
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
App app = new App(AnsiConsole.Console, TimeProvider.System, new DayStore(dataFolder.Path), folderFromEnvironment: dataFolder.FromEnvironment);
return app.Run(args);
