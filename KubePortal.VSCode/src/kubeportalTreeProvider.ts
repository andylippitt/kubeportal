// src/kubeportalTreeProvider.ts
import * as vscode from 'vscode';
import { KubePortalClient } from './kubeportalClient';

export class KubePortalTreeProvider implements vscode.TreeDataProvider<TreeItem> {
  private _onDidChangeTreeData: vscode.EventEmitter<TreeItem | undefined | null | void> = new vscode.EventEmitter<TreeItem | undefined | null | void>();
  readonly onDidChangeTreeData: vscode.Event<TreeItem | undefined | null | void> = this._onDidChangeTreeData.event;
  
  constructor(private client: KubePortalClient) {}
  
  refresh(): void {
    this._onDidChangeTreeData.fire();
  }
  
  getTreeItem(element: TreeItem): vscode.TreeItem {
    return element;
  }
  
  async getChildren(element?: TreeItem): Promise<TreeItem[]> {
    // Check if daemon is running
    const isRunning = await this.client.isDaemonRunning();
    if (!isRunning) {
      return [new TreeItem('Daemon not running', 'message', vscode.TreeItemCollapsibleState.None)];
    }
    
    // Root level - return groups
    if (!element) {
      try {
        const groups = await this.client.listGroups();
        if (!groups || !groups.length) {
          return [new TreeItem('No groups found', 'message', vscode.TreeItemCollapsibleState.None)];
        }
        
        return groups.map((group: any) => {
          const state = vscode.TreeItemCollapsibleState.Collapsed;
          const contextValue = group.enabled ? 'group-enabled' : 'group-disabled';
          const item = new TreeItem(group.name, contextValue, state);
          item.description = `${group.active_forward_count}/${group.forward_count} active`;
          return item;
        });
      } catch (error) {
        console.error('Error getting groups:', error);
        return [new TreeItem(`Error: ${error}`, 'error', vscode.TreeItemCollapsibleState.None)];
      }
    }
    
    // Group level - return forwards in the group
    if (element.contextValue?.startsWith('group-')) {
      try {
        const result = await this.client.listForwards(element.label);
        const forwards = result.forwards;
        const statuses = result.statuses;
        
        if (!forwards || !forwards.length) {
          return [new TreeItem('No forwards in this group', 'message', vscode.TreeItemCollapsibleState.None)];
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
          
          return item;
        });
      } catch (error) {
        console.error('Error getting forwards:', error);
        return [new TreeItem(`Error: ${error}`, 'error', vscode.TreeItemCollapsibleState.None)];
      }
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
}

export class TreeItem extends vscode.TreeItem {
  constructor(
    public readonly label: string,
    public readonly contextValue: string,
    public readonly collapsibleState: vscode.TreeItemCollapsibleState
  ) {
    super(label, collapsibleState);
    this.contextValue = contextValue;
  }
}