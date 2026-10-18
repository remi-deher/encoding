import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  AiClusterView,
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

  forkSession(id: string): Observable<{ readonly id: string }> {
    return this.http.post<{ readonly id: string }>(
      `/api/v1/tools/ai/sessions/${encodeURIComponent(id)}/fork`,
      {},
    );
  }

  getCluster(): Observable<AiClusterView> {
    return this.http.get<AiClusterView>('/api/v1/tools/ai/cluster');
  }

  saveClaudeToken(token: string): Observable<{ readonly saved: boolean }> {
    return this.http.post<{ readonly saved: boolean }>('/api/v1/tools/ai/claude-cli/auth/token', {
      token,
    });
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
