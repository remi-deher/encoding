import type {
  FlowComponentDefinition,
  FlowGraphNode,
  FlowGraphConnection,
  FlowComponentConfiguration,
  FlowComponentConfigurationSchema,
  FlowComponentReference,
} from './flow-component';
import type { MediaType } from './media-type';

export interface FlowPluginCatalog {
  readonly id: string;
  readonly version: string;
  readonly name: string;
  readonly configurationSchema?: FlowComponentConfigurationSchema;
  readonly components: readonly (Omit<FlowComponentDefinition, 'pluginId' | 'pluginName'> & {
    readonly id: string;
    readonly version: string;
  })[];
}

export interface PluginConfigurationResponse {
  readonly pluginId: string;
  readonly revision: number;
  readonly values: FlowComponentConfiguration;
  readonly configuredSecrets: readonly string[];
}

export interface FlowDocument {
  readonly formatVersion: number;
  readonly id: string;
  readonly version: number;
  readonly name: string;
  readonly mediaType: MediaType;
  readonly nodes: readonly {
    readonly id: string;
    readonly name: string | null;
    readonly component: NonNullable<FlowGraphNode['reference']>;
    readonly configuration: FlowComponentConfiguration;
  }[];
  readonly connections: readonly FlowGraphConnection[];
}
export interface FlowVersionResponse {
  readonly draftRevision?: number | null;
  readonly publishedVersion?: number | null;
  readonly definition: FlowDocument;
  readonly backupStatus: string;
  readonly createdAt: string;
}
/** A draft's next version is not an already published version. */
export function flowPublishedVersion(saved: FlowVersionResponse): number {
  return saved.draftRevision != null ? (saved.publishedVersion ?? 0) : saved.definition.version;
}
export interface FlowSummary {
  readonly isDraft: boolean;
  readonly id: string;
  readonly name: string;
  readonly mediaType: MediaType;
  readonly version: number;
  readonly nodeCount: number;
  readonly backupStatus: string;
  readonly updatedAt: string;
}

export interface FlowValidationResult {
  readonly isValid: boolean;
  readonly issues: readonly {
    readonly code: string;
    readonly path: string;
    readonly message: string;
  }[];
}

/** Resolve only exact component/schema versions and explicitly declared plugin compatibility. */
export function resolveFlowComponent(
  plugins: readonly FlowPluginCatalog[],
  reference: FlowComponentReference,
) {
  for (const plugin of plugins) {
    if (plugin.id !== reference.pluginId) continue;
    const component = plugin.components.find(
      (component) =>
        component.id === reference.componentId &&
        component.version === reference.componentVersion &&
        component.configurationSchema?.version === reference.configurationSchemaVersion &&
        (plugin.version === reference.pluginVersion ||
          component.compatiblePluginVersions?.includes(reference.pluginVersion)),
    );
    if (component) return { plugin, component };
  }
  return undefined;
}

export function flowComponentReference(
  component: FlowComponentDefinition,
  plugins: readonly FlowPluginCatalog[],
) {
  if (component.reference) return component.reference;
  const plugin = plugins.find((plugin) => plugin.id === component.pluginId);
  const descriptor = plugin?.components.find((candidate) => candidate.id === component.id);
  return {
    pluginId: component.pluginId,
    pluginVersion: plugin?.version ?? '',
    componentId: component.id,
    componentVersion: descriptor?.version ?? '',
    configurationSchemaVersion: descriptor?.configurationSchema?.version ?? 0,
  };
}
