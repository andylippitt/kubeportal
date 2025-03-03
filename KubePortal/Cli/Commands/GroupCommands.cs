using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KubePortal.Cli.Commands;

[Description("Manage forward groups")]
public class GroupCommand : AsyncCommand<GroupCommand.Settings>
{
    public class Settings : CommandSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        await Task.CompletedTask;
        AnsiConsole.MarkupLine("[yellow]Please specify a group command: list, enable, or disable[/]");
        return 0;
    }
}

[Description("List all forward groups")]
public class GroupListCommand : AsyncCommand<GroupListCommand.Settings>
{
    public class Settings : GlobalSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var client = new KubePortalClient(settings.ApiPort);
        if (!await client.IsDaemonRunningAsync())
        {
            AnsiConsole.MarkupLine("[red]Daemon is not running.[/]");
            return 1;
        }

        var groups = await client.ListGroupsAsync();
        
        if (settings.Json)
        {
            // Create JSON output
            var jsonArray = new System.Text.Json.Nodes.JsonArray();
            foreach (var group in groups)
            {
                jsonArray.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = group.Name,
                    ["enabled"] = group.Enabled,
                    ["forwardCount"] = group.ForwardCount,
                    ["activeForwardCount"] = group.ActiveForwardCount
                });
            }
            
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(jsonArray, options));
            return 0;
        }

        if (groups.Length == 0)
        {
            if (!settings.Quiet)
                AnsiConsole.MarkupLine("No forward groups found.");
            return 0;
        }

        if (!settings.Quiet)
        {
            var table = new Table();
            
            // Configure columns
            table.AddColumn("Group");
            table.AddColumn("Status");
            table.AddColumn("Forwards");
            table.AddColumn("Active");
            
            // Add rows
            foreach (var group in groups.OrderBy(g => g.Name))
            {
                string statusStr = group.Enabled ? "[green]Enabled[/]" : "[grey]Disabled[/]";
                
                table.AddRow(
                    group.Name,
                    statusStr,
                    group.ForwardCount.ToString(),
                    group.ActiveForwardCount.ToString()
                );
            }
            
            AnsiConsole.Write(table);
        }
        
        return 0;
    }
}

[Description("Enable a forward group")]
public class GroupEnableCommand : AsyncCommand<GroupEnableCommand.Settings>
{
    public class Settings : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Name of the group to enable")]
        public required string Name { get; set; } // Make required
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var client = new KubePortalClient(settings.ApiPort);
        if (!await client.IsDaemonRunningAsync())
        {
            AnsiConsole.MarkupLine("[red]Daemon is not running.[/]");
            return 1;
        }

        var (success, error) = await client.EnableGroupAsync(settings.Name);
        
        if (success)
        {
            if (!settings.Quiet && !settings.Json)
                AnsiConsole.MarkupLine($"[green]Group '{settings.Name}' enabled successfully.[/]");
                
            if (settings.Json)
                Console.WriteLine($"{{ \"success\": true, \"name\": \"{settings.Name}\" }}");
                
            return 0;
        }
        else
        {
            if (!settings.Json)
                AnsiConsole.MarkupLine($"[red]Failed to enable group: {error}[/]");
                
            if (settings.Json)
                Console.WriteLine($"{{ \"success\": false, \"error\": \"{error}\" }}");
                
            return 1;
        }
    }
}

[Description("Disable a forward group")]
public class GroupDisableCommand : AsyncCommand<GroupDisableCommand.Settings>
{
    public class Settings : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Name of the group to disable")]
        public required string Name { get; set; } // Make required
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var client = new KubePortalClient(settings.ApiPort);
        if (!await client.IsDaemonRunningAsync())
        {
            AnsiConsole.MarkupLine("[red]Daemon is not running.[/]");
            return 1;
        }

        var (success, error) = await client.DisableGroupAsync(settings.Name);
        
        if (success)
        {
            if (!settings.Quiet && !settings.Json)
                AnsiConsole.MarkupLine($"[green]Group '{settings.Name}' disabled successfully.[/]");
                
            if (settings.Json)
                Console.WriteLine($"{{ \"success\": true, \"name\": \"{settings.Name}\" }}");
                
            return 0;
        }
        else
        {
            if (!settings.Json)
                AnsiConsole.MarkupLine($"[red]Failed to disable group: {error}[/]");
                
            if (settings.Json)
                Console.WriteLine($"{{ \"success\": false, \"error\": \"{error}\" }}");
                
            return 1;
        }
    }
}
