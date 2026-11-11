using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KubePortal.Tests.Core;

/// <summary>
/// The client cache is hit by every new connection, often dozens at once (a Grafana
/// dashboard opening fires a burst). These pin down the two ways that used to drop
/// connections: callers building rival clients, and a client being disposed while
/// another connection was still using it.
/// </summary>
public class KubernetesCacheClientTests
{
    private sealed class Factory
    {
        public int Calls;
        public readonly List<Mock<IKubernetes>> Made = new();

        public IKubernetes Create(string context)
        {
            Interlocked.Increment(ref Calls);
            // Widen the race window the way kubeconfig parsing / exec auth does.
            Thread.Sleep(20);
            var mock = new Mock<IKubernetes>();
            lock (Made) Made.Add(mock);
            return mock.Object;
        }
    }

    private static KubernetesCache Cache(Factory f, TimeSpan ttl, TimeSpan grace) =>
        new(NullLoggerFactory.Instance, f.Create, ttl, grace);

    private static IKubernetes[] Burst(KubernetesCache cache, int n)
    {
        using var start = new Barrier(n);
        var results = new IKubernetes[n];
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = n }, i =>
        {
            start.SignalAndWait();
            results[i] = cache.GetClient("ops");
        });
        return results;
    }

    [Fact]
    public void ConcurrentFirstUse_BuildsOneClient()
    {
        var f = new Factory();
        using var cache = Cache(f, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2));

        var clients = Burst(cache, 32);

        Assert.Equal(1, f.Calls);
        Assert.Single(clients.Distinct());
        f.Made[0].Verify(c => c.Dispose(), Times.Never);
    }

    [Fact]
    public async Task ConcurrentRefreshAfterExpiry_DoesNotDisposeAClientInUse()
    {
        var f = new Factory();
        using var cache = Cache(f, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(600));

        var original = cache.GetClient("ops");
        await Task.Delay(150);                       // let it expire

        var refreshed = Burst(cache, 32);

        // One refresh, not one per caller, and everyone got the same new client.
        Assert.Equal(2, f.Calls);
        Assert.Single(refreshed.Distinct());
        Assert.NotSame(original, refreshed[0]);

        // The old client may still be mid-request on another connection: not disposed yet...
        f.Made[0].Verify(c => c.Dispose(), Times.Never);

        // ...but it is released once the grace period has passed.
        await Task.Delay(1000);
        f.Made[0].Verify(c => c.Dispose(), Times.Once);
        f.Made[1].Verify(c => c.Dispose(), Times.Never);
    }
}
