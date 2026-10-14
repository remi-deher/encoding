export type ExecutionState =
  'queued' | 'running' | 'cancelling' | 'succeeded' | 'failed' | 'cancelled' | 'deferred';
export interface ExecutionResponse {
  readonly id: string;
  readonly media: string;
  readonly library: string;
  readonly flow: string;
  readonly flowVersion: number;
  readonly state: ExecutionState;
  readonly nodeId: string | null;
  readonly node: string | null;
  readonly progress: number | null;
  readonly detail: string;
  readonly attempt: number;
  readonly revision: number;
  readonly queuedAt: string;
  readonly startedAt: string | null;
  readonly finishedAt: string | null;
  readonly beforeMetadata?: {
    readonly format?: { readonly size?: string };
    readonly streams?: readonly unknown[];
  } | null;
  readonly afterMetadata?: {
    readonly format?: { readonly size?: string };
    readonly streams?: readonly unknown[];
  } | null;
  readonly publishedPath?: string | null;
  readonly updatedAt: string;
}
export interface ExecutionEventResponse {
  readonly id: string;
  readonly revision: number;
  readonly state: ExecutionState;
  readonly detail: string;
  readonly createdAt: string;
}
export interface ExecutionOverviewResponse {
  readonly executions: readonly ExecutionResponse[];
  readonly queuedCount: number;
  readonly activeCount: number;
  readonly failedCount: number;
  readonly cancelledCount: number;
  readonly succeededCount: number;
  readonly deferredCount?: number;
  readonly observedAt: string;
  readonly activity: readonly ExecutionActivityResponse[];
}
export const EXECUTION_STATE_LABELS: Record<ExecutionState, string> = {
  queued: 'En attente',
  running: 'En cours',
  cancelling: 'Annulation demandée',
  succeeded: 'Réussie',
  failed: 'Échec',
  cancelled: 'Annulée',
  deferred: 'Reportée',
};

export interface ExecutionActivityResponse {
  readonly id: string;
  readonly executionId: string;
  readonly media: string;
  readonly state: ExecutionState;
  readonly detail: string;
  readonly createdAt: string;
}
