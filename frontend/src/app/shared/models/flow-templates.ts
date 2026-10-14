import type { FlowDocument } from './flow-validation';
import type { MediaType } from './media-type';

export interface FlowTemplateSummary {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly mediaType: MediaType;
  readonly sourceFlowId: string;
  readonly sourceVersion: number;
  readonly sourceFlowName: string;
  readonly nodeCount: number;
  readonly createdAt: string;
}

export interface FlowTemplateDetails {
  readonly template: FlowTemplateSummary;
  readonly definition: FlowDocument;
}

export interface CreateFlowTemplateFromFlowRequest {
  readonly sourceFlowId: string;
  readonly sourceVersion: number;
  readonly name: string;
  readonly description: string;
}
