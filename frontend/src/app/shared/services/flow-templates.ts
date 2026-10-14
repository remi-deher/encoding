import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  CreateFlowTemplateFromFlowRequest,
  FlowTemplateDetails,
  FlowTemplateSummary,
} from '../models/flow-templates';

@Injectable({ providedIn: 'root' })
export class FlowTemplates {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1/templates';

  list() {
    return this.http.get<readonly FlowTemplateSummary[]>(this.base);
  }

  get(id: string) {
    return firstValueFrom(this.http.get<FlowTemplateDetails>(`${this.base}/${id}`));
  }

  createFromFlow(request: CreateFlowTemplateFromFlowRequest) {
    return firstValueFrom(this.http.post<FlowTemplateDetails>(`${this.base}/from-flow`, request));
  }
}
