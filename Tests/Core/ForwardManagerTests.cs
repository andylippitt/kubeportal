using System.Text.Json;
using Moq;
using Xunit;

namespace KubePortal.Tests.Core;

// Create a testable subclass of ForwardManager for tests
public class TestableForwardManager : ForwardManager
{
    public TestableForwardManager(string configPath, ILoggerFactory loggerFactory)
        : base(configPath, loggerFactory)
    {
    }

    // Expose internals for testing
    public void AddForwardToConfig(ForwardDefinition forward)
    {
        _configuredForwards[forward.Name] = forward;
    }
}

public class ForwardManagerTests : IDisposable
{
    private readonly string _testConfigPath;
    private readonly Mock<ILoggerFactory> _mockLoggerFactory;
    private readonly Mock<ILogger<ForwardManager>> _mockLogger;
    private readonly Mock<ILogger> _forwarderLogger;
    
    public ForwardManagerTests()
    {
        _testConfigPath = Path.Combine(Path.GetTempPath(), $"kubeportal_test_config_{Guid.NewGuid()}.json");
        _mockLogger = new Mock<ILogger<ForwardManager>>();
        _forwarderLogger = new Mock<ILogger>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLoggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);
        _mockLoggerFactory.Setup(f => f.CreateLogger(It.IsNotIn<string>(typeof(ForwardManager).FullName!))).Returns(_forwarderLogger.Object);
    }
    
    [Fact]
    public async Task AddForward_ShouldPersistConfig()
    {
        // Arrange
        if (File.Exists(_testConfigPath))
            File.Delete(_testConfigPath);
            
        var manager = new MockForwardManager(_testConfigPath, _mockLoggerFactory.Object);
        var forward = new SocketProxyDefinition
        {
            Name = "test-forward",
            Group = "test-group",
            LocalPort = 8080,
            RemoteHost = "localhost",
            RemotePort = 5000
        };
        
        // Act
        var result = await manager.AddOrUpdateForwardAsync(forward);
        
        // Wait for file to be written
        await Task.Delay(200);
        
        // Assert
        Assert.True(result, "AddOrUpdateForwardAsync should return true");
        Assert.True(File.Exists(_testConfigPath), "Config file should be created");
        
        if (File.Exists(_testConfigPath))
        {
            // Verify config content
            var json = await File.ReadAllTextAsync(_testConfigPath);
            var jsonDoc = JsonDocument.Parse(json);
            var forwardsElement = jsonDoc.RootElement.GetProperty("forwards");
            var forwardElement = forwardsElement.GetProperty("test-forward");
            
            Assert.Equal("socket", forwardElement.GetProperty("type").GetString());
            Assert.Equal("test-group", forwardElement.GetProperty("group").GetString());
            Assert.Equal(8080, forwardElement.GetProperty("localPort").GetInt32());
            Assert.Equal("localhost", forwardElement.GetProperty("remoteHost").GetString());
            Assert.Equal(5000, forwardElement.GetProperty("remotePort").GetInt32());
            Assert.True(forwardElement.GetProperty("enabled").GetBoolean());
        }
    }
    
    [Fact]
    public async Task GetAllForwards_ShouldReturnConfiguredForwards()
    {
        // Arrange
        if (File.Exists(_testConfigPath))
            File.Delete(_testConfigPath);
            
        var manager = new ForwardManager(_testConfigPath, _mockLoggerFactory.Object);
        var forward1 = new SocketProxyDefinition
        {
            Name = "socket-forward",
            Group = "test-group",
            LocalPort = 8080,
            RemoteHost = "localhost",
            RemotePort = 5000
        };
        
        var forward2 = new KubernetesForwardDefinition
        {
            Name = "k8s-forward",
            Group = "test-group",
            LocalPort = 8081,
            Context = "test-context",
            Namespace = "default",
            Service = "test-service",
            ServicePort = 80
        };
        
        // Act
        await manager.AddOrUpdateForwardAsync(forward1);
        await manager.AddOrUpdateForwardAsync(forward2);
        var forwards = await manager.GetAllForwardsAsync();
        
        // Assert
        Assert.Equal(2, forwards.Count);
        Assert.Contains(forwards, f => f.Name == "socket-forward");
        Assert.Contains(forwards, f => f.Name == "k8s-forward");
        
        var socketForward = forwards.SingleOrDefault(f => f.Name == "socket-forward") as SocketProxyDefinition;
        Assert.NotNull(socketForward);
        Assert.Equal("localhost", socketForward!.RemoteHost);
        Assert.Equal(5000, socketForward.RemotePort);
        
        var k8sForward = forwards.SingleOrDefault(f => f.Name == "k8s-forward") as KubernetesForwardDefinition;
        Assert.NotNull(k8sForward);
        Assert.Equal("test-context", k8sForward!.Context);
        Assert.Equal("default", k8sForward.Namespace);
    }
    
    [Fact]
    public async Task GetGroupStatuses_ShouldReturnCorrectStatuses()
    {
        // Arrange
        if (File.Exists(_testConfigPath))
            File.Delete(_testConfigPath);
            
        var manager = new MockForwardManager(_testConfigPath, _mockLoggerFactory.Object);
        var forward1 = new SocketProxyDefinition
        {
            Name = "socket-forward",
            Group = "group1",
            LocalPort = 8080,
            RemoteHost = "localhost",
            RemotePort = 5000,
            Enabled = true
        };
        
        var forward2 = new KubernetesForwardDefinition
        {
            Name = "k8s-forward",
            Group = "group2",
            LocalPort = 8081,
            Context = "test-context",
            Namespace = "default",
            Service = "test-service",
            ServicePort = 80,
            Enabled = false
        };
        
        // Add directly to config without trying to start
        manager.AddForwardToConfig(forward1);
        manager.AddForwardToConfig(forward2);
        
        // Act
        var groupStatuses = await manager.GetGroupStatusesAsync();
        
        // Assert
        Assert.Equal(2, groupStatuses.Count);
        Assert.True(groupStatuses["group1"]);
        Assert.False(groupStatuses["group2"]);
    }

    [Fact]
    public async Task DeleteForward_ShouldRemoveFromConfig()
    {
        // Arrange
        if (File.Exists(_testConfigPath))
            File.Delete(_testConfigPath);
            
        var manager = new ForwardManager(_testConfigPath, _mockLoggerFactory.Object);
        var forward = new SocketProxyDefinition
        {
            Name = "to-delete",
            Group = "test-group",
            LocalPort = 8080,
            RemoteHost = "localhost",
            RemotePort = 5000
        };
        
        await manager.AddOrUpdateForwardAsync(forward);
        
        // Act
        var deleteResult = await manager.DeleteForwardAsync("to-delete");
        var forwards = await manager.GetAllForwardsAsync();
        
        // Assert
        Assert.True(deleteResult);
        // Fixed: Using Assert.Empty instead of Assert.Equal(0, ...)
        Assert.Empty(forwards);
        
        // Verify config file
        var json = await File.ReadAllTextAsync(_testConfigPath);
        var jsonDoc = JsonDocument.Parse(json);
        var forwardsElement = jsonDoc.RootElement.GetProperty("forwards");
        
        Assert.Empty(forwardsElement.EnumerateObject());
    }

    // Fix up the test by using a special test-only constructor
    [Fact]
    public async Task EnableGroup_ShouldEnableAllInGroup()
    {
        // Arrange
        if (File.Exists(_testConfigPath))
            File.Delete(_testConfigPath);
            
        var testManager = new TestableForwardManager(_testConfigPath, _mockLoggerFactory.Object);
        
        // Add forwards to the same group with different enabled states
        var forward1 = new SocketProxyDefinition
        {
            Name = "socket1",
            Group = "test-group",
            LocalPort = 8080,
            RemoteHost = "localhost",
            RemotePort = 5000,
            Enabled = false
        };
        
        var forward2 = new SocketProxyDefinition
        {
            Name = "socket2",
            Group = "test-group",
            LocalPort = 8081,
            RemoteHost = "localhost",
            RemotePort = 5001,
            Enabled = true
        };
        
        var forward3 = new SocketProxyDefinition
        {
            Name = "socket3",
            Group = "other-group",
            LocalPort = 8082,
            RemoteHost = "localhost",
            RemotePort = 5002,
            Enabled = false
        };
        
        // Add directly to config without trying to start them
        testManager.AddForwardToConfig(forward1);
        testManager.AddForwardToConfig(forward2); 
        testManager.AddForwardToConfig(forward3);
        
        // Mock the StartForwardInternalAsync for the test
        _mockLoggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(_mockLogger.Object);
        
        // Act
        var result = await testManager.EnableGroupAsync("test-group");
        var forwards = await testManager.GetAllForwardsAsync();
        
        // Assert - Just check if the enabled flags are set correctly
        Assert.True(result);
        
        // Both forwards in test-group should be enabled
        var socket1 = forwards.SingleOrDefault(f => f.Name == "socket1");
        var socket2 = forwards.SingleOrDefault(f => f.Name == "socket2");
        var socket3 = forwards.SingleOrDefault(f => f.Name == "socket3");
        
        Assert.True(socket1?.Enabled);
        Assert.True(socket2?.Enabled);
        Assert.False(socket3?.Enabled); // Other group should be unchanged
    }

    [Fact]
    public async Task DisableGroup_ShouldDisableAllInGroup()
    {
        // Arrange
        if (File.Exists(_testConfigPath))
            File.Delete(_testConfigPath);
            
        var manager = new ForwardManager(_testConfigPath, _mockLoggerFactory.Object);
        
        // Add forwards to the same group with different enabled states
        var forward1 = new SocketProxyDefinition
        {
            Name = "socket1",
            Group = "test-group",
            LocalPort = 8080,
            RemoteHost = "localhost",
            RemotePort = 5000,
            Enabled = true
        };
        
        var forward2 = new SocketProxyDefinition
        {
            Name = "socket2",
            Group = "test-group",
            LocalPort = 8081,
            RemoteHost = "localhost",
            RemotePort = 5001,
            Enabled = false
        };
        
        var forward3 = new SocketProxyDefinition
        {
            Name = "socket3",
            Group = "other-group",
            LocalPort = 8082,
            RemoteHost = "localhost",
            RemotePort = 5002,
            Enabled = true
        };
        
        await manager.AddOrUpdateForwardAsync(forward1);
        await manager.AddOrUpdateForwardAsync(forward2);
        await manager.AddOrUpdateForwardAsync(forward3);
        
        // Act
        var result = await manager.DisableGroupAsync("test-group");
        var forwards = await manager.GetAllForwardsAsync();
        
        // Assert
        Assert.True(result);
        
        // Both forwards in test-group should be disabled
        var socket1 = forwards.SingleOrDefault(f => f.Name == "socket1");
        var socket2 = forwards.SingleOrDefault(f => f.Name == "socket2");
        var socket3 = forwards.SingleOrDefault(f => f.Name == "socket3");
        
        Assert.False(socket1?.Enabled);
        Assert.False(socket2?.Enabled);
        Assert.True(socket3?.Enabled); // Other group should be unchanged
    }

    [Fact]
    public async Task GetForwardByName_ShouldReturnCorrectForward()
    {
        // Arrange
        if (File.Exists(_testConfigPath))
            File.Delete(_testConfigPath);
            
        var manager = new ForwardManager(_testConfigPath, _mockLoggerFactory.Object);
        var forward = new SocketProxyDefinition
        {
            Name = "specific-forward",
            Group = "test-group",
            LocalPort = 8080,
            RemoteHost = "specific-host",
            RemotePort = 5678
        };
        
        await manager.AddOrUpdateForwardAsync(forward);
        
        // Act
        var result = await manager.GetForwardByNameAsync("specific-forward");
        var notFound = await manager.GetForwardByNameAsync("non-existent");
        
        // Assert
        Assert.NotNull(result);
        Assert.IsType<SocketProxyDefinition>(result);
        
        var socketForward = result as SocketProxyDefinition;
        Assert.Equal("specific-host", socketForward!.RemoteHost);
        Assert.Equal(5678, socketForward.RemotePort);
        
        Assert.Null(notFound);
    }
    
    public void Dispose()
    {
        if (File.Exists(_testConfigPath))
            File.Delete(_testConfigPath);
    }
}
