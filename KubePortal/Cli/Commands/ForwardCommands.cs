using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KubePortal.Cli.Commands;

[Description("Manage port forwards")]
public class ForwardCommand : AsyncCommand<ForwardCommand.Settings>
{
    public class Settings : CommandSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        await Task.CompletedTask;
        AnsiConsole.MarkupLine("[yellow]Please specify a forward command: list, create, delete, start, or stop[/]");
        return 0;
    }
}

[Description("List all port forwards")]
public class ForwardListCommand : AsyncCommand<ForwardListCommand.Settings>
{
    public class Settings : GlobalSettings
    {
        [CommandOption("-g|--group <GROUP>")]
        [Description("Filter forwards by group")]
        public string? Group { get; set; } // Make nullable
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var client = new KubePortalClient(settings.ApiPort);
        if (!await client.IsDaemonRunningAsync())
        {
            AnsiConsole.MarkupLine("[red]Daemon is not running.[/]");
            return 1;
        }

        var (forwards, statuses) = await client.ListForwardsAsync(settings.Group);

        if (settings.Json)
        {
            // Create JSON output
            var jsonArray = new System.Text.Json.Nodes.JsonArray();
            for (int i = 0; i < forwards.Length; i++)
            {
                var forward = forwards[i];
                var status = statuses[i];
                
                var jsonObj = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = forward.Name,
                    ["group"] = forward.Group,
                    ["localPort"] = forward.LocalPort,
                    ["enabled"] = forward.Enabled,
                    ["type"] = forward.ForwardType,
                    ["active"] = status.Active,
                    ["bytesTransferred"] = status.BytesTransferred,
                    ["connectionCount"] = status.ConnectionCount
                };
                
                // Add specific properties based on forward type
                if (forward is SocketProxyDefinition socketForward)
                {
                    jsonObj["remoteHost"] = socketForward.RemoteHost;
                    jsonObj["remotePort"] = socketForward.RemotePort;
                }
                else if (forward is KubernetesForwardDefinition k8sForward)
                {
                    jsonObj["context"] = k8sForward.Context;
                    jsonObj["namespace"] = k8sForward.Namespace;
                    jsonObj["service"] = k8sForward.Service;
                    jsonObj["servicePort"] = k8sForward.ServicePort;
                }
                
                jsonArray.Add(jsonObj);
            }
            
            var options = new JsonSerializerOptions { WriteIndented = true };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(jsonArray, options));
            return 0;
        }

        if (forwards.Length == 0)
        {
            if (!settings.Quiet)
                AnsiConsole.MarkupLine(settings.Group != null 
                    ? $"No forwards found in group '{settings.Group}'." 
                    : "No forwards configured.");
            return 0;
        }

        if (!settings.Quiet)
        {
            var table = new Table();
            
            // Configure columns
            table.AddColumn("Name");
            table.AddColumn("Group");
            table.AddColumn("Status");
            table.AddColumn("Local Port");
            table.AddColumn("Remote");
            table.AddColumn("Bytes");
            table.AddColumn("Conns");
            
            // Add rows
            for (int i = 0; i < forwards.Length; i++)
            {
                var forward = forwards[i];
                var status = statuses[i];
                
                string remoteInfo;
                if (forward is SocketProxyDefinition socketForward)
                {
                    remoteInfo = $"{socketForward.RemoteHost}:{socketForward.RemotePort}";
                }
                else if (forward is KubernetesForwardDefinition k8sForward)
                {
                    remoteInfo = $"{k8sForward.Service}:{k8sForward.ServicePort} ({k8sForward.Namespace})";
                }
                else
                {
                    remoteInfo = "Unknown";
                }
                
                string statusStr = status.Active 
                    ? "[green]Active[/]" 
                    : (forward.Enabled ? "[yellow]Pending[/]" : "[grey]Disabled[/]");
                
                string bytesStr = FormatBytes(status.BytesTransferred);
                
                table.AddRow(
                    forward.Name,
                    forward.Group,
                    statusStr,
                    forward.LocalPort.ToString(),
                    remoteInfo,
                    bytesStr,
                    status.ConnectionCount.ToString()
                );
            }
            
            AnsiConsole.Write(table);
        }
        
        return 0;
    }
    
    private string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int suffixIndex = 0;
        double value = bytes;
        
        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }
        
        return suffixIndex == 0 
            ? bytes.ToString() 
            : value.ToString("0.##") + suffixes[suffixIndex];
    }
}

[Description("Create a new port forward")]
public class ForwardCreateCommand : AsyncCommand<ForwardCreateCommand.Settings>
{
    public class Settings : GlobalSettings
    {
        [CommandOption("-n|--name <NAME>")]
        [Description("Name for the forward")]
        public string? Name { get; set; } // Make nullable
        
        [CommandOption("-g|--group <GROUP>")]
        [Description("Group for the forward")]
        public string Group { get; set; } = "default";
        
        [CommandOption("-l|--local-port <PORT>")]
        [Description("Local port to listen on")]
        public int LocalPort { get; set; }
        
        [CommandOption("--disabled")]
        [Description("Create the forward in disabled state")]
        public bool Disabled { get; set; }
        
        [CommandOption("-t|--type <TYPE>")]
        [Description("Forward type: socket or kubernetes")]
        public string ForwardType { get; set; } = "socket";
        
        // Socket-specific options
        [CommandOption("-h|--remote-host <HOST>")]
        [Description("Remote host for socket forwards")]
        public string? RemoteHost { get; set; } // Make nullable
        
        [CommandOption("-p|--remote-port <PORT>")]
        [Description("Remote port for socket forwards")]
        public int RemotePort { get; set; }
        
        // Kubernetes-specific options
        [CommandOption("-c|--context <CONTEXT>")]
        [Description("Kubernetes context")]
        public string? Context { get; set; } // Make nullable
        
        [CommandOption("--namespace <NAMESPACE>")]
        [Description("Kubernetes namespace")]
        public string? Namespace { get; set; } // Make nullable
        
        [CommandOption("-s|--service <SERVICE>")]
        [Description("Kubernetes service name")]
        public string? Service { get; set; } // Make nullable
        
        [CommandOption("--service-port <PORT>")]
        [Description("Kubernetes service port")]
        public int ServicePort { get; set; }

        public override ValidationResult Validate()
        {
            // Common validations
            if (string.IsNullOrWhiteSpace(Name))
                return ValidationResult.Error("Name is required");
                
            if (LocalPort <= 0 || LocalPort > 65535)
                return ValidationResult.Error($"Invalid local port: {LocalPort}");
                
            // Type-specific validations
            if (ForwardType.ToLower() == "socket")
            {
                if (string.IsNullOrWhiteSpace(RemoteHost))
                    return ValidationResult.Error("Remote host is required for socket forwards");
                    
                if (RemotePort <= 0 || RemotePort > 65535)
                    return ValidationResult.Error($"Invalid remote port: {RemotePort}");
            }
            else if (ForwardType.ToLower() == "kubernetes")
            {
                if (string.IsNullOrWhiteSpace(Context))
                    return ValidationResult.Error("Kubernetes context is required");
                    
                if (string.IsNullOrWhiteSpace(Namespace))
                    return ValidationResult.Error("Kubernetes namespace is required");
                    
                if (string.IsNullOrWhiteSpace(Service))
                    return ValidationResult.Error("Kubernetes service is required");
                    
                if (ServicePort <= 0 || ServicePort > 65535)
                    return ValidationResult.Error($"Invalid service port: {ServicePort}");
            }
            else
            {
                return ValidationResult.Error($"Unknown forward type: {ForwardType}. Use 'socket' or 'kubernetes'");
            }
            
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

        // Create the appropriate forward based on type
        ForwardDefinition forward;
        
        if (settings.ForwardType.ToLower() == "socket")
        {
            // Fix the syntax errors - move the null checks before the object initialization
            if (settings.Name == null)
                throw new InvalidOperationException("Name is required");
                
            if (settings.RemoteHost == null) 
                throw new InvalidOperationException("RemoteHost is required");
                
            forward = new SocketProxyDefinition
            {
                Name = settings.Name,
                Group = settings.Group,
                LocalPort = settings.LocalPort,
                Enabled = !settings.Disabled,
                RemoteHost = settings.RemoteHost,
                RemotePort = settings.RemotePort
            };
        }
        else // kubernetes
        {
            // Fix the syntax errors - move the null checks before the object initialization
            if (settings.Name == null)
                throw new InvalidOperationException("Name is required");
                
            if (settings.Context == null)
                throw new InvalidOperationException("Context is required");
                
            if (settings.Namespace == null)
                throw new InvalidOperationException("Namespace is required");
                
            if (settings.Service == null)
                throw new InvalidOperationException("Service is required");
                
            forward = new KubernetesForwardDefinition
            {
                Name = settings.Name,
                Group = settings.Group,
                LocalPort = settings.LocalPort,
                Enabled = !settings.Disabled,
                Context = settings.Context,
                Namespace = settings.Namespace,
                Service = settings.Service,
                ServicePort = settings.ServicePort
            };
        }

        // Send to server
        var (success, error) = await client.CreateForwardAsync(forward);
        
        if (success)
        {
            if (!settings.Quiet)
            {
                AnsiConsole.MarkupLine($"[green]Forward '{settings.Name}' created successfully.[/]");
                
                if (settings.Disabled)
                    AnsiConsole.MarkupLine($"Forward is disabled. Use [bold]kubeportal forward start {settings.Name}[/] to start it.");
            }
            
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Failed to create forward: {error}[/]");
            return 1;
        }
    }
}

[Description("Delete a port forward")]
public class ForwardDeleteCommand : AsyncCommand<ForwardDeleteCommand.Settings>
{
    public class Settings : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Name of the forward to delete")]
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

        // Confirm deletion unless quiet mode or JSON mode
        if (!settings.Quiet && !settings.Json)
        {
            if (!AnsiConsole.Confirm($"Are you sure you want to delete the forward '{settings.Name}'?"))
                return 0;
        }

        var (success, error) = await client.DeleteForwardAsync(settings.Name);
        
        if (success)
        {
            if (!settings.Quiet && !settings.Json)
                AnsiConsole.MarkupLine($"[green]Forward '{settings.Name}' deleted successfully.[/]");
                
            if (settings.Json)
                Console.WriteLine($"{{ \"success\": true, \"name\": \"{settings.Name}\" }}");
                
            return 0;
        }
        else
        {
            if (!settings.Json)
                AnsiConsole.MarkupLine($"[red]Failed to delete forward: {error}[/]");
                
            if (settings.Json)
                Console.WriteLine($"{{ \"success\": false, \"error\": \"{error}\" }}");
                
            return 1;
        }
    }
}

[Description("Start a port forward")]
public class ForwardStartCommand : AsyncCommand<ForwardStartCommand.Settings>
{
    public class Settings : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Name of the forward to start")]
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

        var (success, error) = await client.StartForwardAsync(settings.Name);
        
        if (success)
        {
            if (!settings.Quiet && !settings.Json)
                AnsiConsole.MarkupLine($"[green]Forward '{settings.Name}' started successfully.[/]");
                
            if (settings.Json)
                Console.WriteLine($"{{ \"success\": true, \"name\": \"{settings.Name}\" }}");
                
            return 0;
        }
        else
        {
            if (!settings.Json)
                AnsiConsole.MarkupLine($"[red]Failed to start forward: {error}[/]");
                
            if (settings.Json)
                Console.WriteLine($"{{ \"success\": false, \"error\": \"{error}\" }}");
                
            return 1;
        }
    }
}

[Description("Stop a port forward")]
public class ForwardStopCommand : AsyncCommand<ForwardStopCommand.Settings>
{
    public class Settings : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Name of the forward to stop")]
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

        var (success, error) = await client.StopForwardAsync(settings.Name);
        
        if (success)
        {
            if (!settings.Quiet && !settings.Json)
                AnsiConsole.MarkupLine($"[green]Forward '{settings.Name}' stopped successfully.[/]");
                
            if (settings.Json)
                Console.WriteLine($"{{ \"success\": true, \"name\": \"{settings.Name}\" }}");
                
            return 0;
        }
        else
        {
            if (!settings.Json)
                AnsiConsole.MarkupLine($"[red]Failed to stop forward: {error}[/]");
                
            if (settings.Json)
                Console.WriteLine($"{{ \"success\": false, \"error\": \"{error}\" }}");
                
            return 1;
        }
    }
}