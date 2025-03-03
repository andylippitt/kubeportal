using Spectre.Console.Cli;

namespace KubePortal.Cli.Commands;

// Base class for commands that need their own settings class
public abstract class BaseCommand<TSettings> : AsyncCommand<TSettings>
    where TSettings : CommandSettings
{
}

// Base class for category commands that act as containers for subcommands
public abstract class BaseCategoryCommand<TSettings> : BaseCommand<TSettings> 
    where TSettings : CommandSettings, new()
{
    public static TSettings GetDefaultSettings() => new TSettings();
}
