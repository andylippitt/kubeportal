import * as vscode from 'vscode';
import { KubePortalClient } from './kubeportalClient';

export class KubePortalTreeProvider implements vscode.TreeDataProvider<TreeItem> {
  private _onDidChangeTreeData: vscode.EventEmitter<TreeItem | undefined | null | void> = new vscode.EventEmitter<TreeItem | undefined | null | void>();
  readonly onDidChangeTreeData: vscode.Event<TreeItem | undefined | null | void> = this._onDidChangeTreeData.event;
  
  // Connection history tracking
  private connectionHistory: Map<string, {
    startTime: Date;
    lastActive: Date;
    peakTransferRate: number;
    totalBytes: number;
  }> = new Map();

  constructor(private client: KubePortalClient) {}
  
  refresh(): void {
    this._onDidChangeTreeData.fire();
  }
  
  getTreeItem(element: TreeItem): vscode.TreeItem {
    return element;
  }
  
  async getChildren(element?: TreeItem): Promise<TreeItem[]> {
    // Check if daemon is running
    try {
      const isRunning = await this.client.isDaemonRunning();
      if (!isRunning) {
        const item = new TreeItem(
          'Daemon not running', 
          'daemon-stopped', 
          vscode.TreeItemCollapsibleState.None
        );
        item.tooltip = 'Start the KubePortal daemon to manage port forwards';
        item.command = {
          command: 'kubeportal.startDaemon',
          title: 'Start Daemon'
        };
        return [item];
      }
      
      // Root level - return groups
      if (!element) {
        try {
          const groups = await this.client.listGroups();
          if (!groups || !groups.length) {
            const item = new TreeItem(
              'No groups found', 
              'no-groups', 
              vscode.TreeItemCollapsibleState.None
            );
            item.tooltip = 'Create a new forward to get started';
            item.command = {
              command: 'kubeportal.createForward',
              title: 'Create Forward'
            };
            return [item];
          }
          
          return groups.map((group: any) => {
            const state = vscode.TreeItemCollapsibleState.Collapsed;
            const contextValue = group.enabled ? 'group-enabled' : 'group-disabled';
            const item = new TreeItem(group.name, contextValue, state);
            
            // Enhanced description with active/total counts
            item.description = `${group.active_forward_count}/${group.forward_count} active`;
            
            // Add tooltip with more details
            item.tooltip = new vscode.MarkdownString();
            item.tooltip.appendMarkdown(`**Group: ${group.name}**\n\n`);
            item.tooltip.appendMarkdown(`- Status: ${group.enabled ? 'Enabled' : 'Disabled'}\n`);
            item.tooltip.appendMarkdown(`- Active Forwards: ${group.active_forward_count}/${group.forward_count}\n`);
            
            // Add icon based on state
            if (group.enabled) {
              if (group.active_forward_count > 0) {
                item.iconPath = new vscode.ThemeIcon('folder-active', new vscode.ThemeColor('terminal.ansiGreen'));
              } else {
                item.iconPath = new vscode.ThemeIcon('folder-opened');
              }
            } else {
              item.iconPath = new vscode.ThemeIcon('folder', new vscode.ThemeColor('disabledForeground'));
            }
            
            return item;
          });
        } catch (error: any) {
          console.error('Error getting groups:', error);
          const errorItem = new TreeItem(
            `Error: ${this.formatError(error)}`, 
            'error', 
            vscode.TreeItemCollapsibleState.None
          );
          errorItem.tooltip = 'Failed to retrieve groups from daemon';
          errorItem.iconPath = new vscode.ThemeIcon('error');
          return [errorItem];
        }
      }
      
      // Group level - return forwards in the group
      if (element.contextValue?.startsWith('group-')) {
        try {
          const result = await this.client.listForwards(element.label);
          const forwards = result.forwards;
          const statuses = result.statuses;
          
          if (!forwards || !forwards.length) {
            const item = new TreeItem(
              'No forwards in this group', 
              'empty-group', 
              vscode.TreeItemCollapsibleState.None
            );
            item.tooltip = 'Add a forward to this group';
            item.command = {
              command: 'kubeportal.createForward',
              title: 'Create Forward'
            };
            return [item];
          }
          
          return forwards.map((forward: any, index: number) => {
            const status = statuses[index];
            const active = status.active;
            const contextValue = active ? 'forward-enabled' : 'forward-disabled';
            
            let description = `${forward.local_port}`;
            if (forward.type === 0) { // Socket
              description += ` → ${forward.remote_host}:${forward.remote_port}`;
            } else if (forward.type === 1) { // Kubernetes
              description += ` → ${forward.service}:${forward.service_port}`;
            }

            if (active) {
              description += ` (${this.formatBytes(status.bytes_transferred)})`;
              
              // Update connection history
              const now = new Date();
              const historyKey = forward.name;
              const existingHistory = this.connectionHistory.get(historyKey);
              
              if (existingHistory) {
                // Calculate transfer rate
                const timeDiff = (now.getTime() - existingHistory.lastActive.getTime()) / 1000;
                if (timeDiff > 0) {
                  const byteDiff = status.bytes_transferred - existingHistory.totalBytes;
                  const transferRate = byteDiff / timeDiff;
                  
                  // Update peak rate if needed
                  if (transferRate > existingHistory.peakTransferRate) {
                    existingHistory.peakTransferRate = transferRate;
                  }
                }
                
                // Update history
                existingHistory.lastActive = now;
                existingHistory.totalBytes = status.bytes_transferred;
              } else {
                // Create new history entry
                this.connectionHistory.set(historyKey, {
                  startTime: new Date(status.connected_time * 1000) || now,
                  lastActive: now,
                  peakTransferRate: 0,
                  totalBytes: status.bytes_transferred
                });
              }
            }
            
            const item = new TreeItem(forward.name, contextValue, vscode.TreeItemCollapsibleState.None);
            item.description = description;
            
            // Set icon based on state
            if (active) {
              item.iconPath = new vscode.ThemeIcon('play', new vscode.ThemeColor('terminal.ansiGreen'));
            } else if (forward.enabled) {
              item.iconPath = new vscode.ThemeIcon('circle-outline');
            } else {
              item.iconPath = new vscode.ThemeIcon('circle-slash', new vscode.ThemeColor('disabledForeground'));
            }
            
            // Add tooltip with more details
            item.tooltip = new vscode.MarkdownString();
            item.tooltip.appendMarkdown(`**Forward: ${forward.name}**\n\n`);
            item.tooltip.appendMarkdown(`- Status: ${active ? 'Active' : (forward.enabled ? 'Enabled' : 'Disabled')}\n`);
            item.tooltip.appendMarkdown(`- Local Port: ${forward.local_port}\n`);
            
            if (forward.type === 0) { // Socket
              item.tooltip.appendMarkdown(`- Remote: ${forward.remote_host}:${forward.remote_port}\n`);
              item.tooltip.appendMarkdown(`- Type: TCP Socket\n`);
            } else if (forward.type === 1) { // Kubernetes
              item.tooltip.appendMarkdown(`- Service: ${forward.service}:${forward.service_port}\n`);
              item.tooltip.appendMarkdown(`- Namespace: ${forward.namespace}\n`);
              item.tooltip.appendMarkdown(`- Context: ${forward.context}\n`);
              item.tooltip.appendMarkdown(`- Type: Kubernetes\n`);
            }
            
            if (active) {
              item.tooltip.appendMarkdown(`- Traffic: ${this.formatBytes(status.bytes_transferred)}\n`);
              if (status.connected_time) {
                item.tooltip.appendMarkdown(`- Connected: ${new Date(status.connected_time * 1000).toLocaleString()}\n`);
              }
            }
            
            // Add quick command to toggle state
            if (active) {
              item.command = {
                command: 'kubeportal.stopForward',
                title: 'Stop Forward',
                arguments: [item]
              };
            } else {
              item.command = {
                command: 'kubeportal.startForward',
                title: 'Start Forward',
                arguments: [item]
              };
            }
            
            // Store additional data for use in traffic indicators
            item.bytesTransferred = status.bytes_transferred;
            if (status.connected_time) {
              item.startTime = new Date(status.connected_time * 1000);
            }
            
            return item;
          });
        } catch (error: any) {
          console.error('Error getting forwards:', error);
          const errorItem = new TreeItem(
            `Error: ${this.formatError(error)}`, 
            'error', 
            vscode.TreeItemCollapsibleState.None
          );
          errorItem.tooltip = 'Failed to retrieve forwards for this group';
          errorItem.iconPath = new vscode.ThemeIcon('error');
          return [errorItem];
        }
      }
    } catch (error: any) {
      console.error('Error in tree provider:', error);
      const errorItem = new TreeItem(
        `Connection error: ${this.formatError(error)}`, 
        'error', 
        vscode.TreeItemCollapsibleState.None
      );
      errorItem.tooltip = 'Failed to connect to KubePortal daemon';
      errorItem.iconPath = new vscode.ThemeIcon('error');
      return [errorItem];
    }
    
    return [];
  }
  
  private formatBytes(bytes: number): string {
    if (!bytes || bytes <= 0) {
      return '0 B';
    }
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(1024));
    return parseFloat((bytes / Math.pow(1024, i)).toFixed(2)) + ' ' + units[i];
  }
  
  private formatDuration(ms: number): string {
    const seconds = Math.floor(ms / 1000);
    const minutes = Math.floor(seconds / 60);
    const hours = Math.floor(minutes / 60);
    
    if (hours > 0) {
      return `${hours}h ${minutes % 60}m`;
    } else if (minutes > 0) {
      return `${minutes}m ${seconds % 60}s`;
    } else {
      return `${seconds}s`;
    }
  }
  
  private formatError(error: any): string {
    if (typeof error === 'string') {
      return error;
    }
    
    if (error.code !== undefined) {
      // gRPC error
      switch (error.code) {
        case 14: return 'Connection failed (daemon not running)';
        case 4: return 'Deadline exceeded (daemon too busy)';
        default: return `gRPC error: ${error.details || error.message || error.code}`;
      }
    }
    
    return error.message || 'Unknown error';
  }
}

export class TreeItem extends vscode.TreeItem {
  bytesTransferred?: number;
  startTime?: Date;

  constructor(
    public readonly label: string,
    public readonly contextValue: string,
    public readonly collapsibleState: vscode.TreeItemCollapsibleState
  ) {
    super(label, collapsibleState);
    this.contextValue = contextValue;
  }
}
