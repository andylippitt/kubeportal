using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace KubePortal.Grpc;

public class DaemonProcess
{
    // Signal for graceful shutdown
    private static readonly CancellationTokenSource _shutdownTokenSource = new();

    // Static accessor for shutdown signal
    public static void SignalShutdown()
    {
        _shutdownTokenSource.Cancel();
    }

    // Check if daemon is running on the given port
    public static async Task<bool> IsDaemonRunningAsync(int port)
    {
        try
        {
            // Try to connect to the daemon port
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
            
            // If we can connect within 1 second, the daemon is running
            if (await Task.WhenAny(connectTask, Task.Delay(1000)) == connectTask)
            {
                // Successfully connected
                return client.Connected;
            }
            
            // Connection timed out
            return false;
        }
        catch
        {
            // Connection failed
            return false;
        }
    }

    // Get the path to the lock file
    public static string GetLockFilePath(int port)
    {
        var appDataDir = GetAppDataDirectory();
        return Path.Combine(appDataDir, $"kubeportal-{port}.lock");
    }

    // Create lock file with PID
    public static bool CreateLockFile(int port)
    {
        var lockFile = GetLockFilePath(port);
        
        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(lockFile);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            // Check if lock file exists and if so, if the process is still running
            if (File.Exists(lockFile))
            {
                string content = File.ReadAllText(lockFile);
                if (int.TryParse(content, out int pid))
                {
                    // Check if process with this PID is running
                    try
                    {
                        var process = System.Diagnostics.Process.GetProcessById(pid);
                        // Process exists, check if it's actually our daemon
                        if (process.ProcessName.Contains("kubeportal"))
                        {
                            return false; // Process is still running
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Process not found, continue to create a new lock
                    }
                }
            }
            
            // Write our PID to the file
            File.WriteAllText(lockFile, System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Remove lock file
    public static void RemoveLockFile(int port)
    {
        try
        {
            var lockFile = GetLockFilePath(port);
            if (File.Exists(lockFile))
            {
                File.Delete(lockFile);
            }
        }
        catch
        {
            // Best effort
        }
    }

    // Get path to app data directory
    private static string GetAppDataDirectory()
    {
        string appDataPath;
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            appDataPath = Path.Combine(appDataPath, "KubePortal");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            appDataPath = Path.Combine(appDataPath, "Library", "Application Support", "KubePortal");
        }
        else
        {
            // Linux or others
            appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            appDataPath = Path.Combine(appDataPath, ".kubeportal");
        }
        
        if (!Directory.Exists(appDataPath))
        {
            Directory.CreateDirectory(appDataPath);
        }
        
        return appDataPath;
    }

    // Get path to config file
    public static string GetConfigFilePath()
    {
        return Path.Combine(GetAppDataDirectory(), "config.json");
    }

    // Get path to daemon log file
    public static string GetLogFilePath()
    {
        return Path.Combine(GetAppDataDirectory(), "daemon.log");
    }

    // Run the daemon process
    public static async Task<int> RunDaemonAsync(int port, LogLevel logLevel)
    {
        // Check if daemon is already running on this port
        if (await IsDaemonRunningAsync(port))
        {
            Console.WriteLine($"Error: Daemon is already running on port {port}.");
            return 1;
        }
        
        // Try to create lock file
        if (!CreateLockFile(port))
        {
            Console.WriteLine("Error: Failed to create lock file. Is another instance running?");
            return 1;
        }
        
        try
        {
            // Set up logging
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(logLevel);
                builder.AddConsole();
                builder.AddFile(GetLogFilePath());
            });
            
            var logger = loggerFactory.CreateLogger<DaemonProcess>();
            logger.LogInformation("Starting KubePortal daemon on port {Port}", port);
            
            // Create the forward manager
            var forwardManager = new ForwardManager(GetConfigFilePath(), loggerFactory);
            await forwardManager.InitializeAsync();
            
            // Register OS signal handlers
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => 
                {
                    logger.LogInformation("Shutdown signal received from OS");
                    _shutdownTokenSource.Cancel();
                };
                
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    logger.LogInformation("Ctrl+C received");
                    _shutdownTokenSource.Cancel();
                };
            }
            
            // Create the web host for gRPC
            var builder = WebApplication.CreateBuilder(new string[] { });
            
            // Configure Kestrel server
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenLocalhost(port, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                });
            });
            
            // Add services
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(forwardManager);
            builder.Services.AddSingleton(loggerFactory);
            builder.Services.AddLogging();
            
            var app = builder.Build();
            
            // Configure the HTTP request pipeline
            app.MapGrpcService<KubePortalServiceImpl>(); // Changed from KubePortalService
            
            // Start the server
            await app.StartAsync();
            logger.LogInformation("KubePortal daemon started successfully on port {Port}", port);
            
            // Wait for shutdown signal
            await Task.Delay(-1, _shutdownTokenSource.Token).ContinueWith(_ => Task.CompletedTask);
            
            // Graceful shutdown
            logger.LogInformation("Shutting down KubePortal daemon...");
            await forwardManager.StopAllAsync();
            await app.StopAsync();
            await forwardManager.DisposeAsync();
            
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Daemon shutdown requested.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting daemon: {ex.Message}");
            return 1;
        }
        finally
        {
            RemoveLockFile(port);
        }
    }
    
    // Start the daemon as a detached process
    public static async Task<bool> StartDaemonDetachedAsync(int port, LogLevel logLevel)
    {
        // Check if already running
        if (await IsDaemonRunningAsync(port))
        {
            return true;
        }
        
        var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            return false;
        }
        
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--internal-daemon-run --api-port {port} --verbosity {logLevel}",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows-specific detached process creation
                System.Diagnostics.Process.Start(startInfo);
            }
            else
            {
                // Unix-like OS (macOS, Linux)
                startInfo.RedirectStandardOutput = false;
                startInfo.RedirectStandardError = false;
                System.Diagnostics.Process.Start(startInfo);
            }
            
            // Wait a moment for the daemon to start
            await Task.Delay(1000);
            
            // Verify it's running
            return await IsDaemonRunningAsync(port);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

// Extension method for file logging
public static class LoggingExtensions
{
    public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string filePath)
    {
        builder.AddProvider(new FileLoggerProvider(filePath));
        return builder;
    }
}

// Simple file logger implementation
public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;

    public FileLoggerProvider(string filePath)
    {
        _filePath = filePath;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(_filePath, categoryName);
    }

    public void Dispose()
    {
    }
}

public class FileLogger : ILogger
{
    private readonly string _filePath;
    private readonly string _categoryName;
    private static readonly object _lock = new object();

    public FileLogger(string filePath, string categoryName)
    {
        _filePath = filePath;
        _categoryName = categoryName;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        try
        {
            var message = formatter(state, exception);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_categoryName}: {message}";

            if (exception != null)
            {
                line += Environment.NewLine + exception;
            }

            lock (_lock)
            {
                File.AppendAllText(_filePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging should never throw
        }
    }

    private class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new NullScope();
        public void Dispose() { }
    }
}
