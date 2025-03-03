# KubePortal

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![License](https://img.shields.io/badge/License-MIT-blue)
![Status](https://img.shields.io/badge/Status-Active-brightgreen)

KubePortal is a powerful command-line tool that simplifies port forwarding for both Kubernetes services and TCP sockets. It provides a unified interface for managing multiple port forwards with persistent configuration and background operation.

<p align="center">
  <img src="https://raw.githubusercontent.com/username/kubeportal/main/docs/assets/kubeportal-logo.png" alt="KubePortal Logo" width="200"/>
</p>

## 🚀 Features

- **Multiple Forward Types**:
  - 🌐 Kubernetes service port forwarding
  - 🔌 TCP socket proxying
- **Daemon Process**: Run forwards in the background as a persistent service
- **Group Management**: Organize forwards into logical groups that can be enabled/disabled together
- **Persistent Configuration**: Save and load your port forwarding setup across sessions
- **JSON Output**: Machine-readable output for integration with other tools and scripts
- **Metrics & Monitoring**: Track bytes transferred and connection counts for each forward
- **Robust CLI**: Rich command-line interface with comprehensive help and documentation

## 📋 Requirements

- .NET 8.0 or later
- For Kubernetes forwarding: Valid kubeconfig with appropriate access permissions

## 🔧 Installation

### Build from Source

```bash
git clone https://github.com/andylippitt/kubeportal.git
cd kubeportal
dotnet build
dotnet run -- --help
```

## 🏁 Quick Start

### Starting the Daemon

The KubePortal daemon needs to be running for port forwards to work:

```bash
# Start the daemon in the background
kubeportal daemon start

# Check daemon status
kubeportal daemon status
```

### Creating Port Forwards

#### Kubernetes Service Forward

```bash
kubeportal forward create \
  --name postgres-dev \
  --local-port 5432 \
  --type kubernetes \
  --context my-cluster \
  --namespace database \
  --service postgres \
  --service-port 5432 \
  --group database
```

#### TCP Socket Forward

```bash
kubeportal forward create \
  --name redis-cache \
  --local-port 6379 \
  --type socket \
  --remote-host redis.internal \
  --remote-port 6379 \
  --group cache
```

### Managing Forwards

```bash
# List all forwards
kubeportal forward list

# See detailed status in JSON format
kubeportal forward list --json | jq

# Start/stop specific forwards
kubeportal forward start postgres-dev
kubeportal forward stop postgres-dev

# Delete a forward
kubeportal forward delete redis-cache
```

## 🧩 Working with Groups

Groups allow you to manage related forwards together:

```bash
# List all groups with their status
kubeportal group list

# Enable all forwards in a group
kubeportal group enable database

# Disable all forwards in a group
kubeportal group disable cache
```

## 📝 Configuration Management

KubePortal supports exporting and applying configuration from JSON files:

```bash
# Export current configuration to a file
kubeportal export > kubeportal-config.json

# Apply configuration
kubeportal apply --file kubeportal-config.json

# Apply configuration from stdin
cat config.json | kubeportal apply --stdin

# Apply with removal of missing forwards
kubeportal apply --file config.json --remove-missing
```

### Example Configuration

```json
{
  "forwards": {
    "postgres-dev": {
      "type": "kubernetes",
      "name": "postgres-dev",
      "group": "database",
      "localPort": 5432,
      "enabled": true,
      "context": "my-cluster",
      "namespace": "database",
      "service": "postgres",
      "servicePort": 5432
    },
    "redis-cache": {
      "type": "socket",
      "name": "redis-cache",
      "group": "cache",
      "localPort": 6379,
      "enabled": true,
      "remoteHost": "redis.internal",
      "remotePort": 6379
    }
  }
}
```

## 🔍 Advanced Usage

### JSON Output

Add `--json` to any command to get machine-readable JSON output:

```bash
kubeportal forward list --json
kubeportal daemon status --json
```

### Daemon Control

```bash
# Start the daemon
kubeportal daemon start

# Check status
kubeportal daemon status

# Reload configuration
kubeportal daemon reload

# Stop the daemon
kubeportal daemon stop
```

### Command Options

Common options available for most commands:

| Option | Description |
|--------|-------------|
| `--api-port <PORT>` | Specify daemon API port (default: 50051) |
| `--verbosity <LEVEL>` | Set logging level (Debug, Info, Warn, Error) |
| `--quiet` | Minimal output, suitable for scripts |
| `--json` | Output in JSON format |

## 🔧 Troubleshooting

### Common Issues

**"Daemon is not running" error**
- Run `kubeportal daemon start` to start the daemon process
- Check if another process is using the API port (default: 50051)

**"Address already in use" error**
- The local port is already in use by another application
- Choose a different port or stop the application using it

**Kubernetes connection issues**
- Verify your kubeconfig context is valid: `kubectl --context=<context> get pods`
- Check that the service exists: `kubectl --context=<context> -n <namespace> get svc <service>`

### Logs

For more detailed logs, run the daemon with debug logging:

```bash
kubeportal daemon start --verbosity debug
```

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## 📄 License

This project is licensed under the MIT License - see the LICENSE file for details.
