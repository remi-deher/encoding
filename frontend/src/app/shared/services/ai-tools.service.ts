import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
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

  listTools(): Observable<readonly AiToolInfo[]> {
    return this.http.get<readonly AiToolInfo[]>('/api/v1/tools/ai');
  }

  getToolStatus(toolId: string): Observable<DeviceAuthStatus> {
    return this.http.get<DeviceAuthStatus>(`/api/v1/tools/ai/${encodeURIComponent(toolId)}/status`);
  }

  startDeviceAuth(toolId: string): Observable<DeviceAuthStartResponse> {
    return this.http.post<DeviceAuthStartResponse>(
      `/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth/device-code/start`,
      {}
    );
  }

  getDeviceAuthStatus(toolId: string): Observable<DeviceAuthStatus> {
    return this.http.get<DeviceAuthStatus>(
      `/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth/device-code/status`
    );
  }

  completeAuth(toolId: string): Observable<{ readonly success: boolean }> {
    return this.http.post<{ readonly success: boolean }>(
      `/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth/complete`,
      {}
    );
  }

  revokeAuth(toolId: string): Observable<{ readonly revoked: boolean }> {
    return this.http.delete<{ readonly revoked: boolean }>(
      `/api/v1/tools/ai/${encodeURIComponent(toolId)}/auth`
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
}
