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

            // deserialize and reserialize to format the JSON
            configJson = System.Text.Json.JsonSerializer.Serialize(
                System.Text.Json.JsonSerializer.Deserialize<object>(configJson),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            if (!settings.Json)
                AnsiConsole.Render(new Markup(configJson));
            else
                Console.WriteLine(configJson);

            return 0;
        }
        catch (Exception ex)
        {
            if (!settings.Json)
                AnsiConsole.MarkupLine($"[red]Failed to export configuration: {ex.Message}[/]");
            else
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
                    success = false,
                    error = ex.Message
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }

            return 1;
        }
    }
}
