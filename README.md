# KubePortal

<div align="center">

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![License](https://img.shields.io/badge/License-MIT-blue)](LICENSE)
[![VS Code Extension](https://img.shields.io/badge/VS%20Code-Extension-007ACC)](https://marketplace.visualstudio.com/items?itemName=violetdata.kubeportal-vscode)

**Simplified port forwarding for Kubernetes and TCP sockets**

</div>

## Overview

KubePortal is a developer tool that simplifies port forwarding for both Kubernetes services and TCP sockets. It provides a unified interface for managing multiple port forwards with persistent configuration, background operation, and a VS Code extension for seamless integration with your IDE.

## Key Features

- **Multiple Forward Types**
  - 🌐 Kubernetes service port forwarding
  - 🔌 TCP socket proxying
- **Daemon Architecture**
  - Persistent background service keeps forwards running
  - gRPC-based communication between CLI and daemon
- **Group Management**
  - Organize forwards into logical groups
  - Enable/disable entire groups with a single command
- **VS Code Integration**
  - Manage forwards directly from VS Code
  - Visual status indicators for active forwards
- **Robust CLI**
  - Comprehensive command suite
  - JSON output mode for scripting and automation
- **Performance Metrics**
  - Track bytes transferred and connection counts
  - Monitor forward status in real-time

## Installation

### Prerequisites

- .NET 8.0 or later
- For Kubernetes forwarding: valid kubeconfig with appropriate access permissions

### Build from Source

```bash
# Clone the repository
git clone https://github.com/andylippitt/kubeportal.git
cd kubeportal

# Build the CLI
dotnet build

# Optional: Install the VS Code extension
cd KubePortal.VSCode
npm install
npm run package-vsix
code --install-extension ../bin/kubeportal-vscode-0.1.0.vsix
```

## Getting Started

### Managing the Daemon

```bash
# Start the daemon (required before using any forwards)
kubeportal daemon start

# Check daemon status
kubeportal daemon status

# Stop the daemon when finished
kubeportal daemon stop
```

### Creating Port Forwards

#### TCP Socket Forward

```bash
kubeportal forward create \
  --name postgres-local \
  --local-port 5432 \
  --type socket \
  --remote-host localhost \
  --remote-port 5432 \
  --group database
```

#### Kubernetes Service Forward

```bash
kubeportal forward create \
  --name redis-k8s \
  --local-port 6379 \
  --type kubernetes \
  --context my-cluster \
  --namespace redis \
  --service redis-master \
  --service-port 6379 \
  --group cache
```

### Managing Forwards

```bash
# List all forwards
kubeportal forward list

# Start a specific forward
kubeportal forward start redis-k8s

# Stop a forward
kubeportal forward stop redis-k8s

# Delete a forward
kubeportal forward delete postgres-local
```

### Working with Groups

```bash
# List all groups
kubeportal group list

# Enable all forwards in a group
kubeportal group enable cache

# Disable all forwards in a group
kubeportal group disable database

# Delete a group and all its forwards
kubeportal group delete temporary-group
```

## Configuration Management

KubePortal supports exporting and applying configuration from JSON files:

```bash
# Export current configuration
kubeportal export > kubeportal-config.json

# Apply configuration from a file
kubeportal apply --file kubeportal-config.json

# Apply configuration with removal of missing forwards
kubeportal apply --file config.json --remove-missing

# Apply configuration to a specific group
kubeportal apply --file config.json --group development
```

### Configuration Format

```json
{
  "forwards": {
    "redis-k8s": {
      "type": "kubernetes",
      "name": "redis-k8s",
      "group": "cache",
      "localPort": 6379,
      "enabled": true,
      "context": "my-cluster",
      "namespace": "redis",
      "service": "redis-master",
      "servicePort": 6379
    },
    "postgres-local": {
      "type": "socket",
      "name": "postgres-local",
      "group": "database",
      "localPort": 5432,
      "enabled": true,
      "remoteHost": "localhost",
      "remotePort": 5432
    }
  }
}
```

## VS Code Extension

The KubePortal VS Code extension provides a graphical interface for managing your port forwards:

- **Tree View**: See all your forwards organized by group
- **Status Indicators**: Visual indicators for active/inactive forwards
- **Quick Actions**: Start, stop, and delete forwards with a click
- **Auto-Start**: Optionally start the daemon when VS Code launches

### Extension Settings

| Setting | Description | Default |
|---------|-------------|---------|
| `kubeportal.daemonPort` | Port for KubePortal daemon | 50051 |
| `kubeportal.autoStartDaemon` | Start daemon when extension activates | `true` |
| `kubeportal.autoRefreshInterval` | Interval for refreshing status (ms) | 5000 |
| `kubeportal.shutdownOnExit` | Stop daemon when VS Code closes | `false` |
| `kubeportal.showHistory` | Show connection history view | `true` |

## Advanced Usage

### JSON Output

Add `--json` to most commands for machine-readable output:

```bash
kubeportal forward list --json
kubeportal daemon status --json
```

### Scriptable Operations

Combining JSON output with `jq` for powerful scripting:

```bash
# Get all active forwards
kubeportal forward list --json | jq '.[] | select(.active == true)'

# Count forwards by group
kubeportal forward list --json | jq 'group_by(.group) | map({group: .[0].group, count: length})'
```

### Command Reference

Common options available for most commands:

| Option | Description |
|--------|-------------|
| `--api-port <PORT>` | Daemon API port (default: 50051) |
| `--verbosity <LEVEL>` | Logging level (Debug, Info, Warn, Error) |
| `--quiet` | Minimal output for scripts |
| `--json` | Output in JSON format |

## Troubleshooting

### Log Files

Logs are stored in the following locations:

- **Windows**: `%LOCALAPPDATA%\KubePortal\daemon.log`
- **macOS**: `~/Library/Application Support/KubePortal/daemon.log`
- **Linux**: `~/.kubeportal/daemon.log`

### Common Issues

**"Daemon is not running" error**
- Run `kubeportal daemon start` to start the daemon
- Check if something else is using port 50051 (or use `--api-port` to specify a different port)

**"Address already in use" error**
- The local port for forwarding is already in use
- Use `netstat -tuln | grep <port>` to identify the process using the port

**Kubernetes connection issues**
- Verify your kubeconfig context: `kubectl config get-contexts`
- Check service existence: `kubectl get svc -n <namespace>`

## Replacing the running kubeportal binary

If you need to replace the running daemon binary (for example, to test a freshly built single-file executable), follow these steps carefully. These instructions assume you have a built `kubeportal` executable from the `dotnet publish` step.

1. Back up the existing binary (recommended):

```bash
sudo cp /usr/local/bin/kubeportal /usr/local/bin/kubeportal.bak.$(date +%s)
```

2. Publish a single-file Linux executable (from repo root):

```bash
dotnet publish KubePortal/KubePortal.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o KubePortal/bin/publish
```

3. Replace the binary atomically and set executable permission:

```bash
sudo cp KubePortal/bin/publish/kubeportal /usr/local/bin/kubeportal.new
sudo chmod +x /usr/local/bin/kubeportal.new
sudo mv /usr/local/bin/kubeportal.new /usr/local/bin/kubeportal
```

4. Restart the daemon. If the daemon was started manually, re-run the same command (example):

```bash
nohup /usr/local/bin/kubeportal --internal-daemon-run --api-port 50051 --verbosity Information \
  > /tmp/kubeportal.log 2>&1 &
```

If the daemon is managed by a service manager (systemd, supervisord, etc.) use the appropriate service control command instead.

5. Verify the daemon is running and responsive:

```bash
# Check process
ps aux | grep -i kubeportal | grep -v grep

# Use the CLI to list forwards
/usr/local/bin/kubeportal forward list --api-port 50051 --json

# Tail recent logs
tail -n 200 /tmp/kubeportal.log
```

Safety notes
- If the daemon is started by your environment or VS Code extension, prefer to stop it from there before replacing the binary to avoid race conditions.
- Backing up the original binary ensures you can roll back quickly.

Updating the DEMS devcontainer

You may want the new `kubeportal` executable available inside the DEMS devcontainer for local development. To copy the binary into the DEMS repo devcontainer directory:

```bash
sudo cp /usr/local/bin/kubeportal /workspaces/DEMS/.devcontainer/kubeportal
sudo chown $USER:$USER /workspaces/DEMS/.devcontainer/kubeportal
chmod +x /workspaces/DEMS/.devcontainer/kubeportal
```

This is useful when the devcontainer init or helpers expect the binary to be present in the repo.

## Architecture

KubePortal is built with a client-daemon architecture:

1. **CLI Client**: The command-line interface that sends commands to the daemon
2. **Daemon Process**: A background service that maintains active forwards
3. **gRPC Communication**: Efficient, typed communication between client and daemon
4. **VS Code Extension**: A UI layer that communicates with the daemon

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is licensed under the MIT License - see the LICENSE file for details.
