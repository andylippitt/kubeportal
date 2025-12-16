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
    private readonly ILogger<KubernetesCache> _logger;
    private readonly TimeSpan _clientTtl = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _podCacheTtl = TimeSpan.FromSeconds(30);
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public KubernetesCache(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<KubernetesCache>();
        _cleanupTimer = new Timer(CleanupExpiredEntries, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Gets or creates a cached Kubernetes client for the specified context.
    /// </summary>
    public IKubernetes GetClient(string context)
    {
        var key = context;
        
        if (_clients.TryGetValue(key, out var cached) && !cached.IsExpired)
        {
            _logger.LogDebug("Using cached Kubernetes client for context '{Context}'", context);
            return cached.Client;
        }

        // Create new client
        _logger.LogDebug("Creating new Kubernetes client for context '{Context}'", context);
        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: context);
        var client = new Kubernetes(config);
        
        var newCached = new CachedClient(client, _clientTtl);
        _clients.AddOrUpdate(key, newCached, (_, old) =>
        {
            // Dispose old client if we're replacing it
            old.Client.Dispose();
            return newCached;
        });

        return client;
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
        
        var client = GetClient(context);
        
        var service = await client.CoreV1.ReadNamespacedServiceAsync(
            serviceName,
            ns,
            cancellationToken: token);

        var pods = await ResolveServiceToPodsAsync(client, ns, service, token);
        
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
        _logger.LogInformation("Pod cache invalidated");
    }

    /// <summary>
    /// Invalidates cached pods for a specific service.
    /// </summary>
    public void InvalidatePodCache(string context, string ns, string serviceName)
    {
        var cacheKey = $"{context}:{ns}:{serviceName}";
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

        // Filter to only running pods
        return podList.Items
            .Where(p => p.Status?.Phase == "Running")
            .ToList();
    }

    private void CleanupExpiredEntries(object? state)
    {
        // Clean up expired clients
        var expiredClientKeys = _clients
            .Where(kvp => kvp.Value.IsExpired)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredClientKeys)
        {
            if (_clients.TryRemove(key, out var removed))
            {
                removed.Client.Dispose();
                _logger.LogDebug("Removed expired Kubernetes client for context '{Context}'", key);
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
