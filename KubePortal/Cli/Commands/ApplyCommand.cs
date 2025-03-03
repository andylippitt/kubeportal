using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KubePortal.Cli.Commands;

[Description("Apply a configuration file")]
public class ApplyCommand : AsyncCommand<ApplyCommand.Settings>
{
    public class Settings : GlobalSettings
    {
        [CommandOption("-f|--file <FILE>")]
        [Description("Path to JSON configuration file")]
        public string? ConfigFile { get; set; } // Fix: removed extra closing brace
        
        [CommandOption("-g|--group <GROUP>")]
        [Description("Default group for forwards without a group")]
        public string? DefaultGroup { get; set; } // Fix: removed extra closing brace
        
        [CommandOption("--remove-missing")]
        [Description("Remove forwards that are not in the configuration file")]
        public bool RemoveMissing { get; set; }
        
        [CommandOption("--stdin")]
        [Description("Read configuration from standard input")]
        public bool ReadFromStdin { get; set; }
        
        public override ValidationResult Validate()
        {
            if (!ReadFromStdin && string.IsNullOrWhiteSpace(ConfigFile))
                return ValidationResult.Error("Either --file or --stdin must be specified");
                
            if (ReadFromStdin && !string.IsNullOrWhiteSpace(ConfigFile))
                return ValidationResult.Error("Cannot use both --file and --stdin");
                
            if (!ReadFromStdin && !File.Exists(ConfigFile))
                return ValidationResult.Error($"Configuration file not found: {ConfigFile}");
                
            return base.Validate();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var client = new KubePortalClient(settings.ApiPort);
        if (!await client.IsDaemonRunningAsync())
        {
            AnsiConsole.MarkupLine("[red]Daemon is not running.[/]");
            return 1;
        }

        // Read config from file or stdin
        string configJson;
        try
        {
            if (settings.ReadFromStdin)
            {
                using var reader = new StreamReader(Console.OpenStandardInput());
                configJson = await reader.ReadToEndAsync();
            }
            else
            {
                configJson = await File.ReadAllTextAsync(settings.ConfigFile!);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to read configuration: {ex.Message}[/]");
            return 1;
        }

        // Apply the configuration
        await AnsiConsole.Status()
            .StartAsync("Applying configuration...", async ctx =>
            {
                await Task.CompletedTask;
                ctx.Spinner(Spinner.Known.Dots);
            });
            
        var (success, added, updated, removed, error) = await client.ApplyConfigAsync(
            configJson, settings.DefaultGroup, settings.RemoveMissing);

        if (success)
        {
            if (!settings.Quiet)
            {
                if (settings.Json)
                {
                    Console.WriteLine($@"{{
  ""success"": true,
  ""added"": {added},
  ""updated"": {updated},
  ""removed"": {removed}
}}");
                }
                else
                {
                    AnsiConsole.MarkupLine("[green]Configuration applied successfully.[/]");
                    AnsiConsole.MarkupLine($"Added: {added}, Updated: {updated}, Removed: {removed}");
                }
            }
            return 0;
        }
        else
        {
            if (settings.Json)
            {
                Console.WriteLine($@"{{
  ""success"": false,
  ""error"": ""{error.Replace("\"", "\\\"")}""
}}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to apply configuration: {error}[/]");
            }
            return 1;
        }
    }
}
