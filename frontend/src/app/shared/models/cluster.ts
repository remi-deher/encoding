export type NodeRole = 'server' | 'worker';
export interface NodeToolCapability {
  readonly toolId: string;
  readonly version: string;
  readonly mediaTypes: readonly string[];
  readonly accelerationTypes: readonly string[];
}
export interface NodeResponse {
  readonly id: string;
  readonly name: string;
  readonly roles: readonly NodeRole[];
  readonly maxConcurrentTasks: number;
  readonly capabilities: readonly NodeToolCapability[];
  readonly status: 'online' | 'offline';
  readonly lastSeenAt: string;
}
export interface LibraryResponse {
  readonly id: string;
  readonly name: string;
  readonly createdAt: string;
  readonly configuredNodeCount: number;
  readonly directAccessNodeCount: number;
  readonly transferNodeCount: number;
  readonly isActive: boolean;
}
export const NODE_ROLE_LABELS: Readonly<Record<NodeRole, string>> = {
  server: 'Serveur',
  worker: 'Worker',
};
