using Moq;
using Xunit;
using System.Text.Json.Nodes;

namespace KubePortal.Tests.Core;

public class ConfigFileTests : IDisposable
{
    private readonly string _testConfigDir;
    private readonly string _testConfigPath;
    private readonly Mock<ILoggerFactory> _mockLoggerFactory;
    private readonly Mock<ILogger<ForwardManager>> _mockLogger;
    
    public ConfigFileTests()
    {
        _testConfigDir = Path.Combine(Path.GetTempPath(), $"kubeportal_tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testConfigDir);
        _testConfigPath = Path.Combine(_testConfigDir, "config.json");
        
        _mockLogger = new Mock<ILogger<ForwardManager>>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLoggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);
    }
    
    [Fact]
    public async Task InitializeAsync_EmptyConfig_ShouldNotFail()
    {
        // Arrange - config doesn't exist yet
        var manager = new ForwardManager(_testConfigPath, _mockLoggerFactory.Object);
        
        // Act - should not throw
        await manager.InitializeAsync();
        
        // Assert
        var forwards = await manager.GetAllForwardsAsync();
        Assert.Empty(forwards);
    }
    
    [Fact]
    public async Task ConfigPersistence_ShouldCreateDirectory()
    {
        // Arrange - use non-existent subdirectory
        var deepConfigPath = Path.Combine(_testConfigDir, "subdir", "config.json");
        
        // Don't watch config for this test (watchConfigEnabled: false)
        var manager = new ForwardManager(deepConfigPath, _mockLoggerFactory.Object, 
            persistenceEnabled: true, watchConfigEnabled: false);
        
        var forward = new SocketProxyDefinition
        {
            Name = "test-forward",
            LocalPort = 8080,
            RemoteHost = "localhost",
            RemotePort = 5000
        };
        
        // Act
        await manager.AddOrUpdateForwardAsync(forward);
        
        // Assert
        Assert.True(File.Exists(deepConfigPath));
        Assert.True(Directory.Exists(Path.GetDirectoryName(deepConfigPath)));
    }
    
    [Fact]
    public async Task LoadConfig_ShouldRecreateForwarders()
    {
        // Arrange
        // First manager creates and saves config
        var manager1 = new ForwardManager(_testConfigPath, _mockLoggerFactory.Object);
        
        var forward = new KubernetesForwardDefinition
        {
            Name = "test-k8s",
            Group = "test-group",
            LocalPort = 8080,
            Context = "test-context",
            Namespace = "default",
            Service = "test-service",
            ServicePort = 80,
            Enabled = true
        };
        
        await manager1.AddOrUpdateForwardAsync(forward);
        await manager1.DisposeAsync();
        
        // Make sure file is completely written and flushed
        await Task.Delay(500);
        
        // Verify the JSON was written with all properties
        var json = await File.ReadAllTextAsync(_testConfigPath);
        var jsonObj = JsonNode.Parse(json);
        var forwardObj = jsonObj?["forwards"]?["test-k8s"];
        
        Assert.NotNull(forwardObj);
        Assert.Equal("test-context", forwardObj["context"]?.GetValue<string>());
        
        // Act
        // Second manager loads from the same config
        var manager2 = new ForwardManager(_testConfigPath, _mockLoggerFactory.Object);
        await manager2.InitializeAsync();
        
        var forwards = await manager2.GetAllForwardsAsync();
        var retrievedForward = await manager2.GetForwardByNameAsync("test-k8s");
        
        // Assert
        Assert.Single(forwards);
        Assert.NotNull(retrievedForward);
        Assert.IsType<KubernetesForwardDefinition>(retrievedForward);
        
        var k8sForward = retrievedForward as KubernetesForwardDefinition;
        Assert.NotNull(k8sForward);
        Assert.Equal("test-k8s", k8sForward!.Name);
        Assert.Equal("test-context", k8sForward.Context);
        Assert.Equal("default", k8sForward.Namespace);
        Assert.Equal("test-service", k8sForward.Service);
        
        await manager2.DisposeAsync();
    }
    
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testConfigDir))
                Directory.Delete(_testConfigDir, true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
