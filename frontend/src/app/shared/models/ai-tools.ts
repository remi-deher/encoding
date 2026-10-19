import { FlowDocument, FlowValidationIssue } from './flow-validation';

export interface DeviceAuthPrompt {
  readonly userCode: string;
  readonly verificationUri: string;
  readonly expiresInSeconds: number;
  readonly intervalSeconds: number;
}

export interface DeviceAuthStatus {
  readonly toolId: string;
  readonly state: 'unconfigured' | 'pending' | 'authorized' | 'expired' | 'failed';
  readonly accountName?: string | null;
  readonly authenticatedAt?: string | null;
  readonly errorMessage?: string | null;
}

export interface DeviceAuthStartResponse {
  readonly toolId: string;
  readonly prompt: DeviceAuthPrompt;
}

export interface AiToolInfo {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly category: string;
  readonly isInstalled: boolean;
  readonly authStatus: DeviceAuthStatus;
  readonly capabilities: readonly string[];
}

export interface FlowAiGenerateRequest {
  readonly mediaType: string;
  readonly userPrompt: string;
  readonly provider?: string | null;
  readonly sessionId?: string | null;
  readonly requestId?: string | null;
}

export interface FlowAiRefactorRequest {
  readonly currentFlow: FlowDocument;
  readonly userPrompt: string;
  readonly provider?: string | null;
  readonly sessionId?: string | null;
  readonly requestId?: string | null;
}

export interface FlowAiExplainRequest {
  readonly flow: FlowDocument;
  readonly provider?: string | null;
  readonly sessionId?: string | null;
  readonly requestId?: string | null;
}

export interface FlowAiResponse {
  readonly success: boolean;
  readonly sessionId?: string | null;
  readonly requestId?: string | null;
  readonly flow?: FlowDocument | null;
  readonly explanation?: string | null;
  readonly validationIssues?: readonly FlowValidationIssue[] | null;
}

export interface AiSettings {
  readonly defaultProvider: string;
  readonly ollamaBaseUrl: string;
  readonly ollamaModel: string;
  readonly openAiBaseUrl: string;
  readonly openAiModel: string;
  readonly openAiApiKey?: string | null;
  readonly hasOpenAiApiKey?: boolean;
  readonly timeoutSeconds: number;
  readonly subtitleBatchSize: number;
}

export interface AiSettingsUpdateRequest {
  readonly defaultProvider?: string;
  readonly ollamaBaseUrl?: string;
  readonly ollamaModel?: string;
  readonly openAiBaseUrl?: string;
  readonly openAiModel?: string;
  readonly openAiApiKey?: string | null;
  readonly timeoutSeconds?: number;
  readonly subtitleBatchSize?: number;
}

export interface AiNodeAvailability {
  readonly nodeId: string;
  readonly name: string;
  readonly providers: readonly string[];
  readonly reachable: boolean;
}
export interface AiClusterView {
  readonly nodes: readonly AiNodeAvailability[];
}

export interface AiUsage {
  readonly inputTokens?: number | null;
  readonly outputTokens?: number | null;
  readonly cachedInputTokens?: number | null;
  readonly estimatedCostUsd?: number | null;
  readonly model?: string | null;
}
export interface AiSessionScope {
  readonly operation: string;
  readonly libraryId?: string | null;
  readonly flowId?: string | null;
  readonly mediaId?: string | null;
  readonly executionId?: string | null;
}
export interface AiSessionSummary {
  readonly id: string;
  readonly provider: string;
  readonly model?: string | null;
  readonly scope?: AiSessionScope | null;
  readonly status: 'completed' | 'running' | 'awaitingRetry';
  readonly turnCount: number;
  readonly attempt: number;
  readonly updatedAt?: string | null;
  readonly attemptDeadline?: string | null;
  readonly lastError?: string | null;
  readonly usage?: AiUsage | null;
}
export interface AiSessionPage {
  readonly items: readonly AiSessionSummary[];
  readonly offset: number;
  readonly hasMore: boolean;
}
export interface AiSessionState {
  readonly id: string;
  readonly systemPrompt: string;
  readonly turns: readonly {
    readonly requestId: string;
    readonly userPrompt: string;
    readonly content: string;
    readonly usage?: AiUsage | null;
    readonly completedAt?: string | null;
  }[];
}

export interface AiQuotaWindow {
  readonly id: string;
  readonly name: string;
  readonly usedPercent: number | null;
  readonly remainingPercent: number | null;
  readonly resetsAt: string | null;
}
export interface AiQuotaSnapshot {
  readonly provider: string;
  readonly state: 'available' | 'unavailable' | 'stale';
  readonly windows: readonly AiQuotaWindow[];
  readonly observedAt: string | null;
  readonly checkedAt: string;
  readonly experimental: boolean;
  readonly message?: string | null;
}
export interface AiNodeQuotas {
  readonly nodeId: string;
  readonly name: string;
  readonly providers: readonly AiQuotaSnapshot[];
}
export interface AiClusterQuotas {
  readonly nodes: readonly AiNodeQuotas[];
}
