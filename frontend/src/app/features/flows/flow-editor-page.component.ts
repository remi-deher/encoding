import { Component, TemplateRef, computed, inject, signal, viewChild } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import dagre from '@dagrejs/dagre';
import { DetailDrawerComponent } from '../../shared/ui/detail-drawer/detail-drawer';
import { ComponentConfigurationFormComponent } from '../../shared/ui/component-configuration-form.component';
import {
  createDefaultFlowComponentConfiguration,
  FlowComponentConfiguration,
  FlowComponentDefinition,
  FlowComponentOutput,
  FlowGraphConnection,
  FlowGraphNode,
  FlowGraphPosition,
} from '../../shared/models/flow-component';
import { FlowGraphEditorComponent } from '../../shared/ui/flow-graph-editor.component';
import { SearchableComponentCatalogComponent } from '../../shared/ui/searchable-component-catalog.component';

const output = (id: string, label: string, description: string): FlowComponentOutput => ({ id, label, description });
const COMMON_OUTPUTS = [output('success', 'Suite', 'Le composant a terminé son traitement.'), output('error', 'Erreur', 'Le composant n’a pas pu terminer son traitement.')];
const COMPONENTS: readonly FlowComponentDefinition[] = [
  { id: 'base-probe-media', name: 'Analyser le média', description: 'Détecte les flux vidéo, audio et sous-titres avec ffprobe.', kind: 'action', mediaType: 'video', pluginId: 'base', pluginName: 'Base', outputs: COMMON_OUTPUTS },
  { id: 'base-transcode-video', name: 'Encoder la vidéo', description: 'Configure une conversion vidéo avec FFmpeg.', kind: 'action', mediaType: 'video', pluginId: 'base', pluginName: 'Base', outputs: COMMON_OUTPUTS, configurationSchema: { version: 1, fields: [
    { id: 'videoCodec', type: 'select', label: 'Codec vidéo', defaultValue: 'hevc', options: [{ value: 'hevc', label: 'H.265 / HEVC' }, { value: 'av1', label: 'AV1' }, { value: 'h264', label: 'H.264' }] },
    { id: 'quality', type: 'number', label: 'Qualité cible', description: 'Valeur CRF utilisée par FFmpeg.', defaultValue: 23, min: 0, max: 51 },
  ] } },
  { id: 'base-convert-image', name: 'Convertir une image', description: 'Convertit une image avec ImageMagick.', kind: 'action', mediaType: 'image', pluginId: 'base', pluginName: 'Base', outputs: COMMON_OUTPUTS },
  { id: 'base-normalize-audio', name: 'Normaliser le volume', description: 'Analyse et ajuste le niveau sonore du fichier.', kind: 'action', mediaType: 'audio', pluginId: 'base', pluginName: 'Base', outputs: COMMON_OUTPUTS },
  { id: 'base-extract-subtitles', name: 'Extraire les sous-titres', description: 'Récupère les pistes de sous-titres intégrées au conteneur.', kind: 'action', mediaType: 'subtitles', pluginId: 'base', pluginName: 'Base', outputs: COMMON_OUTPUTS },
  { id: 'base-condition', name: 'Tester une condition', description: 'Évalue une règle et dirige le flow selon le résultat.', kind: 'condition', mediaType: 'general', pluginId: 'base', pluginName: 'Base', outputs: [output('yes', 'Oui', 'La condition est satisfaite.'), output('no', 'Non', 'La condition n’est pas satisfaite.'), output('error', 'Erreur', 'La condition n’a pas pu être évaluée.')], configurationSchema: { version: 1, fields: [
    { id: 'expression', type: 'text', label: 'Expression', description: 'Règle à évaluer pour choisir la sortie.', placeholder: 'Ex. codec vidéo = HEVC', required: true, multiline: true },
  ] } },
  { id: 'base-variable', name: 'Définir une variable', description: 'Crée ou met à jour une valeur réutilisable dans le flow.', kind: 'variable', mediaType: 'general', pluginId: 'base', pluginName: 'Base', outputs: COMMON_OUTPUTS },
  { id: 'plex-refresh-library', name: 'Actualiser la bibliothèque Plex', description: 'Demande à Plex d’actualiser les éléments traités.', kind: 'action', mediaType: 'video', pluginId: 'plex', pluginName: 'Plex', outputs: COMMON_OUTPUTS },
  { id: 'sonarr-refresh-series', name: 'Actualiser une série Sonarr', description: 'Synchronise le statut de la série après traitement.', kind: 'action', mediaType: 'video', pluginId: 'sonarr', pluginName: 'Sonarr', outputs: COMMON_OUTPUTS },
  { id: 'radarr-refresh-movie', name: 'Actualiser un film Radarr', description: 'Synchronise le statut du film après traitement.', kind: 'action', mediaType: 'video', pluginId: 'radarr', pluginName: 'Radarr', outputs: COMMON_OUTPUTS },
  { id: 'jellyfin-refresh-library', name: 'Actualiser la bibliothèque Jellyfin', description: 'Demande à Jellyfin d’actualiser les éléments traités.', kind: 'action', mediaType: 'video', pluginId: 'jellyfin', pluginName: 'Jellyfin', outputs: COMMON_OUTPUTS },
];
const TYPE_OPTIONS = [{ value: 'video', label: 'Vidéo' }, { value: 'image', label: 'Image' }, { value: 'audio', label: 'Audio' }, { value: 'subtitles', label: 'Sous-titres' }, { value: 'general', label: 'Logique' }] as const;
const PLUGIN_OPTIONS = [{ value: 'base', label: 'Base' }, { value: 'plex', label: 'Plex' }, { value: 'sonarr', label: 'Sonarr' }, { value: 'radarr', label: 'Radarr' }, { value: 'jellyfin', label: 'Jellyfin' }] as const;

interface FlowNodeSeed { readonly id: string; readonly componentId: string; }
interface FlowConnectionSeed { readonly sourceNodeId: string; readonly sourceOutputId: string; readonly targetNodeId: string; }
interface FlowDemo { readonly name: string; readonly nodes: readonly FlowNodeSeed[]; readonly connections?: readonly FlowConnectionSeed[]; }
const FLOW_EDITOR_DEMOS: Readonly<Record<string, FlowDemo>> = {
  'flow-video-library': { name: 'Films — H.265', nodes: [{ id: 'probe', componentId: 'base-probe-media' }, { id: 'transcode', componentId: 'base-transcode-video' }, { id: 'plex', componentId: 'plex-refresh-library' }], connections: [{ sourceNodeId: 'probe', sourceOutputId: 'success', targetNodeId: 'transcode' }, { sourceNodeId: 'transcode', sourceOutputId: 'success', targetNodeId: 'plex' }] },
  'flow-video-series': { name: 'Séries — H.265', nodes: [{ id: 'probe', componentId: 'base-probe-media' }, { id: 'transcode', componentId: 'base-transcode-video' }, { id: 'sonarr', componentId: 'sonarr-refresh-series' }], connections: [{ sourceNodeId: 'probe', sourceOutputId: 'success', targetNodeId: 'transcode' }, { sourceNodeId: 'transcode', sourceOutputId: 'success', targetNodeId: 'sonarr' }] },
  'flow-video-source-check': { name: 'Films — Vérification de la source', nodes: [{ id: 'probe', componentId: 'base-probe-media' }] },
  'flow-image-webp': { name: 'Images — Conversion WebP', nodes: [{ id: 'convert', componentId: 'base-convert-image' }] },
  'flow-music-flac': { name: 'Musique — Vérification FLAC', nodes: [{ id: 'normalize', componentId: 'base-normalize-audio' }] },
  'flow-subtitles-cleanup': { name: 'Sous-titres — Nettoyage', nodes: [{ id: 'extract', componentId: 'base-extract-subtitles' }] },
};
type ComponentDestination = { readonly kind: 'root' } | { readonly kind: 'branch'; readonly sourceNodeId: string; readonly output: FlowComponentOutput };

@Component({
  selector: 'app-flow-editor-page',
  imports: [ComponentConfigurationFormComponent, DetailDrawerComponent, FlowGraphEditorComponent, SearchableComponentCatalogComponent],
  templateUrl: './flow-editor-page.component.html',
  styleUrl: './flow-editor-page.component.scss',
})
export class FlowEditorPageComponent {
  private readonly route = inject(ActivatedRoute);
  protected readonly componentTemplate = viewChild<TemplateRef<unknown>>('componentPicker');
  protected readonly configurationTemplate = viewChild<TemplateRef<unknown>>('componentConfiguration');
  protected readonly componentPickerOpen = signal(false);
  protected readonly configurationOpen = signal(false);
  protected readonly configuringNodeId = signal<string | undefined>(undefined);
  protected readonly nodes = signal<readonly FlowGraphNode[]>([]);
  protected readonly connections = signal<readonly FlowGraphConnection[]>([]);
  protected readonly components = COMPONENTS;
  protected readonly typeOptions = TYPE_OPTIONS;
  protected readonly pluginOptions = PLUGIN_OPTIONS;
  protected readonly configuringNode = computed(() => this.nodes().find((node) => node.id === this.configuringNodeId()));
  private readonly flowId = this.route.snapshot.paramMap.get('flowId');
  private readonly demoFlow = this.flowId ? FLOW_EDITOR_DEMOS[this.flowId] : undefined;
  private readonly componentDestination = signal<ComponentDestination>({ kind: 'root' });
  private nextNodeId = 1;
  private nextConnectionId = 1;

  constructor() {
    const componentsById = new Map(COMPONENTS.map((component) => [component.id, component]));
    const initialNodes = (this.demoFlow?.nodes ?? []).flatMap((seed) => {
      const component = componentsById.get(seed.componentId);
      return component ? [{ id: seed.id, component, position: { x: 0, y: 0 }, configuration: createDefaultFlowComponentConfiguration(component.configurationSchema) }] : [];
    });
    this.nextNodeId = initialNodes.length + 1;
    const initialConnections = (this.demoFlow?.connections ?? []).map((seed) => ({ id: `connection-${this.nextConnectionId++}`, ...seed }));
    this.setGraph(initialNodes, initialConnections);
  }

  protected onAddComponentRequested(): void {
    this.componentDestination.set({ kind: 'root' });
    this.componentPickerOpen.set(true);
  }

  protected onBranchComponentRequested(event: { readonly sourceNodeId: string; readonly output: FlowComponentOutput }): void {
    this.componentDestination.set({ kind: 'branch', ...event });
    this.componentPickerOpen.set(true);
  }

  protected addComponent(component: FlowComponentDefinition): void {
    const node: FlowGraphNode = {
      id: `flow-node-${this.nextNodeId++}`,
      component,
      position: { x: 0, y: 0 },
      configuration: createDefaultFlowComponentConfiguration(component.configurationSchema),
    };
    const nodes = [...this.nodes(), node];
    const destination = this.componentDestination();
    const connections = destination.kind === 'branch'
      ? [...this.connections(), this.createConnection(destination.sourceNodeId, destination.output.id, node.id)]
      : this.connections();
    this.setGraph(nodes, connections);
    this.componentPickerOpen.set(false);
  }

  protected onConfigurationRequested(nodeId: string): void {
    this.configuringNodeId.set(nodeId);
    this.configurationOpen.set(true);
  }

  protected saveNodeConfiguration(configuration: FlowComponentConfiguration): void {
    const nodeId = this.configuringNodeId();
    if (!nodeId) return;
    this.nodes.update((nodes) => nodes.map((node) => node.id === nodeId
      ? { ...node, configuration: { ...node.configuration, ...configuration } }
      : node));
    this.configurationOpen.set(false);
  }

  protected onConnectionRequested(event: { readonly sourceNodeId: string; readonly sourceOutputId: string; readonly targetNodeId: string }): void {
    const alreadyConnected = this.connections().some((connection) => connection.targetNodeId === event.targetNodeId);
    if (alreadyConnected) return;
    this.setGraph([...this.nodes()], [...this.connections(), this.createConnection(event.sourceNodeId, event.sourceOutputId, event.targetNodeId)]);
  }

  protected onNodePositionChanged(event: { readonly nodeId: string; readonly position: FlowGraphPosition }): void {
    this.nodes.update((nodes) => nodes.map((node) => node.id === event.nodeId ? { ...node, position: event.position } : node));
  }

  protected removeNode(nodeId: string): void {
    this.setGraph(
      this.nodes().filter((node) => node.id !== nodeId),
      this.connections().filter((connection) => connection.sourceNodeId !== nodeId && connection.targetNodeId !== nodeId),
    );
  }

  protected removeConnection(connectionId: string): void {
    const connections = this.connections().filter((connection) => connection.id !== connectionId);
    if (connections.length !== this.connections().length) this.setGraph([...this.nodes()], connections);
  }

  private createConnection(sourceNodeId: string, sourceOutputId: string, targetNodeId: string): FlowGraphConnection {
    return { id: `connection-${this.nextConnectionId++}`, sourceNodeId, sourceOutputId, targetNodeId };
  }

  private setGraph(nodes: readonly FlowGraphNode[], connections: readonly FlowGraphConnection[]): void {
    const graph = new dagre.graphlib.Graph().setGraph({ rankdir: 'LR', nodesep: 72, ranksep: 112, marginx: 64, marginy: 48 }).setDefaultEdgeLabel(() => ({}));
    for (const node of nodes) {
      const activeOutputCount = new Set(connections.filter((connection) => connection.sourceNodeId === node.id).map((connection) => connection.sourceOutputId)).size;
      const hasAvailableOutputs = activeOutputCount < node.component.outputs.length;
      const height = 135 + activeOutputCount * 25 + (hasAvailableOutputs ? 34 : 0);
      graph.setNode(node.id, { width: 280, height });
    }
    for (const connection of connections) graph.setEdge(connection.sourceNodeId, connection.targetNodeId, {});
    dagre.layout(graph);
    this.nodes.set(nodes.map((node) => {
      const point = graph.node(node.id);
      return point ? { ...node, position: { x: point.x - point.width / 2, y: point.y - point.height / 2 } } : node;
    }));
    this.connections.set(connections);
  }
}
