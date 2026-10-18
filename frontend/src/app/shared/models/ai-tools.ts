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
