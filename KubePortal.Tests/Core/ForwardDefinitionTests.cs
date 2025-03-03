using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Xunit;

namespace KubePortal.Tests.Core;

public class ForwardDefinitionTests
{
    [Fact]
    public void ForwardDefinitions_ShouldSerializeCorrectly()
    {
        // Arrange
        var k8sDef = new KubernetesForwardDefinition
        {
            Name = "test-k8s",
            Group = "dev",
            LocalPort = 8080,
            Context = "test-context",
            Namespace = "test-namespace",
            Service = "test-service",
            ServicePort = 5000,
            Enabled = true
        };

        var socketDef = new SocketProxyDefinition
        {
            Name = "test-socket",
            Group = "prod",
            LocalPort = 5432,
            RemoteHost = "db.example.com",
            RemotePort = 5432,
            Enabled = false
        };

        // Act
        var k8sJson = k8sDef.ToJson();
        var socketJson = socketDef.ToJson();

        // Assert
        Assert.Equal("kubernetes", k8sJson["type"]?.GetValue<string>());
        Assert.Equal("test-k8s", k8sJson["name"]?.GetValue<string>());
        Assert.Equal(8080, k8sJson["localPort"]?.GetValue<int>());
        Assert.Equal("test-context", k8sJson["context"]?.GetValue<string>());
        Assert.Equal("test-namespace", k8sJson["namespace"]?.GetValue<string>());
        Assert.Equal("test-service", k8sJson["service"]?.GetValue<string>());
        Assert.Equal(5000, k8sJson["servicePort"]?.GetValue<int>());
        Assert.True(k8sJson["enabled"]?.GetValue<bool>());

        Assert.Equal("socket", socketJson["type"]?.GetValue<string>());
        Assert.Equal("test-socket", socketJson["name"]?.GetValue<string>());
        Assert.Equal(5432, socketJson["localPort"]?.GetValue<int>());
        Assert.Equal("db.example.com", socketJson["remoteHost"]?.GetValue<string>());
        Assert.Equal(5432, socketJson["remotePort"]?.GetValue<int>());
        Assert.False(socketJson["enabled"]?.GetValue<bool>());
    }

    [Fact]
    public void ForwardDefinition_ShouldDeserializeFromJson()
    {
        // Arrange
        var k8sJsonStr = @"{
            ""type"": ""kubernetes"",
            ""name"": ""test-k8s"",
            ""group"": ""dev"",
            ""localPort"": 8080,
            ""context"": ""test-context"",
            ""namespace"": ""test-namespace"",
            ""service"": ""test-service"", 
            ""servicePort"": 5000,
            ""enabled"": true
        }";

        var socketJsonStr = @"{
            ""type"": ""socket"",
            ""name"": ""test-socket"",
            ""group"": ""prod"",
            ""localPort"": 5432,
            ""remoteHost"": ""db.example.com"",
            ""remotePort"": 5432,
            ""enabled"": false
        }";

        // Act
        var k8sJson = JsonNode.Parse(k8sJsonStr);
        var socketJson = JsonNode.Parse(socketJsonStr);
        
        var k8sDef = ForwardDefinition.FromJson(k8sJson!);
        var socketDef = ForwardDefinition.FromJson(socketJson!);

        // Assert
        Assert.IsType<KubernetesForwardDefinition>(k8sDef);
        Assert.IsType<SocketProxyDefinition>(socketDef);
        
        Assert.Equal("test-k8s", k8sDef.Name);
        Assert.Equal("dev", k8sDef.Group);
        Assert.Equal(8080, k8sDef.LocalPort);
        
        var k8sTyped = (KubernetesForwardDefinition)k8sDef;
        Assert.Equal("test-context", k8sTyped.Context);
        Assert.Equal("test-namespace", k8sTyped.Namespace);
        Assert.Equal("test-service", k8sTyped.Service);
        Assert.Equal(5000, k8sTyped.ServicePort);
        Assert.True(k8sTyped.Enabled);

        Assert.Equal("test-socket", socketDef.Name);
        Assert.Equal("prod", socketDef.Group);
        Assert.Equal(5432, socketDef.LocalPort);
        
        var socketTyped = (SocketProxyDefinition)socketDef;
        Assert.Equal("db.example.com", socketTyped.RemoteHost);
        Assert.Equal(5432, socketTyped.RemotePort);
        Assert.False(socketTyped.Enabled);
    }

    [Fact]
    public void ForwardDefinition_ShouldValidateCorrectly()
    {
        // Valid cases
        var validK8s = new KubernetesForwardDefinition
        {
            Name = "valid-k8s",
            LocalPort = 8080,
            Context = "test-context",
            Namespace = "test-namespace",
            Service = "test-service",
            ServicePort = 5000
        };

        var validSocket = new SocketProxyDefinition
        {
            Name = "valid-socket",
            LocalPort = 5432,
            RemoteHost = "localhost",
            RemotePort = 5432
        };

        // Invalid cases
        var invalidK8s = new KubernetesForwardDefinition
        {
            Name = "invalid-k8s",
            LocalPort = 8080,
            Context = "", // empty context
            Namespace = "test-namespace",
            Service = "test-service",
            ServicePort = 5000
        };

        var invalidSocket = new SocketProxyDefinition
        {
            Name = "invalid-socket",
            LocalPort = 5432,
            RemoteHost = "", // empty host
            RemotePort = 5432
        };

        var invalidPort = new SocketProxyDefinition
        {
            Name = "invalid-port",
            LocalPort = 0, // invalid port
            RemoteHost = "localhost",
            RemotePort = 5432
        };

        // Assert
        Assert.True(validK8s.Validate(out _));
        Assert.True(validSocket.Validate(out _));
        
        Assert.False(invalidK8s.Validate(out var k8sError));
        Assert.False(invalidSocket.Validate(out var socketError));
        Assert.False(invalidPort.Validate(out var portError));
        
        Assert.Contains("Context", k8sError);
        Assert.Contains("Remote host", socketError);
        Assert.Contains("Invalid port", portError);
    }

    [Fact]
    public void ForwardDefinition_ShouldCreateCorrectForwarderType()
    {
        // Arrange
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLoggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(Mock.Of<ILogger>());

        var k8sDef = new KubernetesForwardDefinition
        {
            Name = "test-k8s",
            LocalPort = 8080,
            Context = "test-context",
            Namespace = "test-namespace",
            Service = "test-service",
            ServicePort = 5000
        };

        var socketDef = new SocketProxyDefinition
        {
            Name = "test-socket",
            LocalPort = 5432,
            RemoteHost = "localhost",
            RemotePort = 5432
        };

        // Act
        var k8sForwarder = k8sDef.CreateForwarder(mockLoggerFactory.Object);
        var socketForwarder = socketDef.CreateForwarder(mockLoggerFactory.Object);

        // Assert
        Assert.Equal("KubernetesForwarder", k8sForwarder.GetType().Name);
        Assert.Equal("SocketProxyForwarder", socketForwarder.GetType().Name);
        Assert.Equal(k8sDef, k8sForwarder.Definition);
        Assert.Equal(socketDef, socketForwarder.Definition);
    }

    [Fact]
    public void UnknownForwardType_ShouldThrowException()
    {
        // Arrange
        var invalidJsonStr = @"{""type"": ""unknown"", ""name"": ""test""}";
        var invalidJson = JsonNode.Parse(invalidJsonStr)!;

        // Act & Assert
        var ex = Assert.Throws<JsonException>(() => ForwardDefinition.FromJson(invalidJson));
        Assert.Contains("Unknown forward type", ex.Message);
    }
}
