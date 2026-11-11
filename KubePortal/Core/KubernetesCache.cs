using System.Collections.Concurrent;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace KubePortal.Core;

/// <summary>
/// Caches Kubernetes clients and pod resolution to avoid multi-second delays 
/// when creating new connections rapidly.
/// </summary>
public class KubernetesCache : IDisposable
{
    private readonly ConcurrentDictionary<string, CachedClient> _clients = new();
    private readonly ConcurrentDictionary<string, CachedPodList> _podCache = new();
    private readonly ConcurrentDictionary<string, CachedService> _serviceCache = new();
    private readonly ILogger<KubernetesCache> _logger;
    private readonly TimeSpan _clientTtl;
    private readonly TimeSpan _retiredClientGrace;
    private readonly TimeSpan _podCacheTtl = TimeSpan.FromSeconds(30);
    private readonly Func<string, IKubernetes> _clientFactory;
    private readonly object _clientLock = new();
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public KubernetesCache(ILoggerFactory loggerFactory)
        : this(loggerFactory,
               context => new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: context)),
               clientTtl: TimeSpan.FromMinutes(10),
               retiredClientGrace: TimeSpan.FromMinutes(2))
    {
    }

    /// <summary>
    /// Test seam: the client factory and lifetimes are injectable so the replacement
    /// path can be exercised without a cluster or a ten-minute wait.
    /// </summary>
    public KubernetesCache(ILoggerFactory loggerFactory, Func<string, IKubernetes> clientFactory,
        TimeSpan clientTtl, TimeSpan retiredClientGrace)
    {
        _logger = loggerFactory.CreateLogger<KubernetesCache>();
        _clientFactory = clientFactory;
        _clientTtl = clientTtl;
        _retiredClientGrace = retiredClientGrace;
        _cleanupTimer = new Timer(CleanupExpiredEntries, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Gets or creates a cached Kubernetes client for the specified context.
    ///
    /// A client handed out here may be mid-request on another connection, so it is never
    /// disposed on the spot. The old code let every caller that missed the cache build its
    /// own client and dispose the one it replaced: when the TTL lapsed under a burst of
    /// connections (a Grafana dashboard opening, say), callers disposed each other's
    /// clients and in-flight service/pod lookups died with TaskCanceledException, which
    /// the forwarder logs only at Debug, so the client just saw an empty reply. Now one
    /// caller per context builds the client, and the one it replaces is retired and
    /// disposed after a grace period longer than any connection setup.
    /// </summary>
    public IKubernetes GetClient(string context)
    {
        if (_clients.TryGetValue(context, out var cached) && !cached.IsExpired)
            return cached.Client;

        lock (_clientLock)
        {
            // Another caller may have refreshed it while we waited for the lock.
            if (_clients.TryGetValue(context, out cached) && !cached.IsExpired)
                return cached.Client;

            _logger.LogDebug("Creating new Kubernetes client for context '{Context}'", context);
            var fresh = new CachedClient(_clientFactory(context), _clientTtl);
            _clients[context] = fresh;
            if (cached != null)
                RetireClient(cached.Client, context);
            return fresh.Client;
        }
    }

    private void RetireClient(IKubernetes client, string context)
    {
        _ = Task.Delay(_retiredClientGrace).ContinueWith(_ =>
        {
            try { client.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Disposing retired client for '{Context}'", context); }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Resolves a service + service port to a concrete pod and the container port that
    /// actually accepts traffic. Mirrors what `kubectl port-forward svc/...` does: the
    /// service's targetPort (numeric or named) is translated against the pod's container
    /// ports. Forwarding the *service* port straight at the pod silently fails whenever
    /// port != targetPort.
    /// </summary>
    public async Task<ResolvedEndpoint?> ResolveEndpointAsync(
        string context,
        string ns,
        string serviceName,
        int servicePort,
        CancellationToken token)
    {
        var svc = await GetServiceAsync(context, ns, serviceName, token);

        var pods = await GetPodsForServiceAsync(context, ns, serviceName, token);
        if (pods.Count == 0)
        {
            _logger.LogError("No ready/running pods found for service '{Service}' in namespace '{Namespace}'",
                serviceName, ns);
            return null;
        }

        var pod = pods[0];

        var targetPort = TranslateServicePort(svc, servicePort, pod, serviceName, _logger);
        if (targetPort is null)
            return null;

        return new ResolvedEndpoint(pod, targetPort.Value);
    }

    /// <summary>
    /// Translates a service port to the pod container port, following the same rules as kubectl:
    /// missing targetPort == the service port, numeric targetPort used as-is, named targetPort
    /// looked up in the pod's container ports.
    /// </summary>
    public static int? TranslateServicePort(V1Service svc, int servicePort, V1Pod pod, string serviceName, ILogger? logger = null)
    {
        var portSpec = svc.Spec?.Ports?.FirstOrDefault(p => p.Port == servicePort);
        if (portSpec == null)
        {
            logger?.LogError(
                "Service '{Service}' does not expose port {Port} (exposes: {Exposed}); forwarding to pod port {Port} as-is",
                serviceName, servicePort,
                string.Join(", ", svc.Spec?.Ports?.Select(p => p.Port.ToString()) ?? Enumerable.Empty<string>()),
                servicePort);
            return servicePort;
        }

        var target = portSpec.TargetPort;

        // IntstrIntOrString carries either a number or a port name as a string.
        var name = target?.Value;

        // No targetPort set -> defaults to the service port.
        if (string.IsNullOrEmpty(name))
            return servicePort;

        if (int.TryParse(name, out var numeric))
            return numeric;

        // Named port: resolve against the pod's container ports.
        var containerPort = pod.Spec?.Containers
            ?.SelectMany(c => c.Ports ?? new List<V1ContainerPort>())
            .FirstOrDefault(cp => string.Equals(cp.Name, name, StringComparison.Ordinal));

        if (containerPort == null)
        {
            logger?.LogError(
                "Service '{Service}' port {Port} targets named port '{Name}', but pod '{Pod}' declares no such container port",
                serviceName, servicePort, name, pod.Metadata?.Name);
            return null;
        }

        logger?.LogDebug("Resolved service '{Service}' port {Port} -> named targetPort '{Name}' -> container port {Container}",
            serviceName, servicePort, name, containerPort.ContainerPort);

        return containerPort.ContainerPort;
    }

    /// <summary>
    /// Gets the service definition, using cache when available.
    /// </summary>
    private async Task<V1Service> GetServiceAsync(
        string context, string ns, string serviceName, CancellationToken token)
    {
        var cacheKey = $"{context}:{ns}:{serviceName}";

        if (_serviceCache.TryGetValue(cacheKey, out var cachedSvc) && !cachedSvc.IsExpired)
            return cachedSvc.Service;

        var client = GetClient(context);
        var svc = await client.CoreV1.ReadNamespacedServiceAsync(serviceName, ns, cancellationToken: token);
        _serviceCache[cacheKey] = new CachedService(svc, _podCacheTtl);
        return svc;
    }

    /// <summary>
    /// Gets pods for a service, using cache when available.
    /// </summary>
    public async Task<IList<V1Pod>> GetPodsForServiceAsync(
        string context,
        string ns,
        string serviceName,
        CancellationToken token)
    {
        var cacheKey = $"{context}:{ns}:{serviceName}";

        if (_podCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
        {
            _logger.LogDebug("Using cached pod list for service '{Service}' ({Count} pods)",
                serviceName, cached.Pods.Count);
            return cached.Pods;
        }

        // Fetch fresh data
        _logger.LogDebug("Fetching pods for service '{Service}' in namespace '{Namespace}'",
            serviceName, ns);

        var service = await GetServiceAsync(context, ns, serviceName, token);

        var pods = await ResolveServiceToPodsAsync(GetClient(context), ns, service, token);

        // Cache the result
        _podCache[cacheKey] = new CachedPodList(pods, _podCacheTtl);

        _logger.LogDebug("Cached {Count} pods for service '{Service}'", pods.Count, serviceName);

        return pods;
    }

    /// <summary>
    /// Invalidates all cached pods (e.g., when a pod restart is detected).
    /// </summary>
    public void InvalidatePodCache()
    {
        _podCache.Clear();
        _serviceCache.Clear();
        _logger.LogInformation("Pod cache invalidated");
    }

    /// <summary>
    /// Invalidates cached pods for a specific service.
    /// </summary>
    public void InvalidatePodCache(string context, string ns, string serviceName)
    {
        var cacheKey = $"{context}:{ns}:{serviceName}";
        _serviceCache.TryRemove(cacheKey, out _);
        if (_podCache.TryRemove(cacheKey, out _))
        {
            _logger.LogDebug("Invalidated pod cache for service '{Service}'", serviceName);
        }
    }

    private async Task<IList<V1Pod>> ResolveServiceToPodsAsync(
        IKubernetes client, string ns, V1Service service, CancellationToken token)
    {
        if (service.Spec.Selector == null || service.Spec.Selector.Count == 0)
        {
            return Array.Empty<V1Pod>();
        }

        var labelSelector = string.Join(",",
            service.Spec.Selector.Select(kv => $"{kv.Key}={kv.Value}"));

        var podList = await client.CoreV1.ListNamespacedPodAsync(
            ns,
            labelSelector: labelSelector,
            cancellationToken: token);

        var running = podList.Items
            .Where(p => p.Status?.Phase == "Running" && p.Metadata?.DeletionTimestamp == null)
            .ToList();

        // Prefer pods that pass their readiness probe; a Running-but-unready pod
        // (still starting, or failing health checks) refuses connections.
        var ready = running.Where(IsReady).ToList();
        return ready.Count > 0 ? ready : running;
    }

    private static bool IsReady(V1Pod pod) =>
        pod.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true;

    private void CleanupExpiredEntries(object? state)
    {
        // Clean up expired clients. Retired, not disposed: a connection may have fetched
        // this client just before it expired and still be using it.
        var expiredClientKeys = new List<string>();
        lock (_clientLock)
        {
            foreach (var kvp in _clients.Where(kvp => kvp.Value.IsExpired).ToList())
            {
                if (_clients.TryRemove(new KeyValuePair<string, CachedClient>(kvp.Key, kvp.Value)))
                {
                    expiredClientKeys.Add(kvp.Key);
                    RetireClient(kvp.Value.Client, kvp.Key);
                }
            }
        }

        // Clean up expired pod cache entries
        var expiredPodKeys = _podCache
            .Where(kvp => kvp.Value.IsExpired)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredPodKeys)
        {
            _podCache.TryRemove(key, out _);
        }

        foreach (var key in _serviceCache.Where(kvp => kvp.Value.IsExpired).Select(kvp => kvp.Key).ToList())
        {
            _serviceCache.TryRemove(key, out _);
        }

        if (expiredClientKeys.Count > 0 || expiredPodKeys.Count > 0)
        {
            _logger.LogDebug("Cache cleanup: removed {Clients} clients, {Pods} pod entries",
                expiredClientKeys.Count, expiredPodKeys.Count);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer.Dispose();

        foreach (var cached in _clients.Values)
        {
            cached.Client.Dispose();
        }
        _clients.Clear();
        _podCache.Clear();
        _serviceCache.Clear();
    }

    private class CachedClient
    {
        public IKubernetes Client { get; }
        public DateTime ExpiresAt { get; }
        public bool IsExpired => DateTime.UtcNow > ExpiresAt;

        public CachedClient(IKubernetes client, TimeSpan ttl)
        {
            Client = client;
            ExpiresAt = DateTime.UtcNow + ttl;
        }
    }

    private class CachedService
    {
        public V1Service Service { get; }
        public DateTime ExpiresAt { get; }
        public bool IsExpired => DateTime.UtcNow > ExpiresAt;

        public CachedService(V1Service service, TimeSpan ttl)
        {
            Service = service;
            ExpiresAt = DateTime.UtcNow + ttl;
        }
    }

    private class CachedPodList
    {
        public IList<V1Pod> Pods { get; }
        public DateTime ExpiresAt { get; }
        public bool IsExpired => DateTime.UtcNow > ExpiresAt;

        public CachedPodList(IList<V1Pod> pods, TimeSpan ttl)
        {
            Pods = pods;
            ExpiresAt = DateTime.UtcNow + ttl;
        }
    }
}

/// <summary>
/// A service resolved down to the pod and container port that will actually accept traffic.
/// </summary>
public record ResolvedEndpoint(V1Pod Pod, int TargetPort);

/// <summary>
/// Singleton provider for the Kubernetes cache.
/// </summary>
public static class KubernetesCacheProvider
{
    private static KubernetesCache? _instance;
    private static readonly object _lock = new();

    public static KubernetesCache GetInstance(ILoggerFactory loggerFactory)
    {
        if (_instance != null) return _instance;

        lock (_lock)
        {
            _instance ??= new KubernetesCache(loggerFactory);
        }

        return _instance;
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _instance?.Dispose();
            _instance = null;
        }
    }
}
