using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace Rewrite.Diagnostics;

public static class Logging
{
    public static void ConfigureLogging(string? logFilePath = null, bool noColor = false, LogEventLevel minimumLevel = LogEventLevel.Information)
    {
        ConsoleTheme? theme = noColor ? ConsoleTheme.None : AnsiConsoleTheme.Literate;
        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Destructure.With<PrettyJsonDestructuringPolicy>()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}", theme: theme);
        if (logFilePath != null)
        {
            loggerConfig = loggerConfig.WriteTo.File(logFilePath);
        }

        Log.Logger = loggerConfig.CreateLogger();
    }
}