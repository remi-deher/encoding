import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom, map } from 'rxjs';
import {
  FlowGraphNode,
  FlowGraphConnection,
  FlowComponentConfigurationField,
} from '../models/flow-component';
import { MediaType } from '../models/media-type';
import {
  FlowPluginCatalog,
  FlowDocument,
  FlowVersionResponse,
  FlowSummary,
  FlowValidationResult,
  flowComponentReference,
} from '../models/flow-validation';
@Injectable({ providedIn: 'root' })
export class FlowValidation {
  private readonly http = inject(HttpClient);
  versionStatus(id: string, version: number) {
    return this.http.get<FlowVersionResponse>('/api/v1/flows/' + id + '/versions/' + version);
  }
  list() {
    return this.http.get<readonly FlowSummary[]>('/api/v1/flows');
  }
  get(id: string) {
    return firstValueFrom(this.http.get<FlowVersionResponse>('/api/v1/flows/' + id));
  }
  catalog() {
    return firstValueFrom(
      this.http.get<readonly FlowPluginCatalog[]>('/api/v1/flows/components').pipe(
        map((plugins) =>
          plugins.map((plugin) => ({
            ...plugin,
            components: plugin.components.map((component) => ({
              ...component,
              configurationSchema: component.configurationSchema
                ? {
                    ...component.configurationSchema,
                    fields: component.configurationSchema.fields.map(
                      (field) =>
                        Object.fromEntries(
                          Object.entries(field).filter(([, value]) => value !== null),
                        ) as unknown as FlowComponentConfigurationField,
                    ),
                  }
                : undefined,
            })),
          })),
        ),
      ),
    );
  }

  async document(
    id: string,
    version: number,
    name: string,
    mediaType: MediaType,
    nodes: readonly FlowGraphNode[],
    connections: readonly FlowGraphConnection[],
  ): Promise<FlowDocument> {
    const plugins = await this.catalog();
    return {
      formatVersion: 1,
      id,
      version,
      name,
      mediaType,
      nodes: nodes.map((node) => ({
        id: node.id,
        name: node.name ?? null,
        component: node.reference ?? flowComponentReference(node.component, plugins),
        configuration: Object.fromEntries(
          Object.entries(node.configuration).filter(
            ([key]) => node.component.kind !== 'input' || key !== 'inputComponentId',
          ),
        ),
      })),
      connections,
    };
  }

  save(definition: FlowDocument, expectedVersion: number) {
    return firstValueFrom(
      this.http.put<FlowVersionResponse>('/api/v1/flows/' + definition.id, {
        definition,
        expectedVersion,
      }),
    );
  }

  async validate(
    id: string,
    name: string,
    mediaType: MediaType,
    nodes: readonly FlowGraphNode[],
    connections: readonly FlowGraphConnection[],
  ): Promise<FlowValidationResult> {
    const document = await this.document(id, 1, name, mediaType, nodes, connections);
    try {
      return await firstValueFrom(
        this.http.post<FlowValidationResult>('/api/v1/flows/validate', document),
      );
    } catch (error) {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 422 &&
        Array.isArray(error.error?.issues)
      )
        return error.error as FlowValidationResult;
      throw error;
    }
  }
}
