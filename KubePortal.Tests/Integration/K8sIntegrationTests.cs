using System.Net.Sockets;
using KubePortal.Core;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace KubePortal.Tests.Integration;

// These tests require an actual K8s cluster with the specified services
// They're marked as Skip by default but can be enabled for integration testing
public class K8sIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _configPath;
    
    public K8sIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _configPath = Path.Combine(Path.GetTempPath(), $"kubeportal_integration_{Guid.NewGuid()}.json");
        
        // Set up logging to the test output
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new XunitLoggerProvider(_output));
            builder.SetMinimumLevel(LogLevel.Debug);
        });
    }
    
    [Fact]
    public async Task ForwardToRabbitMQ_ShouldConnect()
    {
        // Arrange
        var manager = new ForwardManager(_configPath, _loggerFactory);
        var rabbitForward = new KubernetesForwardDefinition
        {
            Name = "test-rabbitmq",
            Group = "test-services",
            LocalPort = GetAvailablePort(),
            Context = "test2", // Use your available context
            Namespace = "test2",
            Service = "test2-rabbitmq",
            ServicePort = 5672,
            Enabled = true
        };
        
        try
        {
            // Act
            _output.WriteLine($"Setting up port forward to RabbitMQ on local port {rabbitForward.LocalPort}");
            await manager.AddOrUpdateForwardAsync(rabbitForward);
            
            // Give it a moment to establish
            await Task.Delay(2000);
            
            // Attempt to connect to the forwarded port
            _output.WriteLine("Attempting to connect to forwarded port");
            using var client = new TcpClient();
            
            // This should succeed if the port forward is working
            var connectTask = client.ConnectAsync("localhost", rabbitForward.LocalPort);
            var timeoutTask = Task.Delay(5000);
            
            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            
            // Assert
            Assert.Equal(connectTask, completedTask);
            Assert.True(client.Connected, "Should be able to connect to the forwarded port");
            
            var forwarders = manager.GetActiveForwarders();
            Assert.Contains("test-rabbitmq", forwarders.Keys);
            Assert.True(forwarders["test-rabbitmq"].IsActive);
        }
        finally
        {
            // Cleanup
            await manager.StopAllAsync();
            await manager.DisposeAsync();
            
            if (File.Exists(_configPath))
                File.Delete(_configPath);
        }
    }
    
    [Fact]
    public async Task ForwardToRedis_ShouldConnect()
    {
        // Arrange
        var manager = new ForwardManager(_configPath, _loggerFactory);
        var redisForward = new KubernetesForwardDefinition
        {
            Name = "test-redis",
            Group = "test-services",
            LocalPort = GetAvailablePort(),
            Context = "test2",
            Namespace = "test2",
            Service = "test2-redis-master",
            ServicePort = 6379,
            Enabled = true
        };
        
        try
        {
            // Act
            _output.WriteLine($"Setting up port forward to Redis on local port {redisForward.LocalPort}");
            await manager.AddOrUpdateForwardAsync(redisForward);
            
            // Give it a moment to establish
            await Task.Delay(2000);
            
            // Attempt to connect to the forwarded port
            _output.WriteLine("Attempting to connect to forwarded port");
            using var client = new TcpClient();
            
            var connectTask = client.ConnectAsync("localhost", redisForward.LocalPort);
            var timeoutTask = Task.Delay(5000);
            
            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            
            // Assert
            Assert.Equal(connectTask, completedTask);
            Assert.True(client.Connected, "Should be able to connect to the forwarded port");
        }
        finally
        {
            // Cleanup
            await manager.StopAllAsync();
            await manager.DisposeAsync();
            
            if (File.Exists(_configPath))
                File.Delete(_configPath);
        }
    }
    
    [Fact]
    public async Task ForwardToApiService_ShouldConnect()
    {
        // Arrange
        var manager = new ForwardManager(_configPath, _loggerFactory);
        var apiForward = new KubernetesForwardDefinition
        {
            Name = "test-api",
            Group = "test-services",
            LocalPort = GetAvailablePort(),
            Context = "test2",
            Namespace = "test2",
            Service = "test2-dems-api",
            ServicePort = 5001,
            Enabled = true
        };
        
        try
        {
            // Act
            _output.WriteLine($"Setting up port forward to API service on local port {apiForward.LocalPort}");
            await manager.AddOrUpdateForwardAsync(apiForward);
            
            // Give it a moment to establish
            await Task.Delay(2000);
            
            // Attempt to connect to the forwarded port
            _output.WriteLine("Attempting to connect to forwarded port");
            using var client = new TcpClient();
            
            var connectTask = client.ConnectAsync("localhost", apiForward.LocalPort);
            var timeoutTask = Task.Delay(5000);
            
            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            
            // Assert
            Assert.Equal(connectTask, completedTask);
            Assert.True(client.Connected, "Should be able to connect to the forwarded port");
        }
        finally
        {
            // Cleanup
            await manager.StopAllAsync();
            await manager.DisposeAsync();
            
            if (File.Exists(_configPath))
                File.Delete(_configPath);
        }
    }
    
    [Fact]
    public async Task MultipleForwards_ShouldAllWork()
    {
        // Arrange
        var manager = new ForwardManager(_configPath, _loggerFactory);
        
        // Create several forwards
        var redisForward = new KubernetesForwardDefinition
        {
            Name = "test-redis-multi",
            Group = "test-multi",
            LocalPort = GetAvailablePort(),
            Context = "test2",
            Namespace = "test2",
            Service = "test2-redis-master",
            ServicePort = 6379,
            Enabled = true
        };
        
        var rabbitForward = new KubernetesForwardDefinition
        {
            Name = "test-rabbit-multi",
            Group = "test-multi",
            LocalPort = GetAvailablePort(),
            Context = "test2",
            Namespace = "test2",
            Service = "test2-rabbitmq",
            ServicePort = 5672,
            Enabled = true
        };
        
        var apiForward = new KubernetesForwardDefinition
        {
            Name = "test-api-multi",
            Group = "test-multi",
            LocalPort = GetAvailablePort(),
            Context = "test2",
            Namespace = "test2",
            Service = "test2-dems-api",
            ServicePort = 5001,
            Enabled = true
        };
        
        try
        {
            // Act - add all forwards
            _output.WriteLine($"Setting up multiple port forwards");
            await manager.AddOrUpdateForwardAsync(redisForward);
            await manager.AddOrUpdateForwardAsync(rabbitForward);
            await manager.AddOrUpdateForwardAsync(apiForward);
            
            // Give time to establish
            await Task.Delay(3000);
            
            // Check active forwarders
            var forwarders = manager.GetActiveForwarders();
            
            // Assert
            Assert.Equal(3, forwarders.Count);
            Assert.True(forwarders["test-redis-multi"].IsActive);
            Assert.True(forwarders["test-rabbit-multi"].IsActive);
            Assert.True(forwarders["test-api-multi"].IsActive);
            
            // Test individual connections
            using (var redisClient = new TcpClient())
            {
                await redisClient.ConnectAsync("localhost", redisForward.LocalPort, CancellationToken.None);
                Assert.True(redisClient.Connected, "Should connect to Redis");
            }
            
            using (var rabbitClient = new TcpClient())
            {
                await rabbitClient.ConnectAsync("localhost", rabbitForward.LocalPort, CancellationToken.None);
                Assert.True(rabbitClient.Connected, "Should connect to RabbitMQ");
            }
            
            using (var apiClient = new TcpClient())
            {
                await apiClient.ConnectAsync("localhost", apiForward.LocalPort, CancellationToken.None);
                Assert.True(apiClient.Connected, "Should connect to API");
            }
            
            // Test group operations
            await manager.DisableGroupAsync("test-multi");
            
            // Wait for shutdown
            await Task.Delay(2000);
            
            forwarders = manager.GetActiveForwarders();
            Assert.Empty(forwarders);
            
            // Re-enable
            await manager.EnableGroupAsync("test-multi");
            await Task.Delay(2000);
            
            forwarders = manager.GetActiveForwarders();
            Assert.Equal(3, forwarders.Count);
        }
        finally
        {
            // Cleanup
            await manager.StopAllAsync();
            await manager.DisposeAsync();
            
            if (File.Exists(_configPath))
                File.Delete(_configPath);
        }
    }
    
    // Helper methods
    private int GetAvailablePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

// Logger that outputs to XUnit's test output
public class XunitLogger : ILogger
{
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly string _categoryName;

    public XunitLogger(ITestOutputHelper testOutputHelper, string categoryName)
    {
        _testOutputHelper = testOutputHelper;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => new NoopDisposable();

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _testOutputHelper.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {_categoryName}: {formatter(state, exception)}");
        if (exception != null)
            _testOutputHelper.WriteLine($"Exception: {exception}");
    }

    private class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

public class XunitLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _testOutputHelper;

    public XunitLoggerProvider(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new XunitLogger(_testOutputHelper, categoryName);
    }

    public void Dispose() { }
}