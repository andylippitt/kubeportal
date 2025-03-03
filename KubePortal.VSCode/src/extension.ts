// src/extension.ts
import * as vscode from 'vscode';
import { KubePortalClient } from './kubeportalClient';
import { KubePortalTreeProvider } from './kubeportalTreeProvider';
import { execSync, spawn } from 'child_process';
import { existsSync } from 'fs';
import { resolve } from 'path';

let statusBarItem: vscode.StatusBarItem;
let client: KubePortalClient;
let treeProvider: KubePortalTreeProvider;

export async function activate(context: vscode.ExtensionContext) {
  console.log('KubePortal extension is now active!');
  
  // Create status bar item
  statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
  statusBarItem.command = 'kubeportal.toggleDaemon';
  context.subscriptions.push(statusBarItem);

  // Initialize client
  const config = vscode.workspace.getConfiguration('kubeportal');
  const port = config.get<number>('daemonPort') || 50051;
  client = new KubePortalClient(port);

  // Initialize tree view
  treeProvider = new KubePortalTreeProvider(client);
  const treeView = vscode.window.createTreeView('kubeportalView', {
    treeDataProvider: treeProvider,
    showCollapseAll: true
  });
  context.subscriptions.push(treeView);

  // Register commands
  context.subscriptions.push(
    vscode.commands.registerCommand('kubeportal.toggleDaemon', () => toggleDaemon()),
    vscode.commands.registerCommand('kubeportal.startDaemon', () => startDaemon()),
    vscode.commands.registerCommand('kubeportal.stopDaemon', () => stopDaemon()),
    vscode.commands.registerCommand('kubeportal.refreshForwards', () => treeProvider.refresh()),
    vscode.commands.registerCommand('kubeportal.createForward', () => createForward()),
    vscode.commands.registerCommand('kubeportal.startForward', (item) => startForward(item.label)),
    vscode.commands.registerCommand('kubeportal.stopForward', (item) => stopForward(item.label)),
    vscode.commands.registerCommand('kubeportal.deleteForward', (item) => deleteForward(item.label)),
    vscode.commands.registerCommand('kubeportal.enableGroup', (item) => enableGroup(item.label)),
    vscode.commands.registerCommand('kubeportal.disableGroup', (item) => disableGroup(item.label))
  );

  // Auto-start daemon if configured
  if (config.get<boolean>('autoStartDaemon')) {
    await startDaemon();
  } else {
    updateStatusBar(false);
  }

  // Start polling for daemon status
  pollDaemonStatus();
}

export function deactivate() {
  // Cleanup if needed
}

async function pollDaemonStatus() {
  try {
    const running = await client.isDaemonRunning();
    updateStatusBar(running);
    
    // Set interval only if not already set
    setTimeout(pollDaemonStatus, 5000);
  } catch (error) {
    console.error('Error polling daemon status:', error);
    updateStatusBar(false);
    
    // Retry after a delay
    setTimeout(pollDaemonStatus, 10000);
  }
}

function updateStatusBar(running: boolean) {
  if (running) {
    statusBarItem.text = '$(plug) KubePortal: Running';
    statusBarItem.backgroundColor = undefined;
    statusBarItem.tooltip = 'KubePortal daemon is running. Click to stop.';
  } else {
    statusBarItem.text = '$(circle-slash) KubePortal: Stopped';
    statusBarItem.backgroundColor = new vscode.ThemeColor('statusBarItem.warningBackground');
    statusBarItem.tooltip = 'KubePortal daemon is not running. Click to start.';
  }
  statusBarItem.show();
}

async function toggleDaemon() {
  const running = await client.isDaemonRunning();
  if (running) {
    await stopDaemon();
  } else {
    await startDaemon();
  }
}

async function startDaemon() {
  try {
    vscode.window.showInformationMessage('Starting KubePortal daemon...');
    
    const config = vscode.workspace.getConfiguration('kubeportal');
    const port = config.get<number>('daemonPort') || 50051;
    let binaryPath = config.get<string>('binaryPath');
    
    if (!binaryPath) {
      // Try to auto-detect binary
      binaryPath = await findKubePortalBinary();
    }
    
    if (!binaryPath) {
      vscode.window.showErrorMessage('KubePortal binary not found. Please install it or set the path in settings.');
      return;
    }
    
    // Start the daemon process
    const process = spawn(binaryPath, ['daemon', 'start', '--api-port', port.toString()], {
      detached: true,
      stdio: 'ignore'
    });
    
    // Don't wait for the process to exit
    process.unref();
    
    // Wait for daemon to start
    let attempts = 0;
    const maxAttempts = 10;
    
    while (attempts < maxAttempts) {
      await new Promise(resolve => setTimeout(resolve, 500));
      
      if (await client.isDaemonRunning()) {
        updateStatusBar(true);
        treeProvider.refresh();
        vscode.window.showInformationMessage('KubePortal daemon started successfully');
        return;
      }
      
      attempts++;
    }
    
    vscode.window.showErrorMessage('Failed to start KubePortal daemon');
    updateStatusBar(false);
  } catch (error) {
    vscode.window.showErrorMessage(`Error starting KubePortal daemon: ${error}`);
    updateStatusBar(false);
  }
}

async function stopDaemon() {
  try {
    if (!await client.isDaemonRunning()) {
      vscode.window.showInformationMessage('KubePortal daemon is not running');
      updateStatusBar(false);
      return;
    }
    
    vscode.window.showInformationMessage('Stopping KubePortal daemon...');
    
    await client.shutdown();
    
    // Wait for daemon to stop
    let attempts = 0;
    const maxAttempts = 10;
    
    while (attempts < maxAttempts) {
      await new Promise(resolve => setTimeout(resolve, 500));
      
      if (!await client.isDaemonRunning()) {
        updateStatusBar(false);
        treeProvider.refresh();
        vscode.window.showInformationMessage('KubePortal daemon stopped successfully');
        return;
      }
      
      attempts++;
    }
    
    vscode.window.showErrorMessage('Failed to stop KubePortal daemon');
  } catch (error) {
    // Error is expected when daemon is shut down
    updateStatusBar(false);
    treeProvider.refresh();
    vscode.window.showInformationMessage('KubePortal daemon stopped successfully');
  }
}

async function findKubePortalBinary(): Promise<string | undefined> {
  try {
    // Try to find in PATH
    const path = execSync('which kubeportal').toString().trim();
    if (existsSync(path)) {
      return path;
    }
  } catch (error) {
    // Not found in PATH, try common locations
    const possibleLocations = [
      '/usr/local/bin/kubeportal',
      '/usr/bin/kubeportal',
      resolve(process.env.HOME || '', '.local/bin/kubeportal')
    ];
    
    for (const location of possibleLocations) {
      if (existsSync(location)) {
        return location;
      }
    }
  }
  
  return undefined;
}

async function createForward() {
  // Create quick picks for the forward type
  const forwardType = await vscode.window.showQuickPick(
    [
      { label: 'Socket Forward', description: 'Forward to a TCP socket' },
      { label: 'Kubernetes Forward', description: 'Forward to a Kubernetes service' }
    ],
    { placeHolder: 'Select forward type' }
  );
  
  if (!forwardType) {
    return;
  }
  
  // Common inputs
  const name = await vscode.window.showInputBox({ prompt: 'Enter a name for the forward' });
  if (!name) return;
  
  const group = await vscode.window.showInputBox({ prompt: 'Enter group name', value: 'default' });
  if (!group) return;
  
  const localPortStr = await vscode.window.showInputBox({ prompt: 'Enter local port' });
  if (!localPortStr) return;
  const localPort = parseInt(localPortStr, 10);
  if (isNaN(localPort) || localPort <= 0 || localPort > 65535) {
    vscode.window.showErrorMessage('Invalid port number');
    return;
  }
  
  // Type-specific inputs
  if (forwardType.label === 'Socket Forward') {
    const remoteHost = await vscode.window.showInputBox({ prompt: 'Enter remote host' });
    if (!remoteHost) return;
    
    const remotePortStr = await vscode.window.showInputBox({ prompt: 'Enter remote port' });
    if (!remotePortStr) return;
    const remotePort = parseInt(remotePortStr, 10);
    if (isNaN(remotePort) || remotePort <= 0 || remotePort > 65535) {
      vscode.window.showErrorMessage('Invalid port number');
      return;
    }
    
    // Create socket forward
    try {
      const result = await client.createSocketForward(name, group, localPort, remoteHost, remotePort);
      if (result.success) {
        vscode.window.showInformationMessage(`Socket forward '${name}' created successfully`);
        treeProvider.refresh();
      } else {
        vscode.window.showErrorMessage(`Failed to create forward: ${result.error}`);
      }
    } catch (error) {
      vscode.window.showErrorMessage(`Error creating forward: ${error}`);
    }
  } else {
    // Kubernetes forward
    const context = await vscode.window.showInputBox({ prompt: 'Enter Kubernetes context' });
    if (!context) return;
    
    const namespace = await vscode.window.showInputBox({ prompt: 'Enter Kubernetes namespace' });
    if (!namespace) return;
    
    const service = await vscode.window.showInputBox({ prompt: 'Enter service name' });
    if (!service) return;
    
    const servicePortStr = await vscode.window.showInputBox({ prompt: 'Enter service port' });
    if (!servicePortStr) return;
    const servicePort = parseInt(servicePortStr, 10);
    if (isNaN(servicePort) || servicePort <= 0 || servicePort > 65535) {
      vscode.window.showErrorMessage('Invalid port number');
      return;
    }
    
    // Create Kubernetes forward
    try {
      const result = await client.createKubernetesForward(
        name, group, localPort, context, namespace, service, servicePort
      );
      if (result.success) {
        vscode.window.showInformationMessage(`Kubernetes forward '${name}' created successfully`);
        treeProvider.refresh();
      } else {
        vscode.window.showErrorMessage(`Failed to create forward: ${result.error}`);
      }
    } catch (error) {
      vscode.window.showErrorMessage(`Error creating forward: ${error}`);
    }
  }
}

async function startForward(name: string) {
  try {
    const result = await client.startForward(name);
    if (result.success) {
      vscode.window.showInformationMessage(`Forward '${name}' started successfully`);
      treeProvider.refresh();
    } else {
      vscode.window.showErrorMessage(`Failed to start forward: ${result.error}`);
    }
  } catch (error) {
    vscode.window.showErrorMessage(`Error starting forward: ${error}`);
  }
}

async function stopForward(name: string) {
  try {
    const result = await client.stopForward(name);
    if (result.success) {
      vscode.window.showInformationMessage(`Forward '${name}' stopped successfully`);
      treeProvider.refresh();
    } else {
      vscode.window.showErrorMessage(`Failed to stop forward: ${result.error}`);
    }
  } catch (error) {
    vscode.window.showErrorMessage(`Error stopping forward: ${error}`);
  }
}

async function deleteForward(name: string) {
  // Confirm deletion
  const confirmed = await vscode.window.showWarningMessage(
    `Are you sure you want to delete forward '${name}'?`,
    'Yes',
    'No'
  );
  
  if (confirmed !== 'Yes') {
    return;
  }
  
  try {
    const result = await client.deleteForward(name);
    if (result.success) {
      vscode.window.showInformationMessage(`Forward '${name}' deleted successfully`);
      treeProvider.refresh();
    } else {
      vscode.window.showErrorMessage(`Failed to delete forward: ${result.error}`);
    }
  } catch (error) {
    vscode.window.showErrorMessage(`Error deleting forward: ${error}`);
  }
}

async function enableGroup(name: string) {
  try {
    const result = await client.enableGroup(name);
    if (result.success) {
      vscode.window.showInformationMessage(`Group '${name}' enabled successfully`);
      treeProvider.refresh();
    } else {
      vscode.window.showErrorMessage(`Failed to enable group: ${result.error}`);
    }
  } catch (error) {
    vscode.window.showErrorMessage(`Error enabling group: ${error}`);
  }
}

async function disableGroup(name: string) {
  try {
    const result = await client.disableGroup(name);
    if (result.success) {
      vscode.window.showInformationMessage(`Group '${name}' disabled successfully`);
      treeProvider.refresh();
    } else {
      vscode.window.showErrorMessage(`Failed to disable group: ${result.error}`);
    }
  } catch (error) {
    vscode.window.showErrorMessage(`Error disabling group: ${error}`);
  }
}