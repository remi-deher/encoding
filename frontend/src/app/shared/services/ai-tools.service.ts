import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  AiClusterView,
  AiAssistantConversation,
  AiAssistantConversationDetail,
  AiAssistantActivity,
  AiAssistantMessageRequest,
  AiAssistantMessageResponse,
  AiAssistantConfirmActionRequest,
  AiAssistantPrepareActionRequest,
  AiAssistantPreparedAction,
  AiAssistantSourcesRequest,
  AiSettings,
  AiSettingsUpdateRequest,
  AiToolInfo,
  DeviceAuthStartResponse,
  DeviceAuthStatus,
  FlowAiExplainRequest,
  FlowAiGenerateRequest,
  FlowAiRefactorRequest,
  FlowAiResponse,
} from '../models/ai-tools';

@Injectable({
  providedIn: 'root',
})
export class AiToolsService {
  private readonly http = inject(HttpClient);

  listAssistantConversations(): Observable<readonly AiAssistantConversation[]> {
    return this.http.get<readonly AiAssistantConversation[]>(
      '/api/v1/tools/ai/assistant/conversations',
    );
  }

  listAssistantActivity(): Observable<readonly AiAssistantActivity[]> {
    return this.http.get<readonly AiAssistantActivity[]>('/api/v1/tools/ai/assistant/activity');
  }

  getAssistantConversation(id: string): Observable<AiAssistantConversationDetail> {
    return this.http.get<AiAssistantConversationDetail>(
      `/api/v1/tools/ai/assistant/conversations/${encodeURIComponent(id)}`,
    );
  }

  updateAssistantSources(
    id: string,
    request: AiAssistantSourcesRequest,
  ): Observable<AiAssistantConversation> {
    return this.http.put<AiAssistantConversation>(
      `/api/v1/tools/ai/assistant/conversations/${encodeURIComponent(id)}/sources`,
      request,
    );
  }

  sendAssistantMessage(request: AiAssistantMessageRequest): Observable<AiAssistantMessageResponse> {
    return this.http.post<AiAssistantMessageResponse>(
      '/api/v1/tools/ai/assistant/messages',
      request,
    );
  }

  cancelAssistantRequest(requestId: string): Observable<void> {
    return this.http.post<void>(
      `/api/v1/tools/ai/assistant/requests/${encodeURIComponent(requestId)}/cancel`,
      {},
    );
  }

  prepareAssistantFlowDeletion(request: AiAssistantPrepareActionRequest): Observable<AiAssistantPreparedAction> {
    return this.http.post<AiAssistantPreparedAction>(
      '/api/v1/tools/ai/assistant/actions/delete-flow/prepare', request,
    );
  }

  confirmAssistantFlowDeletion(request: AiAssistantConfirmActionRequest): Observable<{ readonly deleted: boolean }> {
    return this.http.post<{ readonly deleted: boolean }>(
      '/api/v1/tools/ai/assistant/actions/delete-flow/confirm', request,
    );
  }

  prepareAssistantExecutionCancellation(request: AiAssistantPrepareActionRequest): Observable<AiAssistantPreparedAction> {
    return this.http.post<AiAssistantPreparedAction>(
      '/api/v1/tools/ai/assistant/actions/cancel-execution/prepare', request,
    );
  }

  confirmAssistantExecutionCancellation(request: AiAssistantConfirmActionRequest): Observable<{ readonly cancellationRequested: boolean }> {
    return this.http.post<{ readonly cancellationRequested: boolean }>(
      '/api/v1/tools/ai/assistant/actions/cancel-execution/confirm', request,
    );
  }

  recordFlowProposalDecision(
    requestId: string,
    decision: 'applied-to-editor' | 'rejected',
  ): Observable<unknown> {
    return this.http.post(
      `/api/v1/tools/ai/assistant/activity/${encodeURIComponent(requestId)}/proposal`,
      { decision },
    );
  }

  forkSession(id: string): Observable<{ readonly id: string }> {
    return this.http.post<{ readonly id: string }>(
      `/api/v1/tools/ai/sessions/${encodeURIComponent(id)}/fork`,
      {},
    );
  }

  getCluster(): Observable<AiClusterView> {
    return this.http.get<AiClusterView>('/api/v1/tools/ai/cluster');
  }

  submitAuthInput(toolId: string, input: string): Observable<{ readonly accepted: boolean }> {
    return this.http.post<{ readonly accepted: boolean }>(
      `/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth/input`,
      { input },
    );
  }

  cancelAuth(toolId: string): Observable<unknown> {
    return this.http.post(`/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth/cancel`, {});
  }

  listTools(): Observable<readonly AiToolInfo[]> {
    return this.http.get<readonly AiToolInfo[]>('/api/v1/tools/ai');
  }

  getToolStatus(toolId: string): Observable<DeviceAuthStatus> {
    return this.http.get<DeviceAuthStatus>(`/api/v1/tools/ai/${encodeURIComponent(toolId)}/status`);
  }

  startDeviceAuth(toolId: string): Observable<DeviceAuthStartResponse> {
    return this.http.post<DeviceAuthStartResponse>(
      `/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth/device-code/start`,
      {},
    );
  }

  getDeviceAuthStatus(toolId: string): Observable<DeviceAuthStatus> {
    return this.http.get<DeviceAuthStatus>(
      `/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth/device-code/status`,
    );
  }

  revokeAuth(toolId: string): Observable<{ readonly revoked: boolean }> {
    return this.http.delete<{ readonly revoked: boolean }>(
      `/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth`,
    );
  }

  generateFlow(request: FlowAiGenerateRequest): Observable<FlowAiResponse> {
    return this.http.post<FlowAiResponse>('/api/v1/flows/ai/generate', request);
  }

  refactorFlow(request: FlowAiRefactorRequest): Observable<FlowAiResponse> {
    return this.http.post<FlowAiResponse>('/api/v1/flows/ai/refactor', request);
  }

  explainFlow(request: FlowAiExplainRequest): Observable<FlowAiResponse> {
    return this.http.post<FlowAiResponse>('/api/v1/flows/ai/explain', request);
  }

  getSettings(): Observable<AiSettings> {
    return this.http.get<AiSettings>('/api/v1/tools/ai/settings');
  }

  updateSettings(request: AiSettingsUpdateRequest): Observable<AiSettings> {
    return this.http.put<AiSettings>('/api/v1/tools/ai/settings', request);
  }
}
