import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
import { resolve } from 'path';
import * as vscode from 'vscode';

// Path to your proto file
const PROTO_PATH = resolve(__dirname, "../proto/kubeportal.proto");

// Load the proto file with options
const packageDefinition = protoLoader.loadSync(PROTO_PATH, {
  keepCase: true,
  longs: String,
  enums: String,
  defaults: true,
  oneofs: true,
});

// Load the package definition; adjust the type as needed
const kubeportalProto = grpc.loadPackageDefinition(packageDefinition).kubeportal as any;

// Enum mapping for ForwardType (should match your proto definitions)
enum ForwardType {
  SOCKET = 0,
  KUBERNETES = 1,
}

export class KubePortalClient {
  private client: any;
  private connectionRetryCount = 0;
  private readonly MAX_RETRIES = 5;

  constructor(private port: number) {
    this.initializeClient();
  }

  private initializeClient() {
    // Connect to the daemon on localhost using the given port
    this.client = new kubeportalProto.KubePortalService(`localhost:${this.port}`, grpc.credentials.createInsecure(), {
      // Setting timeouts for gRPC calls to be more tolerant
      'grpc.keepalive_timeout_ms': 10000,
      'grpc.keepalive_time_ms': 30000,
      'grpc.http2.max_pings_without_data': 0,
      'grpc.http2.min_time_between_pings_ms': 10000,
    });
  }

  private async withRetry<T>(operation: () => Promise<T>): Promise<T> {
    try {
      return await operation();
    } catch (err: any) {
      // Check if we should retry based on error and retry count
      if (this.connectionRetryCount < this.MAX_RETRIES && 
         (err.code === grpc.status.UNAVAILABLE || err.code === grpc.status.DEADLINE_EXCEEDED)) {
        this.connectionRetryCount++;
        
        // Calculate backoff time (exponential with jitter)
        const backoffMs = Math.min(1000 * Math.pow(2, this.connectionRetryCount) + Math.random() * 1000, 10000);
        
        console.log(`Connection error (${err.code}). Retrying in ${backoffMs}ms... (${this.connectionRetryCount}/${this.MAX_RETRIES})`);
        
        // Wait and retry
        await new Promise(resolve => setTimeout(resolve, backoffMs));
        
        // Reinitialize the client
        this.initializeClient();
        
        // Try again
        return this.withRetry(operation);
      }
      
      // Reset retry count if the error is something other than connection issues
      if (err.code !== grpc.status.UNAVAILABLE && err.code !== grpc.status.DEADLINE_EXCEEDED) {
        this.connectionRetryCount = 0;
      }
      
      throw err;
    }
  }

  // Check if the daemon is running by calling GetStatus
  async isDaemonRunning(): Promise<boolean> {
    try {
      return await this.withRetry(() => new Promise((resolve, reject) => {
        // Set a timeout to handle hanging gRPC calls
        const timeout = setTimeout(() => {
          reject(new Error('Timeout waiting for daemon response'));
        }, 3000);
        
        this.client.GetStatus({}, { deadline: Date.now() + 3000 }, (err: any, response: any) => {
          clearTimeout(timeout);
          if (err) {
            return reject(err);
          }
          // Reset retry count on successful call
          this.connectionRetryCount = 0;
          resolve(response.running);
        });
      }));
    } catch (err) {
      console.log(`Error checking daemon status: ${err}`);
      return false;
    }
  }

  // Shut down the daemon
  shutdown(): Promise<void> {
    return this.withRetry(() => new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        resolve(); // Resolve anyway on timeout - daemon might be already down
      }, 5000);
      
      this.client.Shutdown({}, { deadline: Date.now() + 5000 }, (err: any, response: any) => {
        clearTimeout(timeout);
        if (err) {
          // Special case: consider a connection error as "daemon stopped"
          if (err.code === grpc.status.UNAVAILABLE) {
            return resolve();
          }
          return reject(err);
        }
        if (response.success) {
          resolve();
        } else {
          reject(new Error('Shutdown failed'));
        }
      });
    }));
  }

  // List all groups
  listGroups(): Promise<any[]> {
    return this.withRetry(() => new Promise((resolve, reject) => {
      this.client.ListGroups({}, { deadline: Date.now() + 5000 }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        // Reset retry count
        this.connectionRetryCount = 0;
        // Assuming response.groups is an array of groups
        resolve(response.groups || []);
      });
    }));
  }

  // List forwards for a given group (using group_filter in the request)
  listForwards(group: string): Promise<{ forwards: any[]; statuses: any[] }> {
    return this.withRetry(() => new Promise((resolve, reject) => {
      this.client.ListForwards({ group_filter: group }, { deadline: Date.now() + 5000 }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ forwards: response.forwards || [], statuses: response.statuses || [] });
      });
    }));
  }

  // Create a socket forward using CreateForward RPC
  createSocketForward(name: string, group: string, localPort: number, remoteHost: string, remotePort: number): Promise<{ success: boolean; error?: string }> {
    const forwardDefinition = {
      name,
      group,
      local_port: localPort,
      enabled: true,
      type: ForwardType.SOCKET,
      remote_host: remoteHost,
      remote_port: remotePort,
    };

    return this.withRetry(() => new Promise((resolve, reject) => {
      this.client.CreateForward({ forward: forwardDefinition }, { deadline: Date.now() + 5000 }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    }));
  }

  // Create a Kubernetes forward using CreateForward RPC
  createKubernetesForward(name: string, group: string, localPort: number, context: string, namespace: string, service: string, servicePort: number): Promise<{ success: boolean; error?: string }> {
    const forwardDefinition = {
      name,
      group,
      local_port: localPort,
      enabled: true,
      type: ForwardType.KUBERNETES,
      context,
      namespace,
      service,
      service_port: servicePort,
    };

    return this.withRetry(() => new Promise((resolve, reject) => {
      this.client.CreateForward({ forward: forwardDefinition }, { deadline: Date.now() + 5000 }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    }));
  }

  // Start a forward
  startForward(name: string): Promise<{ success: boolean; error?: string }> {
    return this.withRetry(() => new Promise((resolve, reject) => {
      this.client.StartForward({ name }, { deadline: Date.now() + 5000 }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    }));
  }

  // Stop a forward
  stopForward(name: string): Promise<{ success: boolean; error?: string }> {
    return this.withRetry(() => new Promise((resolve, reject) => {
      this.client.StopForward({ name }, { deadline: Date.now() + 5000 }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    }));
  }

  // Delete a forward
  deleteForward(name: string): Promise<{ success: boolean; error?: string }> {
    return this.withRetry(() => new Promise((resolve, reject) => {
      this.client.DeleteForward({ name }, { deadline: Date.now() + 5000 }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    }));
  }

  // Enable a group
  enableGroup(name: string): Promise<{ success: boolean; error?: string }> {
    return this.withRetry(() => new Promise((resolve, reject) => {
      this.client.EnableGroup({ name }, { deadline: Date.now() + 5000 }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    }));
  }

  // Disable a group
  disableGroup(name: string): Promise<{ success: boolean; error?: string }> {
    return this.withRetry(() => new Promise((resolve, reject) => {
      this.client.DisableGroup({ name }, { deadline: Date.now() + 5000 }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    }));
  }

  // New method to get the daemon log file path
  getDaemonLogFilePath(): Promise<string> {
    return this.withRetry(() => new Promise((resolve, reject) => {
      this.client.GetLogFilePath({}, { deadline: Date.now() + 3000 }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve(response.path || '');
      });
    }));
  }
}
