import { NodeSchedule } from './node-schedule';
export type NodeRole = 'server' | 'worker';
export interface NodeToolCapability {
  readonly encoders?: readonly string[] | null;
  readonly encoderChecks?: readonly NodeEncoderCapability[] | null;
  readonly toolId: string;
  readonly version: string;
  readonly mediaTypes: readonly string[];
  readonly accelerationTypes: readonly string[];
}
export interface NodeEncoderCapability {
  readonly encoderId: string;
  readonly status: 'usable' | 'detected';
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
  readonly flowName?: string | null;
  readonly flowVersion?: number | null;
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

export interface NodeActivityResponse {
  readonly active: number;
  readonly succeeded: number;
  readonly failed: number;
  readonly cancelled: number;
  readonly lastActivity: string | null;
  readonly sourceTasks: number;
}

export interface NodeProcessingSettings {
  readonly maxConcurrentTasks: number | null;
  readonly effectiveMaxConcurrentTasks: number;
  readonly revision: number;
  readonly schedule: NodeSchedule;
}
