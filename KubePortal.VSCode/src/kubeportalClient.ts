import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
import { resolve } from 'path';

// Path to your proto file
const PROTO_PATH = resolve(__dirname, '../../KubePortal/Grpc/kubeportal.proto ');

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

  constructor(private port: number) {
    // Connect to the daemon on localhost using the given port
    this.client = new kubeportalProto.KubePortalService(`localhost:${port}`, grpc.credentials.createInsecure());
  }

  // Check if the daemon is running by calling GetStatus
  isDaemonRunning(): Promise<boolean> {
    return new Promise((resolve, reject) => {
      this.client.GetStatus({}, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve(response.running);
      });
    });
  }

  // Shut down the daemon
  shutdown(): Promise<void> {
    return new Promise((resolve, reject) => {
      this.client.Shutdown({}, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        if (response.success) {
          resolve();
        } else {
          reject(new Error('Shutdown failed'));
        }
      });
    });
  }

  // List all groups
  listGroups(): Promise<any[]> {
    return new Promise((resolve, reject) => {
      this.client.ListGroups({}, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        // Assuming response.groups is an array of groups
        resolve(response.groups || []);
      });
    });
  }

  // List forwards for a given group (using group_filter in the request)
  listForwards(group: string): Promise<{ forwards: any[]; statuses: any[] }> {
    return new Promise((resolve, reject) => {
      this.client.ListForwards({ group_filter: group }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ forwards: response.forwards || [], statuses: response.statuses || [] });
      });
    });
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

    return new Promise((resolve, reject) => {
      this.client.CreateForward({ forward: forwardDefinition }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    });
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

    return new Promise((resolve, reject) => {
      this.client.CreateForward({ forward: forwardDefinition }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    });
  }

  // Start a forward
  startForward(name: string): Promise<{ success: boolean; error?: string }> {
    return new Promise((resolve, reject) => {
      this.client.StartForward({ name }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    });
  }

  // Stop a forward
  stopForward(name: string): Promise<{ success: boolean; error?: string }> {
    return new Promise((resolve, reject) => {
      this.client.StopForward({ name }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    });
  }

  // Delete a forward
  deleteForward(name: string): Promise<{ success: boolean; error?: string }> {
    return new Promise((resolve, reject) => {
      this.client.DeleteForward({ name }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    });
  }

  // Enable a group
  enableGroup(name: string): Promise<{ success: boolean; error?: string }> {
    return new Promise((resolve, reject) => {
      this.client.EnableGroup({ name }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    });
  }

  // Disable a group
  disableGroup(name: string): Promise<{ success: boolean; error?: string }> {
    return new Promise((resolve, reject) => {
      this.client.DisableGroup({ name }, (err: any, response: any) => {
        if (err) {
          return reject(err);
        }
        resolve({ success: response.success, error: response.error });
      });
    });
  }
}
