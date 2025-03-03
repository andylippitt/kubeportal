using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KubePortal.Cli.Commands;

[Description("Export configuration to JSON")]
public class ExportCommand : AsyncCommand<ExportCommand.Settings>
{
    public class Settings : GlobalSettings
    {
        [CommandOption("--include-disabled")]
        [Description("Include disabled forwards in export")]
        [DefaultValue(true)]
        public bool IncludeDisabled { get; set; } = true;

        [CommandOption("-g|--group <GROUP>")]
        [Description("Only export forwards from this group")]
        public string? GroupFilter { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var client = new KubePortalClient(settings.ApiPort);
        if (!await client.IsDaemonRunningAsync())
        {
            AnsiConsole.MarkupLine("[red]Daemon is not running.[/]");
            return 1;
        }

        try
        {
            string configJson = await client.ExportConfigAsync(settings.IncludeDisabled, settings.GroupFilter);
            
            if (settings.Json)
            {
                // Already JSON, so just output it directly
                Console.WriteLine(configJson);
            }
            else
            {
                Console.WriteLine(configJson);
            }
            
            return 0;
        }
        catch (Exception ex)
        {
            if (!settings.Json)
                AnsiConsole.MarkupLine($"[red]Failed to export configuration: {ex.Message}[/]");
            else
                Console.WriteLine($"{{ \"success\": false, \"error\": \"{ex.Message.Replace("\"", "\\\"")}\" }}");
                
            return 1;
        }
    }
}
