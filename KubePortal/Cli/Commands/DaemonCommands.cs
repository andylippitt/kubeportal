using System.ComponentModel;
using KubePortal.Grpc;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KubePortal.Cli.Commands;

[Description("Start the KubePortal daemon")]
public class DaemonStartCommand : AsyncCommand<DaemonStartCommand.Settings>
{
    public class Settings : GlobalSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // Check if daemon is already running
        using var client = new KubePortalClient(settings.ApiPort);
        if (await client.IsDaemonRunningAsync())
        {
            if (!settings.Quiet)
                AnsiConsole.MarkupLine("[green]Daemon is already running.[/]");
            return 0;
        }

        // Start the daemon and properly await
        bool started = false;
        await AnsiConsole.Status()
            .StartAsync("Starting KubePortal daemon...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                ctx.Status("Launching daemon process");
                
                var logLevel = LogLevelFromVerbosity(settings.Verbosity);
                started = await DaemonProcess.StartDaemonDetachedAsync(settings.ApiPort, logLevel);
            });

        // Wait a moment for daemon to initialize
        await Task.Delay(1000);

        // Verify daemon is running
        if (await client.IsDaemonRunningAsync())
        {
            if (!settings.Quiet)
                AnsiConsole.MarkupLine("[green]Daemon started successfully.[/]");
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Failed to start daemon.[/]");
            return 1;
        }
    }

    private LogLevel LogLevelFromVerbosity(string verbosity)
    {
        return verbosity?.ToLower() switch
        {
            "debug" => LogLevel.Debug,
            "info" => LogLevel.Information,
            "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => LogLevel.Information
        };
    }
}

[Description("Stop the KubePortal daemon")]
public class DaemonStopCommand : AsyncCommand<DaemonStopCommand.Settings>
{
    public class Settings : GlobalSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // Check if daemon is running
        using var client = new KubePortalClient(settings.ApiPort);
        if (!await client.IsDaemonRunningAsync())
        {
            AnsiConsole.MarkupLine("[yellow]Daemon is not running.[/]");
            return 0;
        }

        // Send shutdown request and properly await it
        await AnsiConsole.Status()
            .StartAsync("Stopping KubePortal daemon...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                
                try
                {
                    await client.ShutdownAsync();
                }
                catch
                {
                    // Expected - connection will be terminated
                }
                
                // Wait a moment for shutdown to complete
                await Task.Delay(1000);
            });

        // Verify daemon has stopped
        if (await client.IsDaemonRunningAsync())
        {
            AnsiConsole.MarkupLine("[red]Failed to stop daemon.[/]");
            return 1;
        }
        else
        {
            if (!settings.Quiet)
                AnsiConsole.MarkupLine("[green]Daemon stopped successfully.[/]");
            return 0;
        }
    }
}

[Description("Reload daemon configuration from disk")]
public class DaemonReloadCommand : AsyncCommand<DaemonReloadCommand.Settings>
{
    public class Settings : GlobalSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // Check if daemon is running
        using var client = new KubePortalClient(settings.ApiPort);
        if (!await client.IsDaemonRunningAsync())
        {
            AnsiConsole.MarkupLine("[red]Daemon is not running.[/]");
            return 1;
        }

        // Send reload request
        var (success, error) = await client.ReloadConfigAsync();
        
        if (success)
        {
            if (!settings.Quiet)
                AnsiConsole.MarkupLine("[green]Configuration reloaded successfully.[/]");
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Failed to reload configuration: {0}[/]", error);
            return 1;
        }
    }
}

[Description("Show daemon status")]
public class DaemonStatusCommand : AsyncCommand<DaemonStatusCommand.Settings>
{
    public class Settings : GlobalSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // Check if daemon is running
        using var client = new KubePortalClient(settings.ApiPort);
        if (!await client.IsDaemonRunningAsync())
        {
            if (settings.Json)
            {
                Console.WriteLine("{\"running\": false}");
            }
            else if (!settings.Quiet)
            {
                AnsiConsole.MarkupLine("[yellow]Daemon is not running.[/]");
            }
            return 1;
        }

        // Get daemon status
        var (running, version, activeForwardCount, totalForwardCount, uptime) = 
            await client.GetStatusAsync();

        if (settings.Json)
        {
            Console.WriteLine($@"{{
  ""running"": true,
  ""version"": ""{version}"",
  ""activeForwardCount"": {activeForwardCount},
  ""totalForwardCount"": {totalForwardCount},
  ""uptimeSeconds"": {(int)uptime.TotalSeconds}
}}");
            return 0;
        }

        if (!settings.Quiet)
        {
            var table = new Table();
            table.AddColumn("Property");
            table.AddColumn("Value");
            
            table.AddRow("Status", "[green]Running[/]");
            table.AddRow("Version", version);
            table.AddRow("Active Forwards", activeForwardCount.ToString());
            table.AddRow("Total Forwards", totalForwardCount.ToString());
            table.AddRow("Uptime", FormatUptime(uptime));
            
            AnsiConsole.Write(table);
        }
        
        return 0;
    }

    private string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
        {
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s";
        }
        else if (uptime.TotalHours >= 1)
        {
            return $"{uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s";
        }
        else if (uptime.TotalMinutes >= 1)
        {
            return $"{uptime.Minutes}m {uptime.Seconds}s";
        }
        else
        {
            return $"{uptime.Seconds}s";
        }
    }
}
