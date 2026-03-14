using System.ComponentModel;
using System.Reflection;
using Serilog.Events;
using Spectre.Console.Cli;

namespace Rewrite.Server.Commands;

public class BaseSettings : CommandSettings
{
    [CommandOption("-l|--logfile")]
    [Description("Log file path. Default: disabled")]
    public string? LogFilePath { get; set; }

    [CommandOption("-v|--verbosity <LEVEL>")]
    [Description("Log verbosity level. Valid values: verbose, debug, information, warning, error, fatal. Default: information")]
    public LogEventLevel Verbosity { get; set; } = LogEventLevel.Information;

    [CommandOption("--nuget-config-root <NUGET_CONFIG_DIR>")]
    [Description("Root directory from which to search for nuget.config files. Can be set with MODERNE_CONFIG_DIR environmental variable. Defaults to current assembly location")]
    public string? NugetConfigRoot { get; set; } = Environment.GetEnvironmentVariable("MODERNE_CONFIG_DIR") ?? System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
}