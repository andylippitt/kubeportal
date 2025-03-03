using Grpc.Net.Client;
using KubePortal.Grpc;

namespace KubePortal.Cli;

// Implement both IDisposable and IAsyncDisposable
public class KubePortalClient : IAsyncDisposable, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly KubePortalService.KubePortalServiceClient _client;
    private readonly string _serverAddress;

    public KubePortalClient(int port)
    {
        _serverAddress = $"http://localhost:{port}";
        _channel = GrpcChannel.ForAddress(_serverAddress);
        _client = new KubePortalService.KubePortalServiceClient(_channel);
    }

    // IDisposable implementation
    public void Dispose()
    {
        // Simple blocking dispose - not ideal but works for CLI usage
        _channel.Dispose();
    }

    // IAsyncDisposable implementation
    public async ValueTask DisposeAsync()
    {
        await _channel.ShutdownAsync();
    }

    // Check if daemon is responding
    public async Task<bool> IsDaemonRunningAsync()
    {
        try
        {
            var response = await _client.GetStatusAsync(new GetStatusRequest());
            return response.Running;
        }
        catch
        {
            return false;
        }
    }

    // Forward management
    public async Task<(bool Success, string Error)> CreateForwardAsync(Core.ForwardDefinition forward)
    {
        var request = new CreateForwardRequest
        {
            Forward = ConvertToGrpcForward(forward)
        };

        var response = await _client.CreateForwardAsync(request);
        return (response.Success, response.Error);
    }

    public async Task<(bool Success, string Error)> DeleteForwardAsync(string name)
    {
        var request = new DeleteForwardRequest { Name = name };
        var response = await _client.DeleteForwardAsync(request);
        return (response.Success, response.Error);
    }

    public async Task<(Core.ForwardDefinition[] Forwards, ForwardStatus[] Statuses)> ListForwardsAsync(string? groupFilter = null)
    {
        var request = new ListForwardsRequest { GroupFilter = groupFilter ?? "" };
        var response = await _client.ListForwardsAsync(request);
        
        var forwards = new Core.ForwardDefinition[response.Forwards.Count];
        var statuses = new ForwardStatus[response.Statuses.Count];
        
        for (int i = 0; i < response.Forwards.Count; i++)
        {
            forwards[i] = ConvertFromGrpcForward(response.Forwards[i]);
        }
        
        for (int i = 0; i < response.Statuses.Count; i++)
        {
            statuses[i] = response.Statuses[i];
        }
        
        return (forwards, statuses);
    }

    public async Task<(bool Found, Core.ForwardDefinition? Forward, ForwardStatus? Status)> GetForwardAsync(string name)
    {
        var request = new GetForwardRequest { Name = name };
        var response = await _client.GetForwardAsync(request);
        
        if (!response.Found)
            return (false, null, null);
        
        return (true, ConvertFromGrpcForward(response.Forward), response.Status);
    }

    public async Task<(bool Success, string Error)> StartForwardAsync(string name)
    {
        var request = new StartForwardRequest { Name = name };
        var response = await _client.StartForwardAsync(request);
        return (response.Success, response.Error);
    }

    public async Task<(bool Success, string Error)> StopForwardAsync(string name)
    {
        var request = new StopForwardRequest { Name = name };
        var response = await _client.StopForwardAsync(request);
        return (response.Success, response.Error);
    }

    // Group management
    public async Task<GroupStatus[]> ListGroupsAsync()
    {
        var request = new ListGroupsRequest();
        var response = await _client.ListGroupsAsync(request);
        return response.Groups.ToArray();
    }

    public async Task<(bool Success, string Error)> EnableGroupAsync(string name)
    {
        var request = new EnableGroupRequest { Name = name };
        var response = await _client.EnableGroupAsync(request);
        return (response.Success, response.Error);
    }

    public async Task<(bool Success, string Error)> DisableGroupAsync(string name)
    {
        var request = new DisableGroupRequest { Name = name };
        var response = await _client.DisableGroupAsync(request);
        return (response.Success, response.Error);
    }

    // Configuration management
    public async Task<(bool Success, int Added, int Updated, int Removed, string Error)> ApplyConfigAsync(
        string configJson, string? targetGroup = null, bool removeMissing = false)
    {
        var request = new ApplyConfigRequest
        {
            ConfigJson = configJson,
            TargetGroup = targetGroup ?? "",
            RemoveMissing = removeMissing
        };
        
        var response = await _client.ApplyConfigAsync(request);
        return (response.Success, response.AddedCount, response.UpdatedCount, 
            response.RemovedCount, response.Error);
    }

    public async Task<string> ExportConfigAsync(bool includeDisabled = true, string? groupFilter = null)
    {
        var request = new ExportConfigRequest
        {
            IncludeDisabled = includeDisabled,
            GroupFilter = groupFilter ?? ""
        };
        
        var response = await _client.ExportConfigAsync(request);
        return response.ConfigJson;
    }

    public async Task<(bool Success, string Error)> ReloadConfigAsync()
    {
        var request = new ReloadConfigRequest();
        var response = await _client.ReloadConfigAsync(request);
        return (response.Success, response.Error);
    }

    // Daemon control
    public async Task<(bool Running, string Version, int ActiveForwardCount, 
        int TotalForwardCount, TimeSpan Uptime)> GetStatusAsync()
    {
        var request = new GetStatusRequest();
        var response = await _client.GetStatusAsync(request);
        
        return (response.Running, response.Version, response.ActiveForwardCount,
            response.TotalForwardCount, TimeSpan.FromSeconds(response.UptimeSeconds));
    }

    public async Task<bool> ShutdownAsync()
    {
        var request = new ShutdownRequest();
        var response = await _client.ShutdownAsync(request);
        return response.Success;
    }

    // Helper methods to convert between Core and gRPC types
    private Core.ForwardDefinition ConvertFromGrpcForward(Grpc.ForwardDefinition grpcForward)
    {
        Core.ForwardDefinition forward;
        
        switch (grpcForward.Type)
        {
            case ForwardType.Socket:
                forward = new SocketProxyDefinition
                {
                    Name = grpcForward.Name,
                    Group = grpcForward.Group,
                    LocalPort = grpcForward.LocalPort,
                    Enabled = grpcForward.Enabled,
                    RemoteHost = grpcForward.RemoteHost,
                    RemotePort = grpcForward.RemotePort
                };
                break;
                
            case ForwardType.Kubernetes:
                forward = new KubernetesForwardDefinition
                {
                    Name = grpcForward.Name,
                    Group = grpcForward.Group,
                    LocalPort = grpcForward.LocalPort,
                    Enabled = grpcForward.Enabled,
                    Context = grpcForward.Context,
                    Namespace = grpcForward.Namespace,
                    Service = grpcForward.Service,
                    ServicePort = grpcForward.ServicePort
                };
                break;
                
            default:
                throw new ArgumentOutOfRangeException();
        }

        return forward;
    }

    private Grpc.ForwardDefinition ConvertToGrpcForward(Core.ForwardDefinition forward)
    {
        var grpcForward = new Grpc.ForwardDefinition
        {
            Name = forward.Name,
            Group = forward.Group,
            LocalPort = forward.LocalPort,
            Enabled = forward.Enabled
        };
        
        switch (forward)
        {
            case SocketProxyDefinition socketForward:
                grpcForward.Type = ForwardType.Socket;
                grpcForward.RemoteHost = socketForward.RemoteHost;
                grpcForward.RemotePort = socketForward.RemotePort;
                break;
                
            case KubernetesForwardDefinition k8sForward:
                grpcForward.Type = ForwardType.Kubernetes;
                grpcForward.Context = k8sForward.Context;
                grpcForward.Namespace = k8sForward.Namespace;
                grpcForward.Service = k8sForward.Service;
                grpcForward.ServicePort = k8sForward.ServicePort;
                break;
        }
        
        return grpcForward;
    }
}
