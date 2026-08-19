using System.Collections.Concurrent;
using System.Diagnostics;
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
                client.NoDelay = true; // Disable Nagle's algorithm to reduce latency
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
    private readonly KubernetesCache _cache;
    private readonly ILoggerFactory _loggerFactory;

    public KubernetesForwarder(KubernetesForwardDefinition definition, ILoggerFactory loggerFactory)
        : base(definition, loggerFactory)
    {
        _k8sDefinition = definition;
        _loggerFactory = loggerFactory;
        _cache = KubernetesCacheProvider.GetInstance(loggerFactory);
    }

    protected override async Task ProcessClientConnectionAsync(
        TcpClient client, Guid connectionId, CancellationToken token)
    {
        var totalSw = Stopwatch.StartNew();
        _logger.LogDebug("Processing connection {ConnectionId} for {Name}",
            connectionId, _definition.Name);

        try
        {
            using var clientStream = client.GetStream();

            // Get cached Kubernetes client and pods
            var sw = Stopwatch.StartNew();
            var k8sClient = _cache.GetClient(_k8sDefinition.Context);
            sw.Stop();
            _logger.LogDebug("[Timing] GetClient: {Elapsed}ms", sw.ElapsedMilliseconds);

            sw.Restart();
            var endpoint = await _cache.ResolveEndpointAsync(
                _k8sDefinition.Context,
                _k8sDefinition.Namespace,
                _k8sDefinition.Service,
                _k8sDefinition.ServicePort,
                token);
            sw.Stop();
            _logger.LogDebug("[Timing] ResolveEndpoint: {Elapsed}ms", sw.ElapsedMilliseconds);

            if (endpoint is null)
            {
                // ResolveEndpointAsync already logged the specific reason.
                return;
            }

            var pod = endpoint.Pod;

            _logger.LogDebug("Forwarding connection to pod {Pod}:{Port} (service port {ServicePort})",
                pod.Metadata.Name, endpoint.TargetPort, _k8sDefinition.ServicePort);

            sw.Restart();
            using var webSocket = await k8sClient.WebSocketNamespacedPodPortForwardAsync(
                pod.Metadata.Name,
                _k8sDefinition.Namespace,
                new int[] { endpoint.TargetPort },
                "v4.channel.k8s.io",
                cancellationToken: token);
            sw.Stop();
            _logger.LogDebug("[Timing] WebSocket port-forward setup: {Elapsed}ms", sw.ElapsedMilliseconds);

            using var demux = new StreamDemuxer(webSocket, StreamType.PortForward);
            demux.Start();

            using var serverStream = demux.GetStream((byte?)0, (byte?)0);

            // Channel 1 carries kubelet's port-forward errors (e.g. "connection refused").
            // Without draining it the client just sees an empty reply and nothing is logged.
            var errorStream = demux.GetStream((byte?)1, (byte?)1);
            _ = RunOnDedicatedThread($"pf-err-{_definition.Name}",
                () => DrainErrorStream(errorStream, pod.Metadata.Name, endpoint.TargetPort));

            _logger.LogDebug("[Timing] Total setup before data relay: {Elapsed}ms", totalSw.ElapsedMilliseconds);

            // Set up bidirectional copy; StreamDemuxer requires blocking I/O inside Task.Run
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);

            var serverToClient = CopyStreamAsync(serverStream, clientStream, connectionCts.Token);
            var clientToServer = CopyStreamAsync(clientStream, serverStream, connectionCts.Token);

            await Task.WhenAny(serverToClient, clientToServer);
            connectionCts.Cancel();

            // Brief drain for the other direction to finish cleanly
            try { await Task.WhenAll(serverToClient, clientToServer).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* drain best-effort */ }

            totalSw.Stop();
            _logger.LogDebug("[Timing] Connection {ConnectionId} total: {Elapsed}ms",
                connectionId, totalSw.ElapsedMilliseconds);
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

    private void DrainErrorStream(Stream errorStream, string podName, int targetPort)
    {
        try
        {
            var buffer = new byte[4096];
            var message = new System.Text.StringBuilder();
            int read;
            while ((read = errorStream.Read(buffer, 0, buffer.Length)) > 0)
                message.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));

            var text = message.ToString().Trim();
            if (text.Length > 0)
                _logger.LogError("Port-forward error from {Pod}:{Port} ({Name}): {Message}",
                    podName, targetPort, _definition.Name, text);
        }
        catch
        {
            // Stream torn down with the connection - nothing useful to report.
        }
        finally
        {
            try { errorStream.Dispose(); } catch { /* best effort */ }
        }
    }

    private Task CopyStreamAsync(Stream source, Stream destination, CancellationToken token)
    {
        // StreamDemuxer only exposes blocking I/O, and these loops stay blocked for the
        // whole life of the connection. Running them on the thread pool starves it: the
        // pool only injects new threads once or twice a second, so a burst of parallel
        // requests (a Grafana dashboard, say) stalls for 10-15s until enough threads
        // exist. Dedicated threads are created immediately, so bursts stay fast.
        return RunOnDedicatedThread($"copy-{_definition.Name}", () =>
        {
            using var registration = token.Register(() =>
            {
                try { source.Close(); }
                catch { /* Ignore */ }
            });

            try
            {
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    destination.Write(buffer, 0, bytesRead);
                    UpdateBytesTransferred(bytesRead);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    _logger.LogError(ex, "Error copying stream");
            }
        });
    }

    private static Task RunOnDedicatedThread(string name, Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
        return tcs.Task;
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
            // Connect to remote endpoint with timeout
            remoteClient = new TcpClient { NoDelay = true };
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            await remoteClient.ConnectAsync(
                _socketDefinition.RemoteHost,
                _socketDefinition.RemotePort,
                connectCts.Token);

            _logger.LogDebug("Connected to remote endpoint {Host}:{Port}",
                _socketDefinition.RemoteHost, _socketDefinition.RemotePort);

            using var clientStream = client.GetStream();
            using var remoteStream = remoteClient.GetStream();

            // Linked CTS so both directions cancel when either finishes
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);

            var remoteToClient = CopyStreamAsync(remoteStream, clientStream, connectionCts.Token);
            var clientToRemote = CopyStreamAsync(clientStream, remoteStream, connectionCts.Token);

            await Task.WhenAny(remoteToClient, clientToRemote);
            connectionCts.Cancel();

            // Brief drain for the other direction to finish cleanly
            try { await Task.WhenAll(remoteToClient, clientToRemote).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* drain best-effort */ }

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
