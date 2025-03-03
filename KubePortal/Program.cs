using KubePortal.Cli.Commands;
using KubePortal.Grpc;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

// Entry point (top-level statements)
if (args.Contains("--internal-daemon-run"))
{
    // Run daemon mode
    await RunDaemonAsync(args);
}
else
{
    // Run CLI mode
    await RunCliAsync(args);
}

// Helper methods
static async Task<int> RunDaemonAsync(string[] args)
{
    // Extract port and verbosity from args
    int port = 50051; // Default
    LogLevel logLevel = LogLevel.Information; // Default

    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--api-port" && int.TryParse(args[i + 1], out int parsedPort))
            port = parsedPort;

        if (args[i] == "--verbosity")
        {
            logLevel = args[i + 1].ToLower() switch
            {
                "debug" => LogLevel.Debug,
                "info" => LogLevel.Information,
                "warn" => LogLevel.Warning,
                "error" => LogLevel.Error,
                _ => LogLevel.Information
            };
        }
    }

    return await DaemonProcess.RunDaemonAsync(port, logLevel);
}



static async Task<int> RunCliAsync(string[] args)
{
    var app = new CommandApp();
    
    app.Configure(config =>
    {
        // Set application info
        config.SetApplicationName("kubeportal");
        
        // Register daemon commands
        config.AddBranch<CommandSettings>("daemon", daemon =>
        {
            daemon.SetDescription("Manage the KubePortal daemon");
            daemon.AddCommand<DaemonStartCommand>("start");
            daemon.AddCommand<DaemonStopCommand>("stop");
            daemon.AddCommand<DaemonReloadCommand>("reload");
            daemon.AddCommand<DaemonStatusCommand>("status");
        });
        
        // Register forward commands
        config.AddBranch<CommandSettings>("forward", forward =>
        {
            forward.SetDescription("Manage port forwards");
            forward.AddCommand<ForwardListCommand>("list");
            forward.AddCommand<ForwardCreateCommand>("create");
            forward.AddCommand<ForwardDeleteCommand>("delete");
            forward.AddCommand<ForwardStartCommand>("start");
            forward.AddCommand<ForwardStopCommand>("stop");
        });
        
        // Register group commands
        config.AddBranch<CommandSettings>("group", group =>
        {
            group.SetDescription("Manage forward groups");
            group.AddCommand<GroupListCommand>("list");
            group.AddCommand<GroupEnableCommand>("enable");
            group.AddCommand<GroupDisableCommand>("disable");
            group.AddCommand<GroupDeleteCommand>("delete");
        });
        
        // Register configuration commands
        config.AddCommand<ApplyCommand>("apply");
        config.AddCommand<ExportCommand>("export");
        
        // Default command (show help)
        config.AddCommand<DefaultCommand>("");
    });
    
    return await app.RunAsync(args);
}

// Default command class for showing help
[Description("KubePortal - Kubernetes port forwarding and socket proxy tool")]
class DefaultCommand : AsyncCommand<DefaultCommand.Settings>
{
    public class Settings : CommandSettings
    {
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // Display welcome message and basic help
        AnsiConsole.Write(
            new FigletText("KubePortal")
                .Color(Color.Blue));

        AnsiConsole.WriteLine("Kubernetes port forwarding and TCP socket proxy tool");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Usage:");
        AnsiConsole.WriteLine("  kubeportal [command] [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Commands:");
        AnsiConsole.WriteLine("  daemon    Manage the KubePortal daemon");
        AnsiConsole.WriteLine("  forward   Manage port forwards");
        AnsiConsole.WriteLine("  group     Manage forward groups");
        AnsiConsole.WriteLine("  apply     Apply a configuration file");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Get started by running:");
        AnsiConsole.WriteLine("  kubeportal daemon start");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("For more details, run:");
        AnsiConsole.WriteLine("  kubeportal [command] --help");

        return Task.FromResult(0);
    }
}

