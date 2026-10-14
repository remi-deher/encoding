import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  FlowScriptDetails,
  FlowScriptDraft,
  FlowScriptSummary,
  FlowScriptVersion,
} from '../models/flow-scripts';

@Injectable({ providedIn: 'root' })
export class FlowScripts {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1/scripts';

  list() {
    return this.http.get<readonly FlowScriptSummary[]>(this.base);
  }

  get(id: string) {
    return firstValueFrom(this.http.get<FlowScriptDetails>(`${this.base}/${id}`));
  }

  version(id: string, version: number) {
    return firstValueFrom(
      this.http.get<FlowScriptVersion>(`${this.base}/${id}/versions/${version}`),
    );
  }

  create(draft: FlowScriptDraft) {
    return firstValueFrom(this.http.post<FlowScriptDetails>(this.base, { draft }));
  }

  saveDraft(id: string, expectedRevision: number, draft: FlowScriptDraft) {
    return firstValueFrom(
      this.http.put<FlowScriptDetails>(`${this.base}/${id}/draft`, { expectedRevision, draft }),
    );
  }

  publish(id: string, expectedRevision: number) {
    return firstValueFrom(
      this.http.post<FlowScriptVersion>(`${this.base}/${id}/publish`, { expectedRevision }),
    );
  }
}
