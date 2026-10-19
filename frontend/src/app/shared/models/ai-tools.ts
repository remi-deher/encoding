import { FlowDocument, FlowValidationIssue } from './flow-validation';

export interface DeviceAuthPrompt {
  readonly userCode?: string | null;
  readonly verificationUri: string;
  readonly expiresInSeconds: number;
  readonly intervalSeconds: number;
  readonly requiresInput?: boolean;
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
  readonly assistantProvider?: string | null;
  readonly flowProvider?: string | null;
  readonly assistantLocalOnly?: boolean;
  readonly flowLocalOnly?: boolean;
  readonly assistantConversationRetentionDays: number;
  readonly assistantActivityRetentionDays: number;
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
  readonly assistantProvider?: string | null;
  readonly flowProvider?: string | null;
  readonly assistantLocalOnly?: boolean;
  readonly flowLocalOnly?: boolean;
  readonly assistantConversationRetentionDays?: number;
  readonly assistantActivityRetentionDays?: number;
}

export interface AiNodeAvailability {
  readonly nodeId: string;
  readonly name: string;
  readonly providers: readonly string[];
  readonly reachable: boolean;
  readonly models?: Readonly<Record<string, string | null>>;
}
export interface AiClusterView {
  readonly nodes: readonly AiNodeAvailability[];
}

export interface AiAssistantSource {
  readonly kind: 'flow' | 'execution' | 'node' | 'library' | 'tool';
  readonly resourceId: string;
  readonly label: string;
  readonly href: string;
  readonly version?: number | null;
}

export interface AiAssistantConversation {
  readonly id: string;
  readonly provider: string;
  readonly title: string;
  readonly source: AiAssistantSource;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly additionalSources?: readonly AiAssistantSource[] | null;
  readonly model?: string | null;
  readonly reasoningEffort?: string | null;
}

export interface AiAssistantModelOption {
  readonly provider: string;
  readonly model?: string | null;
  readonly label: string;
  readonly reasoningEfforts: readonly string[];
}

export interface AiAssistantOptions {
  readonly defaultProvider: string;
  readonly defaultModel?: string | null;
  readonly models: readonly AiAssistantModelOption[];
}

export interface AiAssistantTurn {
  readonly requestId: string;
  readonly userPrompt: string;
  readonly content: string;
  readonly sources?: readonly AiAssistantSource[] | null;
}

export interface AiAssistantConversationDetail {
  readonly conversation: AiAssistantConversation;
  readonly turns: readonly AiAssistantTurn[];
  readonly pendingTurn?: AiAssistantPendingTurn | null;
}

export interface AiAssistantPendingTurn {
  readonly requestId: string;
  readonly userPrompt: string;
  readonly isRunning: boolean;
}

export interface AiAssistantMessageRequest {
  readonly requestId: string;
  readonly userPrompt: string;
  readonly conversationId?: string | null;
  readonly resourceType?: 'flow' | 'execution' | 'node' | 'library' | null;
  readonly resourceId?: string | null;
  readonly resourceVersion?: number | null;
  readonly toolCalls?: readonly AiAssistantToolCall[];
  readonly provider?: string | null;
  readonly model?: string | null;
  readonly reasoningEffort?: string | null;
}

export interface AiAssistantToolCall {
  readonly name:
    'list_flows' | 'get_flow' | 'list_nodes' | 'list_executions' | 'get_libraries_overview';
  readonly flowId?: string | null;
  readonly state?: 'queued' | 'running' | 'failed' | 'completed' | null;
  readonly limit?: number | null;
}

export interface AiAssistantSourcesRequest {
  readonly sources: readonly {
    readonly type: string;
    readonly id: string;
    readonly version?: number | null;
  }[];
}

export interface AiAssistantMessageResponse {
  readonly success: boolean;
  readonly conversation: AiAssistantConversation;
  readonly requestId: string;
  readonly content?: string | null;
  readonly errorMessage?: string | null;
  readonly sources: readonly AiAssistantSource[];
}

export interface AiAssistantPrepareActionRequest {
  readonly resourceId: string;
  readonly expectedVersion: number;
}

export interface AiAssistantPreparedAction {
  readonly confirmationToken: string;
  readonly summary: string;
  readonly expiresAt: string;
}

export interface AiAssistantConfirmActionRequest {
  readonly confirmationToken: string;
}

export interface AiAssistantActivity {
  readonly requestId: string;
  readonly conversationId: string | null;
  readonly provider: string;
  readonly status:
    | 'running'
    | 'succeeded'
    | 'failed'
    | 'cancelled'
    | 'proposal-ready'
    | 'applied-to-editor'
    | 'rejected';
  readonly startedAt: string;
  readonly updatedAt: string;
  readonly durationMilliseconds: number | null;
  readonly errorCategory: string | null;
  readonly operation?: string;
  readonly tools?: readonly string[] | null;
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
