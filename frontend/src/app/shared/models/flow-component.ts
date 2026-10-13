export interface FlowComponentDefinition {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly kind: FlowComponentKind;
  readonly mediaType: 'video' | 'image' | 'audio' | 'subtitles' | 'general';
  readonly pluginId: string;
  readonly pluginName: string;
  readonly outputs: readonly FlowComponentOutput[];
  readonly configurationSchema?: FlowComponentConfigurationSchema;
}

export type FlowComponentKind = 'action' | 'condition' | 'variable';

export interface FlowComponentOutput {
  readonly id: string;
  readonly label: string;
  readonly description: string;
}

export interface FlowComponentConfigurationSchema {
  readonly version: 1;
  readonly fields: readonly FlowComponentConfigurationField[];
}

interface FlowComponentConfigurationFieldBase {
  readonly id: string;
  readonly label: string;
  readonly description?: string;
  readonly required?: boolean;
}

export type FlowComponentConfigurationField =
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'text';
      readonly defaultValue?: string;
      readonly placeholder?: string;
      readonly multiline?: boolean;
    })
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'number';
      readonly defaultValue?: number;
      readonly min?: number;
      readonly max?: number;
      readonly step?: number;
    })
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'boolean';
      readonly defaultValue?: boolean;
    })
  | (FlowComponentConfigurationFieldBase & {
      readonly type: 'select';
      readonly defaultValue?: string;
      readonly options: readonly { readonly value: string; readonly label: string }[];
    });

export type FlowComponentConfigurationValue = string | number | boolean | null;
export type FlowComponentConfiguration = Readonly<Record<string, FlowComponentConfigurationValue>>;

export function createDefaultFlowComponentConfiguration(
  schema: FlowComponentConfigurationSchema | undefined,
): FlowComponentConfiguration {
  return Object.fromEntries((schema?.fields ?? []).flatMap((field) => {
    if (field.defaultValue !== undefined) return [[field.id, field.defaultValue]];
    if (field.type === 'boolean') return [[field.id, false]];
    return [];
  }));
}

export interface FlowComponentFilterOption {
  readonly value: string;
  readonly label: string;
}

export interface FlowGraphPosition {
  readonly x: number;
  readonly y: number;
}

export interface FlowGraphNode {
  readonly id: string;
  readonly component: FlowComponentDefinition;
  readonly position: FlowGraphPosition;
  readonly configuration: FlowComponentConfiguration;
}

export interface FlowGraphConnection {
  readonly id: string;
  readonly sourceNodeId: string;
  readonly sourceOutputId: string;
  readonly targetNodeId: string;
}

export interface FlowConnectionRequest {
  readonly sourceNodeId: string;
  readonly sourceOutputId: string;
  readonly targetNodeId: string;
}
