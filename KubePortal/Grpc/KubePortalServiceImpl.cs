using Grpc.Core;
using System.Text.Json.Nodes;

namespace KubePortal.Grpc;

// Implementation of the gRPC service
public class KubePortalServiceImpl : KubePortalService.KubePortalServiceBase
{
    private readonly ForwardManager _manager;
    private readonly ILogger<KubePortalServiceImpl> _logger;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly string _version = "1.0.0"; // Should be dynamically retrieved

    public KubePortalServiceImpl(ForwardManager manager, ILogger<KubePortalServiceImpl> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    #region Forward Management

    public override async Task<CreateForwardResponse> CreateForward(
        CreateForwardRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogInformation("Creating forward '{Name}'", request.Forward.Name);
            
            var forward = ConvertFromGrpcForward(request.Forward);
            if (forward == null)
            {
                return new CreateForwardResponse
                {
                    Success = false,
                    Error = "Invalid forward definition"
                };
            }

            var result = await _manager.AddOrUpdateForwardAsync(forward);
            return new CreateForwardResponse
            {
                Success = result,
                Error = !result ? "Failed to create forward. Check daemon logs for details." : string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating forward");
            return new CreateForwardResponse
            {
                Success = false,
                Error = $"Exception: {ex.Message}"
            };
        }
    }

    public override async Task<DeleteForwardResponse> DeleteForward(
        DeleteForwardRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogInformation("Deleting forward '{Name}'", request.Name);
            
            var result = await _manager.DeleteForwardAsync(request.Name);
            return new DeleteForwardResponse
            {
                Success = result,
                Error = !result ? "Forward not found or could not be deleted" : string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting forward");
            return new DeleteForwardResponse
            {
                Success = false,
                Error = $"Exception: {ex.Message}"
            };
        }
    }

    public override async Task<ListForwardsResponse> ListForwards(
        ListForwardsRequest request, ServerCallContext context)
    {
        try
        {
            var forwards = await _manager.GetAllForwardsAsync();
            var activeForwarders = _manager.GetActiveForwarders();
            
            // Apply group filter if specified
            if (!string.IsNullOrEmpty(request.GroupFilter))
            {
                forwards = forwards.Where(f => f.Group == request.GroupFilter).ToList();
            }

            var response = new ListForwardsResponse();
            
            foreach (var forward in forwards)
            {
                response.Forwards.Add(ConvertToGrpcForward(forward));
                
                // Add status if the forward is active
                if (activeForwarders.TryGetValue(forward.Name, out var forwarder))
                {
                    response.Statuses.Add(new ForwardStatus
                    {
                        Name = forward.Name,
                        Active = forwarder.IsActive,
                        BytesTransferred = forwarder.BytesTransferred,
                        ConnectionCount = forwarder.ConnectionCount,
                        StartTime = forwarder.StartTime?.ToString("o") ?? string.Empty
                    });
                }
                else
                {
                    response.Statuses.Add(new ForwardStatus
                    {
                        Name = forward.Name,
                        Active = false,
                        BytesTransferred = 0,
                        ConnectionCount = 0
                    });
                }
            }
            
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing forwards");
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<GetForwardResponse> GetForward(
        GetForwardRequest request, ServerCallContext context)
    {
        try
        {
            var forward = await _manager.GetForwardByNameAsync(request.Name);
            if (forward == null)
            {
                return new GetForwardResponse { Found = false };
            }

            var response = new GetForwardResponse
            {
                Found = true,
                Forward = ConvertToGrpcForward(forward),
                Status = new ForwardStatus { Name = forward.Name, Active = false }
            };
            
            // Add active status if available
            var activeForwarders = _manager.GetActiveForwarders();
            if (activeForwarders.TryGetValue(forward.Name, out var forwarder))
            {
                response.Status.Active = forwarder.IsActive;
                response.Status.BytesTransferred = forwarder.BytesTransferred;
                response.Status.ConnectionCount = forwarder.ConnectionCount;
                response.Status.StartTime = forwarder.StartTime?.ToString("o") ?? string.Empty;
            }
            
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting forward '{Name}'", request.Name);
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<StartForwardResponse> StartForward(
        StartForwardRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogInformation("Starting forward '{Name}'", request.Name);
            
            var result = await _manager.StartForwardAsync(request.Name);
            return new StartForwardResponse
            {
                Success = result,
                Error = !result ? "Forward not found or could not be started" : string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting forward '{Name}'", request.Name);
            return new StartForwardResponse
            {
                Success = false,
                Error = $"Exception: {ex.Message}"
            };
        }
    }

    public override async Task<StopForwardResponse> StopForward(
        StopForwardRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogInformation("Stopping forward '{Name}'", request.Name);
            
            var result = await _manager.StopForwardAsync(request.Name);
            return new StopForwardResponse
            {
                Success = result,
                Error = !result ? "Forward not found or could not be stopped" : string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping forward '{Name}'", request.Name);
            return new StopForwardResponse
            {
                Success = false,
                Error = $"Exception: {ex.Message}"
            };
        }
    }
    
    #endregion

    #region Group Management

    public override async Task<ListGroupsResponse> ListGroups(
        ListGroupsRequest request, ServerCallContext context)
    {
        try
        {
            var groupStatuses = await _manager.GetGroupStatusesAsync();
            var forwards = await _manager.GetAllForwardsAsync();
            var activeForwarders = _manager.GetActiveForwarders();
            
            var response = new ListGroupsResponse();
            
            foreach (var group in groupStatuses)
            {
                var groupForwards = forwards.Where(f => f.Group == group.Key).ToList();
                var activeCount = groupForwards.Count(f => activeForwarders.ContainsKey(f.Name));
                
                response.Groups.Add(new GroupStatus
                {
                    Name = group.Key,
                    Enabled = group.Value,
                    ForwardCount = groupForwards.Count,
                    ActiveForwardCount = activeCount
                });
            }
            
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing groups");
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<EnableGroupResponse> EnableGroup(
        EnableGroupRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogInformation("Enabling group '{Name}'", request.Name);
            
            var result = await _manager.EnableGroupAsync(request.Name);
            return new EnableGroupResponse
            {
                Success = result,
                Error = !result ? "Group not found or could not be enabled" : string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enabling group '{Name}'", request.Name);
            return new EnableGroupResponse
            {
                Success = false,
                Error = $"Exception: {ex.Message}"
            };
        }
    }

    public override async Task<DisableGroupResponse> DisableGroup(
        DisableGroupRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogInformation("Disabling group '{Name}'", request.Name);
            
            var result = await _manager.DisableGroupAsync(request.Name);
            return new DisableGroupResponse
            {
                Success = result,
                Error = !result ? "Group not found or could not be disabled" : string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disabling group '{Name}'", request.Name);
            return new DisableGroupResponse
            {
                Success = false,
                Error = $"Exception: {ex.Message}"
            };
        }
    }
    
    #endregion

    #region Configuration Management

    public override async Task<ApplyConfigResponse> ApplyConfig(
        ApplyConfigRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogInformation("Applying configuration" + 
                (!string.IsNullOrEmpty(request.TargetGroup) 
                    ? $" for group '{request.TargetGroup}'" 
                    : string.Empty));
            
            var response = new ApplyConfigResponse();
            
            var result = await MergeConfigAsync(
                request.ConfigJson, 
                request.TargetGroup, 
                request.RemoveMissing);
            
            response.Success = true;
            response.AddedCount = result.added;
            response.UpdatedCount = result.updated;
            response.RemovedCount = result.removed;
            
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying configuration");
            return new ApplyConfigResponse
            {
                Success = false,
                Error = $"Exception: {ex.Message}"
            };
        }
    }

    public override async Task<ExportConfigResponse> ExportConfig(
        ExportConfigRequest request, ServerCallContext context)
    {
        try
        {
            var forwards = await _manager.GetAllForwardsAsync();
            
            // Apply filters
            if (!request.IncludeDisabled)
            {
                forwards = forwards.Where(f => f.Enabled).ToList();
            }
            
            if (!string.IsNullOrEmpty(request.GroupFilter))
            {
                forwards = forwards.Where(f => f.Group == request.GroupFilter).ToList();
            }
            
            // Build the JSON response
            var forwardsJson = new JsonObject();
            
            foreach (var forward in forwards)
            {
                forwardsJson[forward.Name] = forward.ToJson();
            }
            
            var config = new JsonObject
            {
                ["forwards"] = forwardsJson
            };
            
            return new ExportConfigResponse
            {
                ConfigJson = config.ToJsonString()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting configuration");
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<ReloadConfigResponse> ReloadConfig(
        ReloadConfigRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogInformation("Reloading configuration from disk");
            
            // Reinitialize the ForwardManager
            await _manager.InitializeAsync();
            
            return new ReloadConfigResponse
            {
                Success = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reloading configuration");
            return new ReloadConfigResponse
            {
                Success = false,
                Error = $"Exception: {ex.Message}"
            };
        }
    }
    
    #endregion

    #region Daemon Management

    public override Task<GetStatusResponse> GetStatus(
        GetStatusRequest request, ServerCallContext context)
    {
        try
        {
            var activeForwarders = _manager.GetActiveForwarders();
            var uptime = DateTime.UtcNow - _startTime;
            
            return Task.FromResult(new GetStatusResponse
            {
                Running = true,
                Version = _version,
                ActiveForwardCount = activeForwarders.Count,
                TotalForwardCount = _manager.GetAllForwardsAsync().Result.Count,
                UptimeSeconds = (long)uptime.TotalSeconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting daemon status");
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override Task<ShutdownResponse> Shutdown(
        ShutdownRequest request, ServerCallContext context)
    {
        _logger.LogInformation("Shutdown requested via gRPC");
        
        // Signal shutdown (handled by the host)
        DaemonProcess.SignalShutdown();
        
        return Task.FromResult(new ShutdownResponse
        {
            Success = true
        });
    }
    
    #endregion

    #region Helper Methods

    private Core.ForwardDefinition? ConvertFromGrpcForward(Grpc.ForwardDefinition grpcForward)
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
                _logger.LogWarning("Unknown forward type: {Type}", grpcForward.Type);
                return null;
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

    // Merge configuration logic
    private async Task<(int added, int updated, int removed)> MergeConfigAsync(
        string jsonContent, string? targetGroup = null, bool removeMissing = false)
    {
        var added = 0;
        var updated = 0;
        var removed = 0;
        
        try
        {
            var jsonDoc = JsonNode.Parse(jsonContent);
            if (jsonDoc?["forwards"] is JsonObject forwards)
            {
                // Get existing forwards in the target group for comparison
                var existingForwards = await _manager.GetAllForwardsAsync();
                var existingInGroup = string.IsNullOrEmpty(targetGroup)
                    ? existingForwards
                    : existingForwards.Where(f => f.Group == targetGroup).ToList();
                
                // Track which forwards we've updated
                var processedNames = new HashSet<string>();
                
                // Process forwards from the JSON
                foreach (var kvp in forwards.AsObject())
                {
                    var forwardJson = kvp.Value as JsonObject;
                    if (forwardJson != null)
                    {
                        // Set the name property if missing
                        if (forwardJson["name"] == null)
                        {
                            forwardJson["name"] = kvp.Key;
                        }
                        
                        // Set group for forwards if targetGroup is specified
                        if (!string.IsNullOrEmpty(targetGroup) && (forwardJson["group"] == null))
                        {
                            forwardJson["group"] = targetGroup;
                        }
                        
                        // Check if this forward belongs to the target group
                        var group = forwardJson["group"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(targetGroup) || group == targetGroup)
                        {
                            // Process this forward
                            try
                            {
                                var forward = Core.ForwardDefinition.FromJson(forwardJson);
                                processedNames.Add(forward.Name);
                                
                                // Check if it's an update or a new forward
                                bool isUpdate = existingInGroup.Any(f => f.Name == forward.Name);
                                
                                // Add or update the forward
                                await _manager.AddOrUpdateForwardAsync(forward);
                                
                                if (isUpdate)
                                    updated++;
                                else
                                    added++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error processing forward '{Key}'", kvp.Key);
                            }
                        }
                    }
                }
                
                // Remove forwards that weren't in the JSON, if requested
                if (removeMissing)
                {
                    foreach (var existingForward in existingInGroup)
                    {
                        if (!processedNames.Contains(existingForward.Name))
                        {
                            await _manager.DeleteForwardAsync(existingForward.Name);
                            removed++;
                        }
                    }
                }
            }
            
            return (added, updated, removed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error merging configuration");
            throw;
        }
    }
    
    #endregion
}
