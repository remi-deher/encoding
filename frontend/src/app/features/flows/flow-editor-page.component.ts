import { toObservable, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { timer, switchMap, catchError, of, from } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { FlowValidation } from '../../shared/services/flow-validation';
import { FlowContinuationSource } from '../../shared/models/flow-sequence';
import { Component, TemplateRef, computed, inject, signal, viewChild } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import {
  GENERIC_FILE_INPUT,
  MEDIA_INPUT_COMPONENTS,
  createFlowInputConfigurationSchema,
  getFlowInputComponents,
} from '../../shared/models/flow-control-components';
import { canConnectFlowGraph, flowConfigurationFieldError } from '../../shared/models/flow-graph';
import { NotificationService } from '../../shared/services/notification.service';
import { FLOW_COMPONENT_CATALOG } from '../../shared/models/flow-component-catalog';
import { MEDIA_TYPE_OPTIONS, MediaType } from '../../shared/models/media-type';
import { DetailDrawerComponent } from '../../shared/ui/detail-drawer/detail-drawer';
import { ComponentConfigurationFormComponent } from '../../shared/ui/component-configuration-form.component';
import {
  createDefaultFlowComponentConfiguration,
  isFlowComponentCompatible,
  getFlowConfigurationSchema,
  FlowComponentConfiguration,
  FlowComponentConfigurationValue,
  FlowComponentDefinition,
  FlowComponentOutput,
  FlowGraphConnection,
  FlowGraphNode,
} from '../../shared/models/flow-component';
import { FlowGraphEditorComponent } from '../../shared/ui/flow-graph-editor.component';
import { SearchableComponentCatalogComponent } from '../../shared/ui/searchable-component-catalog.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';

const TYPE_OPTIONS = [
  { value: 'video', label: 'Vidéo' },
  { value: 'image', label: 'Image' },
  { value: 'audio', label: 'Audio' },
  { value: 'subtitles', label: 'Sous-titres' },
  { value: 'general', label: 'Logique' },
] as const;
interface FlowNodeSeed {
  readonly id: string;
  readonly componentId: string;
}
interface FlowConnectionSeed {
  readonly sourceNodeId: string;
  readonly sourceOutputId: string;
  readonly targetNodeId: string;
}
interface FlowDemo {
  readonly name: string;
  readonly nodes: readonly FlowNodeSeed[];
  readonly connections?: readonly FlowConnectionSeed[];
}
const FLOW_EDITOR_DEMOS: Readonly<Record<string, FlowDemo>> = {
  'flow-video-library': {
    name: 'Films — H.265',
    nodes: [
      { id: 'probe', componentId: 'base-probe-media' },
      { id: 'transcode', componentId: 'base-transcode-video' },
      { id: 'plex', componentId: 'plex-refresh-library' },
    ],
    connections: [
      { sourceNodeId: 'probe', sourceOutputId: 'success', targetNodeId: 'transcode' },
      { sourceNodeId: 'transcode', sourceOutputId: 'success', targetNodeId: 'plex' },
    ],
  },
  'flow-video-series': {
    name: 'Séries — H.265',
    nodes: [
      { id: 'probe', componentId: 'base-probe-media' },
      { id: 'transcode', componentId: 'base-transcode-video' },
      { id: 'sonarr', componentId: 'sonarr-refresh-series' },
    ],
    connections: [
      { sourceNodeId: 'probe', sourceOutputId: 'success', targetNodeId: 'transcode' },
      { sourceNodeId: 'transcode', sourceOutputId: 'success', targetNodeId: 'sonarr' },
    ],
  },
  'flow-video-source-check': {
    name: 'Films — Vérification de la source',
    nodes: [{ id: 'probe', componentId: 'base-probe-media' }],
  },
  'flow-image-webp': {
    name: 'Images — Conversion WebP',
    nodes: [{ id: 'convert', componentId: 'base-convert-image' }],
  },
  'flow-music-flac': {
    name: 'Musique — Vérification FLAC',
    nodes: [{ id: 'normalize', componentId: 'base-normalize-audio' }],
  },
  'flow-subtitles-cleanup': {
    name: 'Sous-titres — Nettoyage',
    nodes: [{ id: 'prepare', componentId: 'base-script' }],
  },
};
type ComponentDestination =
  | { readonly kind: 'continuation'; readonly sources: readonly FlowContinuationSource[] }
  | {
      readonly kind: 'branch';
      readonly sourceNodeId: string;
      readonly output: FlowComponentOutput;
    };

@Component({
  selector: 'app-flow-editor-page',
  host: { '(window:beforeunload)': 'warnBeforeUnload($event)' },
  imports: [
    RouterLink,
    MatButtonModule,
    ComponentConfigurationFormComponent,
    DetailDrawerComponent,
    FlowGraphEditorComponent,
    SearchableComponentCatalogComponent,
    PageHeaderComponent,
  ],
  templateUrl: './flow-editor-page.component.html',
  styleUrl: './flow-editor-page.component.scss',
})
export class FlowEditorPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly notifications = inject(NotificationService);
  protected readonly componentTemplate = viewChild<TemplateRef<unknown>>('componentPicker');
  protected readonly configurationTemplate =
    viewChild<TemplateRef<unknown>>('componentConfiguration');
  protected readonly componentPickerOpen = signal(false);
  protected readonly configurationOpen = signal(false);
  protected readonly configuringNodeId = signal<string | undefined>(undefined);
  protected readonly nodes = signal<readonly FlowGraphNode[]>([]);
  protected readonly connections = signal<readonly FlowGraphConnection[]>([]);
  protected readonly flowMediaType = signal<MediaType>('video');
  private readonly installedComponents = signal<readonly FlowComponentDefinition[]>([]);
  protected readonly components = computed(() =>
    this.installedComponents().filter(
      (component) =>
        component.kind !== 'input' && isFlowComponentCompatible(component, this.flowMediaType()),
    ),
  );
  protected readonly typeOptions = computed(() =>
    TYPE_OPTIONS.filter((option) =>
      this.components().some((component) => component.mediaType === option.value),
    ),
  );
  protected readonly pluginOptions = computed(() => [
    ...new Map(
      this.components().map((component) => [
        component.pluginId,
        { value: component.pluginId, label: component.pluginName },
      ]),
    ).values(),
  ]);
  protected readonly configuringNode = computed(() =>
    this.nodes().find((node) => node.id === this.configuringNodeId()),
  );
  protected readonly configuringSchema = computed(() =>
    this.configuringNode()?.component.kind === 'input'
      ? createFlowInputConfigurationSchema(this.flowMediaType())
      : getFlowConfigurationSchema(
          this.configuringNode()?.component.configurationSchema,
          this.flowMediaType(),
        ),
  );
  protected readonly configuringValues = computed<FlowComponentConfiguration>(() => {
    const node = this.configuringNode();
    return node?.component.kind === 'input'
      ? { inputComponentId: node.component.id }
      : (node?.configuration ?? {});
  });
  private readonly flowId = this.route.snapshot.paramMap.get('flowId');
  private readonly starterId = this.flowId ?? this.route.snapshot.queryParamMap.get('starter');
  private readonly demoFlow =
    this.starterId && this.starterId !== 'blank' ? FLOW_EDITOR_DEMOS[this.starterId] : undefined;
  private readonly savedName = signal<string | null>(null);
  protected readonly flowTitle = computed(() => {
    if (this.savedName()) return this.savedName()!;
    const name = this.route.snapshot.queryParamMap.get('name');
    if (name) return name;
    return this.demoFlow?.name ?? (this.flowId ? 'Modifier un flow' : 'Nouveau flow');
  });
  private readonly componentDestination = signal<ComponentDestination | null>(null);
  private nextNodeId = 1;
  private nextConnectionId = 1;

  constructor() {
    const componentsById = new Map(
      FLOW_COMPONENT_CATALOG.map((component) => [component.id, component]),
    );
    const requestedMediaType = this.route.snapshot.queryParamMap.get('mediaType');
    const inferredMediaType = this.starterId?.startsWith('flow-image')
      ? 'image'
      : this.starterId?.startsWith('flow-music')
        ? 'music'
        : this.starterId?.startsWith('flow-subtitles')
          ? 'subtitles'
          : 'video';
    const mediaType =
      MEDIA_TYPE_OPTIONS.find((option) => option.value === requestedMediaType)?.value ??
      inferredMediaType;
    this.flowMediaType.set(mediaType);
    const inputComponent = this.demoFlow ? MEDIA_INPUT_COMPONENTS[mediaType] : GENERIC_FILE_INPUT;
    const entry: FlowGraphNode = {
      id: 'flow-input',
      component: inputComponent,
      position: { x: 64, y: 48 },
      configuration: {},
    };
    const initialNodes: FlowGraphNode[] = (this.demoFlow?.nodes ?? []).flatMap((seed) => {
      const component = componentsById.get(seed.componentId);
      return component && isFlowComponentCompatible(component, mediaType)
        ? [
            {
              id: seed.id,
              component,
              position: { x: 0, y: 0 },
              configuration: createDefaultFlowComponentConfiguration(
                getFlowConfigurationSchema(component.configurationSchema, mediaType),
              ),
            },
          ]
        : [];
    });
    this.nextNodeId = initialNodes.length + 1;
    const initialConnections = (this.demoFlow?.connections ?? [])
      .filter(
        (seed) =>
          initialNodes.some((node) => node.id === seed.sourceNodeId) &&
          initialNodes.some((node) => node.id === seed.targetNodeId),
      )
      .map((seed) => ({
        id: `connection-${this.nextConnectionId++}`,
        ...seed,
      }));
    if (initialNodes.length)
      initialConnections.unshift(this.createConnection(entry.id, 'success', initialNodes[0].id));
    this.setGraph([entry, ...initialNodes], initialConnections);
    void this.serverValidation
      .catalog()
      .then((plugins) => {
        this.installedComponents.set(
          plugins.flatMap((plugin) =>
            plugin.components.map((component) => ({
              ...component,
              pluginId: plugin.id,
              pluginName: plugin.name,
            })),
          ),
        );
      })
      .catch(() =>
        this.notifications.show(
          'Catalogue des composants indisponible. Aucun nouveau bloc ne peut être ajouté.',
        ),
      );
    if (this.flowId && /^[0-9a-f]{8}-[0-9a-f-]{27}$/i.test(this.flowId)) {
      this.loadState.set('loading');
      void this.loadSavedFlow(this.flowId);
    }
    toObservable(computed(() => ({ version: this.storedVersion(), draft: this.draftRevision() })))
      .pipe(
        switchMap(({ version, draft }) =>
          version > 0 || draft !== undefined
            ? timer(0, 5000).pipe(
                switchMap(() =>
                  (draft !== undefined
                    ? from(this.serverValidation.get(this.validationFlowId))
                    : this.serverValidation.versionStatus(this.validationFlowId, version)
                  ).pipe(catchError(() => of(null))),
                ),
              )
            : of(null),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((response) => {
        if (response && (response.draftRevision ?? undefined) === this.draftRevision())
          this.backupStatus.set(response.backupStatus);
      });
  }

  protected onTerminationRequested(event: {
    readonly sourceNodeId: string;
    readonly output: FlowComponentOutput;
  }): void {
    const component = this.components().find((component) => component.id === 'base-complete-flow');
    if (!component) return;
    this.componentDestination.set({ kind: 'branch', ...event });
    this.addComponent(component);
  }
  protected onBranchComponentRequested(event: {
    readonly sourceNodeId: string;
    readonly output: FlowComponentOutput;
  }): void {
    this.componentDestination.set({ kind: 'branch', ...event });
    this.componentPickerOpen.set(true);
  }

  protected onContinuationRequested(sources: readonly FlowContinuationSource[]): void {
    if (!sources.length) return;
    this.componentDestination.set({ kind: 'continuation', sources });
    this.componentPickerOpen.set(true);
  }

  protected addComponent(component: FlowComponentDefinition): void {
    if (component.kind === 'input') return;
    if (!isFlowComponentCompatible(component, this.flowMediaType())) {
      this.notifications.show('Ce bloc n’est pas compatible avec le type de média du flow.');
      return;
    }
    const destination = this.componentDestination();
    if (!destination) return;
    const node: FlowGraphNode = {
      id: `flow-node-${this.nextNodeId++}`,
      component,
      position: { x: 0, y: 0 },
      configuration: createDefaultFlowComponentConfiguration(
        getFlowConfigurationSchema(component.configurationSchema, this.flowMediaType()),
      ),
    };
    const nodes = [...this.nodes(), node];
    if (destination.kind === 'continuation') {
      let next = [...this.connections()];
      for (const source of destination.sources) {
        if (
          !canConnectFlowGraph(nodes, next, {
            sourceNodeId: source.sourceNodeId,
            sourceOutputId: source.output.id,
            targetNodeId: node.id,
          })
        ) {
          this.notifications.show(
            'Une branche a changé. Choisissez à nouveau où ajouter la suite.',
          );
          return;
        }
        next.push(this.createConnection(source.sourceNodeId, source.output.id, node.id));
      }
      this.setGraph(nodes, next);
      this.componentPickerOpen.set(false);
      return;
    }
    const existing =
      destination.kind === 'branch'
        ? this.connections().find(
            (edge) =>
              edge.sourceNodeId === destination.sourceNodeId &&
              edge.sourceOutputId === destination.output.id,
          )
        : undefined;
    const remaining = this.connections().filter((edge) => edge.id !== existing?.id);
    if (
      destination.kind === 'branch' &&
      !canConnectFlowGraph(nodes, remaining, {
        sourceNodeId: destination.sourceNodeId,
        sourceOutputId: destination.output.id,
        targetNodeId: node.id,
      })
    ) {
      this.notifications.show('Cette sortie n’est plus disponible.');
      return;
    }
    const continuation =
      component.outputs.find((output) => output.id === 'success') ?? component.outputs[0];
    if (existing && !continuation) {
      this.notifications.show(
        'Une étape terminale ne peut pas être insérée avant une suite. Ajoutez-la à la fin d’un chemin.',
      );
      return;
    }
    const connections =
      destination.kind === 'branch'
        ? [
            ...remaining,
            this.createConnection(destination.sourceNodeId, destination.output.id, node.id),
            ...(existing && continuation
              ? [this.createConnection(node.id, continuation.id, existing.targetNodeId)]
              : []),
          ]
        : remaining;
    this.setGraph(nodes, connections);
    this.componentPickerOpen.set(false);
  }

  protected onInlineConfigurationChanged(event: {
    readonly nodeId: string;
    readonly fieldId: string;
    readonly value: FlowComponentConfigurationValue;
  }): void {
    const node = this.nodes().find((candidate) => candidate.id === event.nodeId);
    const field = getFlowConfigurationSchema(
      node?.component.configurationSchema,
      this.flowMediaType(),
    )?.fields.find((field) => field.id === event.fieldId);
    if (!node || !field?.inline) return;
    const error = flowConfigurationFieldError(field, event.value);
    if (error) {
      this.notifications.show(error);
      return;
    }
    this.nodes.update((nodes) =>
      nodes.map((candidate) =>
        candidate.id === node.id
          ? { ...candidate, configuration: { ...candidate.configuration, [field.id]: event.value } }
          : candidate,
      ),
    );
  }

  protected onConfigurationRequested(nodeId: string): void {
    this.configuringNodeId.set(nodeId);
    this.configurationOpen.set(true);
  }

  protected renameNode(name: string): void {
    const nodeId = this.configuringNodeId();
    this.nodes.update((nodes) =>
      nodes.map((node) => (node.id === nodeId ? { ...node, name: name || undefined } : node)),
    );
  }

  private readonly serverValidation = inject(FlowValidation);
  private readonly editorRouter = inject(Router);
  private readonly validationFlowId =
    this.flowId && /^[0-9a-f]{8}-[0-9a-f-]{27}$/i.test(this.flowId)
      ? this.flowId
      : crypto.randomUUID();
  private readonly savedGraph = signal<string | null>(null);
  hasUnsavedChanges(): boolean {
    return (
      this.loadState() === 'ready' &&
      this.savedGraph() !== JSON.stringify([this.nodes(), this.connections()])
    );
  }
  protected warnBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.hasUnsavedChanges()) {
      event.preventDefault();
      event.returnValue = '';
    }
  }
  private readonly draftRevision = signal<number | undefined>(undefined);
  protected readonly storedVersion = signal(0);
  protected readonly saving = signal(false);
  protected readonly loadState = signal<'ready' | 'loading' | 'error'>('ready');
  protected readonly backupStatus = signal<string | null>(null);
  private async loadSavedFlow(id: string): Promise<void> {
    try {
      const [saved, plugins] = await Promise.all([
        this.serverValidation.get(id),
        this.serverValidation.catalog(),
      ]);
      const definition = saved.definition;
      this.savedName.set(definition.name);
      this.flowMediaType.set(definition.mediaType);
      const nodes: FlowGraphNode[] = definition.nodes.map((node) => {
        const plugin = plugins.find(
          (plugin) =>
            plugin.id === node.component.pluginId &&
            plugin.version === node.component.pluginVersion,
        );
        const descriptor = plugin?.components.find(
          (component) =>
            component.id === node.component.componentId &&
            component.version === node.component.componentVersion,
        );
        const component: FlowComponentDefinition = descriptor
          ? { ...descriptor, pluginId: plugin!.id, pluginName: plugin!.name }
          : {
              id: node.component.componentId,
              name: node.name ?? node.component.componentId,
              description:
                'Cette version du composant est indisponible. Sa configuration est conservée.',
              kind: 'action',
              mediaType: 'general',
              pluginId: node.component.pluginId,
              pluginName: node.component.pluginId,
              outputs: definition.connections
                .filter((edge) => edge.sourceNodeId === node.id)
                .map((edge) => ({
                  id: edge.sourceOutputId,
                  label: edge.sourceOutputId,
                  description: '',
                })),
            };
        return {
          id: node.id,
          name: node.name ?? undefined,
          component,
          reference: node.component,
          configuration: node.configuration,
          position: { x: 0, y: 0 },
        };
      });
      this.nextNodeId =
        Math.max(0, ...nodes.map((node) => Number(node.id.match(/^flow-node-(\d+)$/)?.[1]) || 0)) +
        1;
      this.nextConnectionId =
        Math.max(
          0,
          ...definition.connections.map(
            (edge) => Number(edge.id.match(/^connection-(\d+)$/)?.[1]) || 0,
          ),
        ) + 1;
      this.setGraph(nodes, definition.connections);
      this.savedGraph.set(JSON.stringify([nodes, definition.connections]));
      this.storedVersion.set(saved.publishedVersion ?? definition.version);
      this.draftRevision.set(saved.draftRevision);
      this.backupStatus.set(saved.backupStatus);
      this.loadState.set('ready');
    } catch {
      this.loadState.set('error');
      this.notifications.show('Impossible de charger le flow enregistré.');
    }
  }

  protected async saveFlow(draft = false): Promise<void> {
    if (this.saving() || this.loadState() !== 'ready') return;
    this.saving.set(true);
    const nodes = this.nodes(),
      connections = this.connections(),
      expected = this.storedVersion();
    try {
      const document = await this.serverValidation.document(
        this.validationFlowId,
        expected + 1,
        this.flowTitle(),
        this.flowMediaType(),
        nodes,
        connections,
      );
      const saved = draft
        ? await this.serverValidation.saveDraft(document, this.draftRevision() ?? 0)
        : await this.serverValidation.save(document, expected, this.draftRevision());
      this.savedGraph.set(JSON.stringify([nodes, connections]));
      this.storedVersion.set(
        draft ? (saved.publishedVersion ?? expected) : saved.definition.version,
      );
      this.draftRevision.set(saved.draftRevision);
      this.backupStatus.set(saved.backupStatus);
      const changed = this.nodes() !== nodes || this.connections() !== connections;
      if (!changed && this.flowId !== saved.definition.id)
        void this.editorRouter.navigate(['/flows', saved.definition.id, 'edit'], {
          replaceUrl: true,
        });
      this.notifications.show(
        draft
          ? 'Brouillon enregistré.'
          : 'Version ' +
              saved.definition.version +
              ' publiée. Sauvegarde JSON : ' +
              (saved.backupStatus === 'ready' ? 'disponible.' : 'en attente.') +
              (changed ? ' Des modifications plus récentes restent à enregistrer.' : ''),
      );
    } catch (error) {
      const message =
        error instanceof HttpErrorResponse && error.status === 409
          ? 'Conflit de version : rechargez le flow avant de réessayer.'
          : error instanceof HttpErrorResponse && error.status === 422
            ? error.error.issues.map((issue: { message: string }) => issue.message).join(' ')
            : 'Enregistrement non confirmé. Votre flow reste dans l’éditeur.';
      this.notifications.show(message);
    } finally {
      this.saving.set(false);
    }
  }
  protected readonly validating = signal(false);
  protected async validateGraph(): Promise<void> {
    if (this.validating()) return;
    this.validating.set(true);
    const checkedNodes = this.nodes();
    const checkedConnections = this.connections();
    try {
      const result = await this.serverValidation.validate(
        this.validationFlowId,
        this.flowTitle(),
        this.flowMediaType(),
        checkedNodes,
        checkedConnections,
      );
      if (this.nodes() !== checkedNodes || this.connections() !== checkedConnections) {
        this.notifications.show(
          'Le flow a été modifié pendant la vérification. Vérifiez sa nouvelle version.',
        );
        return;
      }
      this.notifications.show(
        result.isValid
          ? 'Le backend confirme la validité du flow. Le moteur ne lance pas encore de traitement.'
          : result.issues.map((issue) => issue.path + ' : ' + issue.message).join(' '),
      );
    } catch {
      this.notifications.show(
        'Validation backend indisponible. La validité du flow n’a pas été confirmée.',
      );
    } finally {
      this.validating.set(false);
    }
  }

  protected saveNodeConfiguration(configuration: FlowComponentConfiguration): void {
    const nodeId = this.configuringNodeId();
    if (!nodeId) return;
    if (this.configuringNode()?.component.kind === 'input') {
      const inputComponent = getFlowInputComponents(this.flowMediaType()).find(
        (component) => component.id === configuration['inputComponentId'],
      );
      if (!inputComponent) {
        this.notifications.show('Choisissez une entrée compatible avec le type de flow.');
        return;
      }
      this.nodes.update((nodes) =>
        nodes.map((node) =>
          node.id === nodeId
            ? { ...node, reference: undefined, component: inputComponent, configuration: {} }
            : node,
        ),
      );
      this.configurationOpen.set(false);
      return;
    }
    this.nodes.update((nodes) =>
      nodes.map((node) =>
        node.id === nodeId
          ? { ...node, configuration: { ...node.configuration, ...configuration } }
          : node,
      ),
    );
    this.configurationOpen.set(false);
  }

  protected onConnectionRequested(event: {
    readonly sourceNodeId: string;
    readonly sourceOutputId: string;
    readonly targetNodeId: string;
  }): void {
    if (!canConnectFlowGraph(this.nodes(), this.connections(), event)) {
      this.notifications.show(
        'Connexion impossible : vérifiez la sortie, le point d’entrée et l’absence de cycle.',
      );
      return;
    }
    this.setGraph(
      [...this.nodes()],
      [
        ...this.connections(),
        this.createConnection(event.sourceNodeId, event.sourceOutputId, event.targetNodeId),
      ],
    );
  }

  protected onConnectionsChanged(connections: readonly FlowGraphConnection[]): void {
    const nodes = this.nodes();
    if (
      connections.some(
        (edge, index) =>
          !canConnectFlowGraph(
            nodes,
            connections.filter((_, other) => other !== index),
            edge,
          ),
      )
    ) {
      this.notifications.show('Cette réorganisation produirait une connexion invalide.');
      return;
    }
    this.setGraph(nodes, connections);
  }

  protected removeNode(nodeId: string): void {
    if (this.nodes().find((node) => node.id === nodeId)?.component.kind === 'input') return;
    const incoming = this.connections().filter((edge) => edge.targetNodeId === nodeId);
    const outgoing = this.connections().filter((edge) => edge.sourceNodeId === nodeId);
    let remaining = this.connections().filter(
      (edge) => edge.sourceNodeId !== nodeId && edge.targetNodeId !== nodeId,
    );
    if (outgoing.length === 1)
      remaining = [
        ...remaining,
        ...incoming.map((edge) => ({ ...edge, targetNodeId: outgoing[0].targetNodeId })),
      ];
    this.setGraph(
      this.nodes().filter((node) => node.id !== nodeId),
      remaining,
    );
    if (outgoing.length > 1)
      this.notifications.show(
        'Étape supprimée. Les blocs de ses branches sont conservés parmi les blocs non reliés.',
      );
  }

  protected removeConnection(connectionId: string): void {
    const connections = this.connections().filter((connection) => connection.id !== connectionId);
    if (connections.length !== this.connections().length)
      this.setGraph([...this.nodes()], connections);
  }

  private createConnection(
    sourceNodeId: string,
    sourceOutputId: string,
    targetNodeId: string,
  ): FlowGraphConnection {
    return {
      id: `connection-${this.nextConnectionId++}`,
      sourceNodeId,
      sourceOutputId,
      targetNodeId,
    };
  }

  private setGraph(
    nodes: readonly FlowGraphNode[],
    connections: readonly FlowGraphConnection[],
  ): void {
    this.nodes.set(nodes);
    this.connections.set(connections);
  }
}
