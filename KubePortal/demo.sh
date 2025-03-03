#!/bin/bash
# KubePortal Demo Script

set -e  # Exit on error

# Helper functions
print_header() {
  echo -e "\n\033[1;36m===== $1 =====\033[0m\n"
  sleep 1
}

run_command() {
  echo -e "\033[1;33m$ $1\033[0m"
  eval $1
  sleep 1.5
}

# Define the command to use (can be changed for development vs installed)
CMD="dotnet run --"

# Ensure clean start
print_header "Stopping daemon if it's running"
run_command "$CMD daemon stop || true"

# Start the demo
print_header "Starting KubePortal daemon"
run_command "$CMD daemon start"
run_command "$CMD daemon status"

# Create some forwards
print_header "Creating example port forwards"

# Socket forward example
run_command "$CMD forward create \
  --name example-web \
  --local-port 8000 \
  --type socket \
  --remote-host example.com \
  --remote-port 80"

# Kubernetes forward example (assumes you have a Kubernetes context)
# Comment this out if you don't have Kubernetes available
K8S_CONTEXT=$(kubectl config current-context 2>/dev/null || echo "")
if [ -n "$K8S_CONTEXT" ]; then
  run_command "$CMD forward create \
    --name nginx \
    --local-port 8080 \
    --type kubernetes \
    --context $K8S_CONTEXT \
    --namespace default \
    --service nginx \
    --service-port 80 \
    --group 'k8s-services'"
fi

# Create more socket forwards with different groups
run_command "$CMD forward create \
  --name google \
  --local-port 8001 \
  --type socket \
  --remote-host google.com \
  --remote-port 80 \
  --group 'external-sites'"

run_command "$CMD forward create \
  --name github \
  --local-port 8002 \
  --type socket \
  --remote-host github.com \
  --remote-port 443 \
  --group 'external-sites'"

# List all forwards
print_header "Listing all forwards"
run_command "$CMD forward list"

# Start a forward
print_header "Starting and testing a forward"
run_command "$CMD forward start example-web"
run_command "curl -s -I http://localhost:8000 | head -n1"

# Show more details
print_header "Checking forward status after use"
run_command "$CMD forward list"

# Group operations
print_header "Working with groups"
run_command "$CMD group list"
run_command "$CMD group enable external-sites"
run_command "$CMD group disable external-sites"

# Export config - use single quotes to properly handle command substitution
print_header "Exporting configuration"
run_command "$CMD export > kubeportal-config.json"
run_command "cat kubeportal-config.json | jq"

# Apply modified config
print_header "Applying modified configuration"
cat > modified-config.json << EOF
{
  "forwards": {
    "example-web": {
      "type": "socket",
      "name": "example-web",
      "group": "web-services",
      "localPort": 8000,
      "enabled": true,
      "remoteHost": "example.com",
      "remotePort": 80
    },
    "new-service": {
      "type": "socket",
      "name": "new-service",
      "group": "web-services",
      "localPort": 9000,
      "enabled": false,
      "remoteHost": "example.org",
      "remotePort": 80
    }
  }
}
EOF
run_command "cat modified-config.json | jq"
run_command "$CMD apply --file modified-config.json --remove-missing"

# Check the results
print_header "Checking the results"
run_command "$CMD forward list"
run_command "$CMD group list"

# Verify JSON output mode
print_header "JSON output mode"
run_command "$CMD forward list --json | jq"
run_command "$CMD daemon status --json | jq"

# Clean up and stop daemon
print_header "Cleaning up and stopping daemon"
run_command "$CMD daemon stop"
run_command "rm -f kubeportal-config.json modified-config.json"

print_header "Demo completed!"
echo -e "This demo showcased the main features of KubePortal."
echo -e "For more information, see the README.md file."
