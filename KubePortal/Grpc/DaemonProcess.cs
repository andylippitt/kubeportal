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

    // Add a start time file path method
    private static string GetStartTimeFilePath()
    {
        return Path.Combine(GetAppDataDirectory(), "daemon-start-time.txt");
    }

    // Save start time when daemon starts
    private static void SaveStartTime()
    {
        try
        {
            File.WriteAllText(GetStartTimeFilePath(), DateTime.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save start time: {ex.Message}");
        }
    }

    // Get the start time from file
    private static DateTime GetStartTime()
    {
        try
        {
            var path = GetStartTimeFilePath();
            if (File.Exists(path))
            {
                var timeStr = File.ReadAllText(path);
                if (DateTime.TryParse(timeStr, out var startTime))
                {
                    return startTime;
                }
            }
        }
        catch
        {
            // Ignore errors, use current time as fallback
        }
        
        return DateTime.UtcNow;
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
        
        // Save start time
        SaveStartTime();
        
        try
        {
            // Ensure log directory exists
            var logFilePath = GetLogFilePath();
            var logDir = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
            
            // Set up logging with correct verbosity mapping
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(logLevel);
                builder.AddConsole();
                
                // Configure file logging with correct message template and log rotation
                builder.AddFile(GetLogFilePath(), options => {
                    options.FileSizeLimitBytes = 10 * 1024 * 1024; // 10MB size limit
                    options.MaxRollingFiles = 3; // Keep 3 archived log files
                });
            });
            
            var logger = loggerFactory.CreateLogger<DaemonProcess>();
            logger.LogInformation("Starting KubePortal daemon on port {Port} with log level {LogLevel}", port, logLevel);
            
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
                Arguments = $"--internal-daemon-run --api-port {port} --verbosity {logLevel.ToString()}",
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
    public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string filePath, Action<FileLoggerOptions>? configure = null)
    {
        var options = new FileLoggerOptions();
        configure?.Invoke(options);
        builder.AddProvider(new FileLoggerProvider(filePath, options));
        return builder;
    }
}

public class FileLoggerOptions
{
    public long FileSizeLimitBytes { get; set; } = 5 * 1024 * 1024; // 5MB default
    public int MaxRollingFiles { get; set; } = 3;
}

// Simple file logger implementation
public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly FileLoggerOptions _options;

    public FileLoggerProvider(string filePath, FileLoggerOptions options)
    {
        _filePath = filePath;
        _options = options;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(_filePath, categoryName, _options);
    }

    public void Dispose()
    {
    }
}

public class FileLogger : ILogger
{
    private readonly string _filePath;
    private readonly string _categoryName;
    private readonly FileLoggerOptions _options;
    private static readonly object _lock = new object();

    public FileLogger(string filePath, string categoryName, FileLoggerOptions options)
    {
        _filePath = filePath;
        _categoryName = categoryName;
        _options = options;
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
                // Check if log rotation is needed
                var fileInfo = new FileInfo(_filePath);
                if (fileInfo.Exists && fileInfo.Length > _options.FileSizeLimitBytes)
                {
                    RotateLogFiles();
                }
                
                File.AppendAllText(_filePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging should never throw
        }
    }

    private void RotateLogFiles()
    {
        try
        {
            // Remove oldest log file if max count reached
            for (int i = _options.MaxRollingFiles; i > 0; i--)
            {
                string oldFile = $"{_filePath}.{i}";
                if (File.Exists(oldFile) && i == _options.MaxRollingFiles)
                {
                    File.Delete(oldFile);
                }
                else if (File.Exists(oldFile))
                {
                    File.Move(oldFile, $"{_filePath}.{i + 1}");
                }
            }

            // Rename current log file
            if (File.Exists(_filePath))
            {
                File.Move(_filePath, $"{_filePath}.1");
            }
        }
        catch
        {
            // Best effort for log rotation
        }
    }

    private class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new NullScope();
        public void Dispose() { }
    }
}
