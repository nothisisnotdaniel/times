using Spectre.Console;
using Times;

return new App(AnsiConsole.Console, TimeProvider.System).Run(args);
