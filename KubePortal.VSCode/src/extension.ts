// src/extension.ts
import * as vscode from 'vscode';
import { KubePortalClient } from './kubeportalClient';
import { KubePortalTreeProvider } from './kubeportalTreeProvider';
import { execSync, spawn } from 'child_process';
import { existsSync, readFile } from 'fs';
import { resolve } from 'path';

let statusBarItem: vscode.StatusBarItem;
let client: KubePortalClient;
let treeProvider: KubePortalTreeProvider;
let refreshTimer: NodeJS.Timer | null = null;

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
    vscode.commands.registerCommand('kubeportal.disableGroup', (item) => disableGroup(item.label)),
    vscode.commands.registerCommand('kubeportal.viewLogFile', () => viewLogFile())
  );

  // Auto-start daemon if configured
  if (config.get<boolean>('autoStartDaemon')) {
    await startDaemon();
  } else {
    updateStatusBar(false);
  }

  // Setup auto-refresh if enabled
  setupAutoRefresh();
  
  // Register for configuration changes
  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration(e => {
      if (e.affectsConfiguration('kubeportal.autoRefreshInterval')) {
        setupAutoRefresh();
      }
    })
  );

  // Register for window state changes to perform clean shutdown
  context.subscriptions.push(
    vscode.window.onDidChangeWindowState(async (e) => {
      if (!e.focused) {
        // Window lost focus, but we don't want to shut down the daemon
        // This is just an example of how to detect window state changes
      }
    })
  );
}

// Fix for deactivate() function to ensure it always returns a value
export function deactivate(): Promise<void> | undefined {
  if (refreshTimer) {
    clearInterval(refreshTimer);
    refreshTimer = null;
  }
  
  const config = vscode.workspace.getConfiguration('kubeportal');
  const shutdownOnExit = config.get<boolean>('shutdownOnExit') || false;
  
  if (shutdownOnExit) {
    // Try to gracefully shut down the daemon
    return new Promise<void>(async (resolve) => {
      try {
        if (await client.isDaemonRunning()) {
          await client.shutdown();
          // Wait a bit for shutdown to complete
          setTimeout(() => {
            resolve();
          }, 1000);
        } else {
          resolve();
        }
      } catch (error) {
        console.error('Error during shutdown:', error);
        resolve();
      }
    });
  }
  
  return undefined; // Explicitly return undefined when not shutting down
}

function setupAutoRefresh() {
  // Clear existing timer if any
  if (refreshTimer) {
    clearInterval(refreshTimer);
    refreshTimer = null;
  }
  
  // Get refresh interval from settings
  const config = vscode.workspace.getConfiguration('kubeportal');
  const interval = config.get<number>('autoRefreshInterval') || 0;
  
  // Only set up if interval is positive
  if (interval > 0) {
    refreshTimer = setInterval(async () => {
      const running = await client.isDaemonRunning();
      updateStatusBar(running);
      
      // Only refresh tree view if daemon is running
      if (running) {
        treeProvider.refresh();
      }
    }, interval);
  }
}

function shouldNotify(level: string): boolean {
  const config = vscode.workspace.getConfiguration('kubeportal');
  const notificationLevel = config.get<string>('notificationLevel') || 'All';
  
  switch (notificationLevel) {
    case 'None':
      return false;
    case 'Error':
      return level === 'error';
    case 'All':
    default:
      return true;
  }
}

function showNotification(message: string, level: 'info' | 'error' | 'warning' = 'info') {
  if (!shouldNotify(level)) {
    return;
  }
  
  switch (level) {
    case 'error':
      vscode.window.showErrorMessage(message);
      break;
    case 'warning':
      vscode.window.showWarningMessage(message);
      break;
    default:
      vscode.window.showInformationMessage(message);
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

async function toggleDaemon(): Promise<boolean> {
  try {
    const running = await client.isDaemonRunning();
    if (running) {
      await stopDaemon();
      return false;
    } else {
      return await startDaemon();
    }
  } catch (error: any) {
    console.error('Error toggling daemon:', error);
    showNotification(`Error toggling daemon: ${error.message}`, 'error');
    updateStatusBar(false);
    return false;
  }
}

// Fix for two error typing issues in catch blocks

async function startDaemon(): Promise<boolean> {
  try {
    // Check if already running
    if (await client.isDaemonRunning()) {
      updateStatusBar(true);
      showNotification('KubePortal daemon is already running');
      return true;
    }
    
    statusBarItem.text = '$(sync~spin) KubePortal: Starting...';
    statusBarItem.show();
    
    showNotification('Starting KubePortal daemon...', 'info');
    
    const config = vscode.workspace.getConfiguration('kubeportal');
    const port = config.get<number>('daemonPort') || 50051;
    const logLevel = config.get<string>('logLevel') || 'Information';
    let binaryPath = config.get<string>('binaryPath');
    
    if (!binaryPath) {
      // Try to auto-detect binary
      binaryPath = await findKubePortalBinary();
    }
    
    if (!binaryPath) {
      const message = 'KubePortal binary not found. Please install it or set the path in settings.';
      showNotification(message, 'error');
      updateStatusBar(false);
      return false;
    }
    
    // Start the daemon process
    const process = spawn(binaryPath, ['daemon', 'start', '--api-port', port.toString(), '--verbosity', logLevel], {
      detached: true,
      stdio: 'ignore'
    });
    
    process.unref();
    
    // Wait for daemon to start with increased patience
    let attempts = 0;
    const maxAttempts = 20; // Increased from 10
    const delayBetweenAttempts = 500; // ms
    
    while (attempts < maxAttempts) {
      await new Promise(resolve => setTimeout(resolve, delayBetweenAttempts));
      attempts++;
      
      try {
        if (await client.isDaemonRunning()) {
          updateStatusBar(true);
          treeProvider.refresh();
          showNotification('KubePortal daemon started successfully', 'info');
          return true;
        }
      } catch (error: unknown) { // Add type annotation here
        // Just continue trying, but log the error
        console.log(`Attempt ${attempts}: ${error instanceof Error ? error.message : String(error)}`);
        
        // On the last few attempts, show more detailed progress
        if (attempts > maxAttempts - 3) {
          showNotification(`Still starting daemon (attempt ${attempts}/${maxAttempts})...`, 'info');
        }
      }
    }
    
    showNotification('Failed to start KubePortal daemon after multiple attempts. Check logs for details.', 'error');
    updateStatusBar(false);
    return false;
  } catch (error: unknown) {
    const errorMessage = `Error starting KubePortal daemon: ${error instanceof Error ? error.message : String(error)}`;
    showNotification(errorMessage, 'error');
    console.error(errorMessage, error);
    updateStatusBar(false);
    return false;
  }
}

async function stopDaemon() {
  try {
    if (!await client.isDaemonRunning()) {
      showNotification('KubePortal daemon is not running');
      updateStatusBar(false);
      return;
    }
    
    statusBarItem.text = '$(sync~spin) KubePortal: Stopping...';
    statusBarItem.show();
    
    showNotification('Stopping KubePortal daemon...');
    
    await client.shutdown();
    
    // Wait for daemon to stop with increased timeout
    let attempts = 0;
    const maxAttempts = 20; // Increased max attempts
    
    while (attempts < maxAttempts) {
      await new Promise(resolve => setTimeout(resolve, 500));
      
      if (!await client.isDaemonRunning()) {
        updateStatusBar(false);
        treeProvider.refresh();
        showNotification('KubePortal daemon stopped successfully');
        return;
      }
      
      attempts++;
    }
    
    showNotification('Failed to stop KubePortal daemon gracefully', 'warning');
    updateStatusBar(false);
  } catch (error: unknown) {
    console.error('Error stopping daemon:', error);
    // Even if there was an error, update the status bar
    updateStatusBar(false);
    treeProvider.refresh();
    const errorMessage = error instanceof Error ? error.message : String(error);
    showNotification(`KubePortal daemon stopped. ${errorMessage}`, 'info');
  }
}

// New function to view log file
async function viewLogFile(): Promise<void> {
  try {
    // Try to get log path from daemon if it's running
    let logPath = '';
    
    if (await client.isDaemonRunning()) {
      try {
        logPath = await client.getDaemonLogFilePath();
      } catch (error: any) {
        console.error('Error getting log path from daemon:', error);
      }
    }
    
    // If we couldn't get it from daemon, try the default location
    if (!logPath) {
      let homeDir = process.env.HOME || process.env.USERPROFILE;
      if (process.platform === 'win32') {
        logPath = `${process.env.LOCALAPPDATA}\\KubePortal\\daemon.log`;
      } else if (process.platform === 'darwin') {
        logPath = `${homeDir}/Library/Application Support/KubePortal/daemon.log`;
      } else {
        logPath = `${homeDir}/.kubeportal/daemon.log`;
      }
    }
    
    // Check if file exists
    if (!existsSync(logPath)) {
      showNotification(`Log file not found: ${logPath}`, 'error');
      return;
    }
    
    // Open the log file
    await vscode.commands.executeCommand('vscode.open', vscode.Uri.file(logPath));
  } catch (error: unknown) {
    console.error('Error opening log file:', error);
    const errorMessage = error instanceof Error ? error.message : String(error);
    showNotification(`Error opening log file: ${errorMessage}`, 'error');
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