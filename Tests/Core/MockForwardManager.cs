using Moq;

namespace KubePortal.Tests.Core;

/// <summary>
/// A special manager for testing that doesn't actually try to start forwarders
/// </summary>
public class MockForwardManager : ForwardManager
{
    public MockForwardManager(string configPath, ILoggerFactory loggerFactory) 
        : base(configPath, loggerFactory, persistenceEnabled: true, watchConfigEnabled: false)
    {
    }

    // Override the entire AddOrUpdateForwardAsync method for testing purposes
    public override async Task<bool> AddOrUpdateForwardAsync(ForwardDefinition forward)
    {
        // Simplified implementation specifically for tests
        // Don't call the base method which may have socket operations
        
        // Validate the forward
        if (!forward.Validate(out var errorMessage))
        {
            _logger.LogWarning("Invalid forward definition: {Error}", errorMessage);
            return false;
        }
        
        await _configLock.WaitAsync();
        try
        {
            // Store in config dictionary
            _configuredForwards[forward.Name] = forward;
            
            // Save to file system
            if (_persistenceEnabled)
            {
                await SaveConfigAsync();
            }
            
            // Create a mock forwarder if enabled
            if (forward.Enabled)
            {
                var mockForwarder = new Mock<IForwarder>();
                mockForwarder.Setup(f => f.Definition).Returns(forward);
                mockForwarder.Setup(f => f.IsActive).Returns(true);
                mockForwarder.Setup(f => f.BytesTransferred).Returns(1000);
                
                _activeForwarders[forward.Name] = mockForwarder.Object;
                
                _logger.LogInformation("Started mock forward '{Name}'", forward.Name);
            }
            
            // Always return success for tests
            return true;
        }
        finally
        {
            _configLock.Release();
        }
    }
    
    protected override async Task<bool> StartForwardInternalAsync(ForwardDefinition forward)
    {
        await Task.CompletedTask;

        // Create a mock forwarder that simulates success
        var mockForwarder = new Mock<IForwarder>();
        mockForwarder.Setup(f => f.Definition).Returns(forward);
        mockForwarder.Setup(f => f.IsActive).Returns(true);
        mockForwarder.Setup(f => f.BytesTransferred).Returns(1000);
        
        // Add to active forwarders
        _activeForwarders[forward.Name] = mockForwarder.Object;
        
        _logger.LogInformation("Started mock forward '{Name}'", forward.Name);
        
        // Always return true for test purposes
        return true;
    }
    
    public void AddForwardToConfig(ForwardDefinition forward) 
    {
        _configuredForwards[forward.Name] = forward;
    }
    
    // Override this to make the test more reliable
    protected override async Task SaveConfigAsync()
    {
        // Call base method first to ensure file is written
        await base.SaveConfigAsync();
        
        // Add a small delay to ensure file is flushed
        await Task.Delay(100);
    }
}
