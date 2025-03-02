using System.Text.Json;
using System.Text.Json.Nodes;

namespace KubePortal.Core.Models;

public abstract class ForwardDefinition
{
    // Name is the primary identifier (must be unique)
    public string Name { get; set; } = "";
    
    // Group for organization
    public string Group { get; set; } = "default";
    
    // Configuration
    public int LocalPort { get; set; }
    public bool Enabled { get; set; } = true;
    
    // Type discriminator
    public abstract string ForwardType { get; }
    
    // Factory method
    public abstract IForwarder CreateForwarder(ILoggerFactory loggerFactory);
    
    // Serialization
    public abstract JsonObject ToJson();
    
    public static ForwardDefinition FromJson(JsonNode json)
    {
        var type = json["type"]?.GetValue<string>() ?? 
            throw new JsonException("Missing 'type' property");
        
        var name = json["name"]?.GetValue<string>() ?? 
            throw new JsonException("Missing 'name' property");
            
        var group = json["group"]?.GetValue<string>() ?? "default";
        var localPort = json["localPort"]?.GetValue<int>() ?? 0;
        var enabled = json["enabled"]?.GetValue<bool>() ?? true;
        
        ForwardDefinition result;
        
        switch (type)
        {
            case "kubernetes":
                var k8s = new KubernetesForwardDefinition
                {
                    Name = name,
                    Group = group,
                    LocalPort = localPort,
                    Enabled = enabled,
                    Context = json["context"]?.GetValue<string>() ?? "",
                    Namespace = json["namespace"]?.GetValue<string>() ?? "",
                    Service = json["service"]?.GetValue<string>() ?? "",
                    ServicePort = json["servicePort"]?.GetValue<int>() ?? 0
                };
                result = k8s;
                break;
            
            case "socket":
                var socket = new SocketProxyDefinition
                {
                    Name = name,
                    Group = group,
                    LocalPort = localPort,
                    Enabled = enabled,
                    RemoteHost = json["remoteHost"]?.GetValue<string>() ?? "",
                    RemotePort = json["remotePort"]?.GetValue<int>() ?? 0
                };
                result = socket;
                break;
            
            default:
                throw new JsonException($"Unknown forward type: {type}");
        }
        
        return result;
    }
    
    // Validation
    public virtual bool Validate(out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            errorMessage = "Name cannot be empty";
            return false;
        }
        
        if (LocalPort <= 0 || LocalPort > 65535)
        {
            errorMessage = $"Invalid port: {LocalPort}";
            return false;
        }
        
        errorMessage = "";
        return true;
    }
}

public class KubernetesForwardDefinition : ForwardDefinition
{
    public string Context { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Service { get; set; } = "";
    public int ServicePort { get; set; }
    public override string ForwardType => "kubernetes";
    
    public override IForwarder CreateForwarder(ILoggerFactory loggerFactory) => 
        new KubernetesForwarder(this, loggerFactory);
    
    public override JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["type"] = ForwardType,
            ["name"] = Name,
            ["group"] = Group,
            ["localPort"] = LocalPort,
            ["enabled"] = Enabled,
            ["context"] = Context,
            ["namespace"] = Namespace,
            ["service"] = Service,
            ["servicePort"] = ServicePort
        };
        
        return json;
    }
    
    public override bool Validate(out string errorMessage)
    {
        if (!base.Validate(out errorMessage)) return false;
        
        if (string.IsNullOrWhiteSpace(Context))
        {
            errorMessage = "Context cannot be empty";
            return false;
        }
        
        if (string.IsNullOrWhiteSpace(Namespace))
        {
            errorMessage = "Namespace cannot be empty";
            return false;
        }
        
        if (string.IsNullOrWhiteSpace(Service))
        {
            errorMessage = "Service cannot be empty";
            return false;
        }
        
        if (ServicePort <= 0 || ServicePort > 65535)
        {
            errorMessage = $"Invalid service port: {ServicePort}";
            return false;
        }
        
        return true;
    }
}


public class SocketProxyDefinition : ForwardDefinition
{
    public string RemoteHost { get; set; } = "";
    public int RemotePort { get; set; }
    public override string ForwardType => "socket";
    
    public override IForwarder CreateForwarder(ILoggerFactory loggerFactory) => 
        new SocketProxyForwarder(this, loggerFactory);
    
    public override JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["type"] = ForwardType,
            ["name"] = Name,
            ["group"] = Group,
            ["localPort"] = LocalPort,
            ["enabled"] = Enabled,
            ["remoteHost"] = RemoteHost,
            ["remotePort"] = RemotePort
        };
        
        return json;
    }
    
    public override bool Validate(out string errorMessage)
    {
        if (!base.Validate(out errorMessage)) return false;
        
        if (string.IsNullOrWhiteSpace(RemoteHost))
        {
            errorMessage = "Remote host cannot be empty";
            return false;
        }
        
        if (RemotePort <= 0 || RemotePort > 65535)
        {
            errorMessage = $"Invalid remote port: {RemotePort}";
            return false;
        }
        
        return true;
    }
}
