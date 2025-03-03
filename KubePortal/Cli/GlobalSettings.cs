using System.ComponentModel;
using Spectre.Console.Cli;

namespace KubePortal.Cli;

public class GlobalSettings : CommandSettings
{
    [CommandOption("--api-port <PORT>")]
    [Description("Port for the gRPC API (default: 50051)")]
    [DefaultValue(50051)]
    public int ApiPort { get; set; } = 50051;
    
    [CommandOption("--verbosity <LEVEL>")]
    [Description("Logging verbosity (Debug, Info, Warn, Error)")]
    [DefaultValue("Info")]
    public string Verbosity { get; set; } = "Info";
    
    [CommandOption("-q|--quiet")]
    [Description("Run in quiet mode (minimal output)")]
    public bool Quiet { get; set; }
    
    [CommandOption("--json")]
    [Description("Output in JSON format where applicable")]
    public bool Json { get; set; }
}
