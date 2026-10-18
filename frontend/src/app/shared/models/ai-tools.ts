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
}

export interface FlowAiRefactorRequest {
  readonly currentFlow: FlowDocument;
  readonly userPrompt: string;
  readonly provider?: string | null;
}

export interface FlowAiExplainRequest {
  readonly flow: FlowDocument;
  readonly provider?: string | null;
}

export interface FlowAiResponse {
  readonly success: boolean;
  readonly flow?: FlowDocument | null;
  readonly explanation?: string | null;
  readonly validationIssues?: readonly FlowValidationIssue[] | null;
}
