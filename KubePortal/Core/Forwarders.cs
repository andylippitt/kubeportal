using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using k8s;
using k8s.Models;

namespace KubePortal.Core;

public interface IForwarder : IAsyncDisposable
{
    // Properties
    ForwardDefinition Definition { get; }
    bool IsActive { get; }
    DateTime? StartTime { get; }
    int ConnectionCount { get; }
    long BytesTransferred { get; }

    // Lifecycle
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public abstract class ForwarderBase : IForwarder
{
    protected readonly ForwardDefinition _definition;
    protected readonly ILogger _logger;
    protected readonly CancellationTokenSource _cts = new();
    protected TcpListener? _listener;
    protected bool _isActive;
    protected readonly ConcurrentDictionary<Guid, Task> _activeConnections = new();
    protected long _totalBytesTransferred = 0;
    protected DateTime? _startTime;

    protected ForwarderBase(ForwardDefinition definition, ILoggerFactory loggerFactory)
    {
        _definition = definition;
        _logger = loggerFactory.CreateLogger(GetType());
    }

    // IForwarder properties
    public ForwardDefinition Definition => _definition;
    public bool IsActive => _isActive;
    public DateTime? StartTime => _startTime;
    public int ConnectionCount => _activeConnections.Count;
    public long BytesTransferred => Interlocked.Read(ref _totalBytesTransferred);

    // Start implementation
    public virtual async Task StartAsync(CancellationToken cancellationToken)
    {
        // Skip if already running
        if (_isActive) return;

        try
        {
            cancellationToken.Register(() => _cts.Cancel());

            _listener = new TcpListener(IPAddress.Loopback, _definition.LocalPort);
            _listener.Start();
            _isActive = true;
            _startTime = DateTime.UtcNow;

            // Start accepting connections
            _ = Task.Run(ListenLoopAsync);

            _logger.LogInformation("Started forwarder '{Name}' on port {Port}",
                _definition.Name, _definition.LocalPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start forwarder");
            await StopAsync();
            throw;
        }
    }

    // Accept connections loop
    private async Task ListenLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(_cts.Token);
                var connectionId = Guid.NewGuid();

                // Process in background
                var connectionTask = ProcessClientConnectionAsync(client, connectionId, _cts.Token);
                _activeConnections[connectionId] = connectionTask;

                // Clean up completed connections
                CleanupCompletedConnections();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in listener loop");
        }
    }

    // Abstract method for connection handling
    protected abstract Task ProcessClientConnectionAsync(
        TcpClient client, Guid connectionId, CancellationToken token);

    // Stop implementation
    public virtual async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isActive) return;

        _cts.Cancel();
        _listener?.Stop();
        _isActive = false;

        // Wait for active connections (with timeout)
        await WaitForConnectionsAsync(TimeSpan.FromSeconds(5));

        _logger.LogInformation("Stopped forwarder '{Name}'", _definition.Name);
    }

    // Helper to wait for connections
    private async Task WaitForConnectionsAsync(TimeSpan timeout)
    {
        var tasks = _activeConnections.Values.ToArray();
        var timeoutTask = Task.Delay(timeout);

        await Task.WhenAny(Task.WhenAll(tasks), timeoutTask);
    }

    // Clean up completed connections
    private void CleanupCompletedConnections()
    {
        foreach (var kvp in _activeConnections.Where(x => x.Value.IsCompleted))
            _activeConnections.TryRemove(kvp.Key, out _);
    }

    // Helper method to update byte counter
    protected void UpdateBytesTransferred(long bytes)
    {
        Interlocked.Add(ref _totalBytesTransferred, bytes);
    }

    // Cleanup
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}

public class KubernetesForwarder : ForwarderBase
{
    private readonly KubernetesForwardDefinition _k8sDefinition;

    public KubernetesForwarder(KubernetesForwardDefinition definition, ILoggerFactory loggerFactory)
        : base(definition, loggerFactory)
    {
        _k8sDefinition = definition;
    }

    protected override async Task ProcessClientConnectionAsync(
        TcpClient client, Guid connectionId, CancellationToken token)
    {
        _logger.LogDebug("Processing connection {ConnectionId} for {Name}",
            connectionId, _definition.Name);

        try
        {
            using var clientStream = client.GetStream();

            // Create Kubernetes client
            var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(
                currentContext: _k8sDefinition.Context);
            using var k8sClient = new Kubernetes(config);

            // Get service and resolve to pods
            var service = await k8sClient.CoreV1.ReadNamespacedServiceAsync(
                _k8sDefinition.Service,
                _k8sDefinition.Namespace,
                cancellationToken: token);

            var pods = await ResolveServiceToPodsAsync(k8sClient, _k8sDefinition.Namespace, service, token);

            if (pods.Count == 0)
            {
                _logger.LogError("No pods found for service {Service} in namespace {Namespace}",
                    _k8sDefinition.Service, _k8sDefinition.Namespace);
                return;
            }

            // Use first pod for the port forward
            var pod = pods[0];

            _logger.LogDebug("Forwarding connection to pod {Pod}:{Port}",
                pod.Metadata.Name, _k8sDefinition.ServicePort);

            using var webSocket = await k8sClient.WebSocketNamespacedPodPortForwardAsync(
                pod.Metadata.Name,
                _k8sDefinition.Namespace,
                new int[] { _k8sDefinition.ServicePort },
                "v4.channel.k8s.io",
                cancellationToken: token);

            using var demux = new StreamDemuxer(webSocket, StreamType.PortForward);
            demux.Start();

            using var serverStream = demux.GetStream((byte?)0, (byte?)0);

            // Set up bidirectional copy
            var serverToClient = CopyStreamAsync(serverStream, clientStream, token);
            var clientToServer = CopyStreamAsync(clientStream, serverStream, token);

            // Wait until either direction completes or gets canceled
            await Task.WhenAny(serverToClient, clientToServer);

            _logger.LogDebug("Connection {ConnectionId} completed", connectionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Connection {ConnectionId} cancelled", connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing connection {ConnectionId}", connectionId);
        }
        finally
        {
            client.Dispose();
            _activeConnections.TryRemove(connectionId, out _);
        }
    }

    private async Task<IList<V1Pod>> ResolveServiceToPodsAsync(
        IKubernetes client, string ns, V1Service service, CancellationToken token)
    {
        var labelSelector = string.Join(",",
            service.Spec.Selector.Select(kv => $"{kv.Key}={kv.Value}"));

        var podList = await client.CoreV1.ListNamespacedPodAsync(
            ns,
            labelSelector: labelSelector,
            cancellationToken: token);

        return podList.Items;
    }

    private Task CopyStreamAsync(Stream source, Stream destination, CancellationToken token)
    {
        return Task.Run(() =>
        {
            using var registration = token.Register(() =>
            {
                try
                {
                    // Force stream closure on cancellation
                    source.Close();
                }
                catch { /* Ignore */ }
            });

            try
            {
                // Register for bytes read
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    destination.Write(buffer, 0, bytesRead);
                    UpdateBytesTransferred(bytesRead);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    _logger.LogError(ex, "Error copying stream");
            }
        }, token);
    }
}

public class SocketProxyForwarder : ForwarderBase
{
    private readonly SocketProxyDefinition _socketDefinition;

    public SocketProxyForwarder(SocketProxyDefinition definition, ILoggerFactory loggerFactory)
        : base(definition, loggerFactory)
    {
        _socketDefinition = definition;
    }

    protected override async Task ProcessClientConnectionAsync(
        TcpClient client, Guid connectionId, CancellationToken token)
    {
        _logger.LogDebug("Processing connection {ConnectionId} for {Name}",
            connectionId, _definition.Name);

        TcpClient? remoteClient = null;

        try
        {
            // Connect to remote endpoint
            remoteClient = new TcpClient();
            await remoteClient.ConnectAsync(
                _socketDefinition.RemoteHost,
                _socketDefinition.RemotePort,
                token);

            _logger.LogDebug("Connected to remote endpoint {Host}:{Port}",
                _socketDefinition.RemoteHost, _socketDefinition.RemotePort);

            using var clientStream = client.GetStream();
            using var remoteStream = remoteClient.GetStream();

            // Set up bidirectional copy
            var remoteToClient = CopyStreamAsync(remoteStream, clientStream, token);
            var clientToRemote = CopyStreamAsync(clientStream, remoteStream, token);

            // Wait until either direction completes or gets canceled
            await Task.WhenAny(remoteToClient, clientToRemote);

            _logger.LogDebug("Connection {ConnectionId} completed", connectionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Connection {ConnectionId} cancelled", connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing connection {ConnectionId}", connectionId);
        }
        finally
        {
            remoteClient?.Dispose();
            client.Dispose();
            _activeConnections.TryRemove(connectionId, out _);
        }
    }

    private async Task CopyStreamAsync(
        Stream source, Stream destination, CancellationToken token)
    {
        const int bufferSize = 81920; // 80KB buffer
        var buffer = new byte[bufferSize];

        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
            {
                await destination.WriteAsync(buffer, 0, read, token);

                // Update bytes counter for EACH chunk, not at the end
                UpdateBytesTransferred(read);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during cancellation
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error copying stream");
        }
    }
}
