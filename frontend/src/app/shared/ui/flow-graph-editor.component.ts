import { routeFlowLink } from './flow-link-routing';
import { FlowLinks, FlowLinkDrawing } from './flow-links';
import { FlowInlineParameters } from './flow-inline-parameters';
import { MediaType } from '../models/media-type';
import {
  getFlowConfigurationSchema,
  FlowComponentConfigurationSchema,
  FlowComponentConfigurationValue,
} from '../models/flow-component';
import { flowComponentColors } from '../models/flow-presentation';
import {
  Component,
  computed,
  input,
  output,
  inject,
  ElementRef,
  viewChildren,
  viewChild,
  signal,
  afterRenderEffect,
  DestroyRef,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { CdkDrag, CdkDragHandle, CdkDragDrop, CdkDropList } from '@angular/cdk/drag-drop';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatExpansionModule } from '@angular/material/expansion';
import { NotificationService } from '../services/notification.service';
import {
  FLOW_COMPONENT_KIND_LABELS,
  FlowComponentDefinition,
  FlowComponentOutput,
  FlowConnectionRequest,
  FlowGraphConnection,
  FlowGraphNode,
} from '../models/flow-component';
import { canConnectFlowGraph } from '../models/flow-graph';
import { FlowComponentPackInstance } from '../models/flow-validation';
import { projectFlowPacks } from '../models/flow-pack-projection';
import {
  FlowSequence,
  FlowContinuationSource,
  flowContinuationSources,
  structureFlow,
  reorderFlowSequence,
} from '../models/flow-sequence';

@Component({
  selector: 'app-flow-graph-editor',
  imports: [
    NgTemplateOutlet,
    FlowInlineParameters,
    FlowLinks,
    CdkDrag,
    CdkDragHandle,
    CdkDropList,
    MatButtonModule,
    MatMenuModule,
    MatExpansionModule,
  ],
  templateUrl: './flow-graph-editor.component.html',
  styleUrl: './flow-graph-editor.component.scss',
})
export class FlowGraphEditorComponent {
  readonly mediaType = input.required<MediaType>();
  readonly configurationChanged = output<{
    readonly nodeId: string;
    readonly fieldId: string;
    readonly value: FlowComponentConfigurationValue;
  }>();
  readonly nodes = input.required<readonly FlowGraphNode[]>();
  readonly connections = input.required<readonly FlowGraphConnection[]>();
  readonly componentPacks = input<readonly FlowComponentPackInstance[]>([]);
  readonly configurationRequested = output<string>();
  readonly branchComponentRequested = output<{
    readonly sourceNodeId: string;
    readonly output: FlowComponentOutput;
  }>();
  readonly terminationRequested = output<{
    readonly sourceNodeId: string;
    readonly output: FlowComponentOutput;
  }>();
  readonly continuationRequested = output<readonly FlowContinuationSource[]>();
  readonly connectionRequested = output<FlowConnectionRequest>();
  readonly connectionsChanged = output<readonly FlowGraphConnection[]>();
  readonly nodeRemoved = output<string>();
  readonly connectionRemoved = output<string>();
  private readonly cards = viewChildren<ElementRef<HTMLElement>>('stepCard');
  private readonly notifications = inject(NotificationService);
  protected readonly projection = computed(() =>
    projectFlowPacks(this.nodes(), this.connections(), this.componentPacks()),
  );
  protected readonly visualNodes = computed(() => this.projection().nodes);
  protected readonly visualConnections = computed(() => this.projection().connections);
  protected readonly drawnGraphConnections = computed(() => {
    const expanded = this.componentPacks().filter((pack) => this.expandedPacks().has(pack.id));
    const internal = this.connections().filter((edge) =>
      expanded.some(
        (pack) =>
          pack.nodeIds.includes(edge.sourceNodeId) && pack.nodeIds.includes(edge.targetNodeId),
      ),
    );
    return [...this.visualConnections(), ...internal];
  });
  protected readonly structured = computed(() =>
    structureFlow(this.visualNodes(), this.visualConnections()),
  );
  private readonly surface = viewChild<ElementRef<HTMLElement>>('surface');
  private readonly ports = viewChildren<ElementRef<HTMLElement>>('outputPort');
  private readonly inputs = viewChildren<ElementRef<HTMLElement>>('inputPort');
  private observer?: ResizeObserver;
  protected readonly showConnections = signal(true);
  protected readonly expandedPacks = signal<ReadonlySet<string>>(new Set());
  protected readonly pendingConnection = signal<FlowContinuationSource | null>(null);
  protected readonly drawnConnections = signal<readonly FlowLinkDrawing[]>([]);
  protected readonly drawingSize = signal({ width: 0, height: 0 });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.observer?.disconnect());
    afterRenderEffect(() => {
      this.nodes();
      this.connections();
      this.componentPacks();
      this.expandedPacks();
      this.cards();
      this.ports();
      this.inputs();
      const root = this.surface()?.nativeElement;
      this.observer?.disconnect();
      if (!root) return;
      this.observer = new ResizeObserver(() => this.measureConnections());
      this.observer.observe(root);
      for (const card of this.cards()) this.observer.observe(card.nativeElement);
      this.measureConnections();
    });
  }
  protected measureConnections(): void {
    const root = this.surface()?.nativeElement;
    if (!root) return;
    const origin = root.getBoundingClientRect();
    this.drawingSize.set({ width: root.clientWidth, height: root.scrollHeight });
    const obstacles = this.cards()
      .map((card) => card.nativeElement)
      .filter((card) => !card.closest('mat-expansion-panel:not(.mat-expanded)'))
      .map((card) => {
        const bounds = card.getBoundingClientRect();
        return {
          left: bounds.left - origin.left,
          right: bounds.right - origin.left,
          top: bounds.top - origin.top,
          bottom: bounds.bottom - origin.top,
        };
      });
    const paths = this.drawnGraphConnections().flatMap((edge) => {
      const source = this.ports().find(
        (port) =>
          port.nativeElement.dataset['sourceId'] === edge.sourceNodeId &&
          port.nativeElement.dataset['outputId'] === edge.sourceOutputId,
      )?.nativeElement;
      const target = this.inputs().find(
        (port) => port.nativeElement.dataset['nodeId'] === edge.targetNodeId,
      )?.nativeElement;
      if (!source || !target) return [];
      if (
        source.closest('mat-expansion-panel:not(.mat-expanded)') ||
        target.closest('mat-expansion-panel:not(.mat-expanded)')
      )
        return [];
      const a = source.getBoundingClientRect(),
        b = target.getBoundingClientRect();
      if (!a.width || !b.width) return [];
      const sx = a.left + a.width / 2 - origin.left,
        sy = a.top + a.height / 2 - origin.top;
      const tx = b.left + b.width / 2 - origin.left,
        ty = b.top + b.height / 2 - origin.top;
      const path = routeFlowLink({ x: sx, y: sy }, { x: tx, y: ty }, obstacles, root.clientWidth);
      if (!path) return [];
      const geometry = root.ownerDocument.createElementNS('http://www.w3.org/2000/svg', 'path');
      geometry.setAttribute('d', path);
      const midpoint = geometry.getPointAtLength(geometry.getTotalLength() / 2);
      return [
        {
          id: edge.id,
          path,
          error: edge.sourceOutputId === 'error',
          insertion: { x: midpoint.x, y: midpoint.y },
        },
      ];
    });
    this.drawnConnections.set(paths);
  }
  protected incomingCount(node: FlowGraphNode): number {
    if (this.projection().instanceByChildId.has(node.id))
      return this.connections().filter((edge) => edge.targetNodeId === node.id).length;
    return this.visualConnections().filter((edge) => edge.targetNodeId === node.id).length;
  }
  protected insertOnConnection(id: string): void {
    const edge = this.connections().find((connection) => connection.id === id);
    const node = this.nodes().find((node) => node.id === edge?.sourceNodeId);
    const output = node?.component.outputs.find((output) => output.id === edge?.sourceOutputId);
    if (node && output) this.add(node, output);
  }
  protected selectOutput(node: FlowGraphNode, output: FlowComponentOutput): void {
    if (this.isConnected(node, output)) {
      this.notifications.show(
        'Cette sortie est déjà reliée. Déconnectez-la depuis son menu pour changer la liaison.',
      );
      return;
    }
    const pack = this.packForVisualNode(node);
    const binding = pack?.outputs.find((candidate) => candidate.id === output.id);
    const sourceNodeId = binding?.sourceNodeId ?? node.id;
    const sourceOutput = binding
      ? this.nodes()
          .find((candidate) => candidate.id === sourceNodeId)
          ?.component.outputs.find((candidate) => candidate.id === binding.sourceOutputId)
      : output;
    if (!sourceOutput) return;
    this.pendingConnection.set({ sourceNodeId, output: sourceOutput });
  }
  protected canLinkTo(node: FlowGraphNode): boolean {
    const pending = this.pendingConnection();
    const targetNodeId = this.packForVisualNode(node)?.inputNodeId ?? node.id;
    return (
      !!pending &&
      canConnectFlowGraph(this.nodes(), this.connections(), {
        sourceNodeId: pending.sourceNodeId,
        sourceOutputId: pending.output.id,
        targetNodeId,
      })
    );
  }
  protected linkTo(node: FlowGraphNode): void {
    const pending = this.pendingConnection();
    if (!pending || !this.canLinkTo(node)) return;
    this.connectionRequested.emit({
      sourceNodeId: pending.sourceNodeId,
      sourceOutputId: pending.output.id,
      targetNodeId: this.packForVisualNode(node)?.inputNodeId ?? node.id,
    });
    this.pendingConnection.set(null);
    this.showConnections.set(true);
  }
  protected readonly colors = flowComponentColors;
  protected openConfiguration(node: FlowGraphNode, event: Event): void {
    if (node.component.kind === 'pack') {
      event.preventDefault();
      return;
    }
    const target = event.target;
    if (
      target instanceof Element &&
      target.closest(
        'button,a,input,select,textarea,[role="menuitem"],mat-form-field,app-flow-inline-parameters',
      )
    )
      return;
    event.preventDefault();
    this.configurationRequested.emit(node.id);
  }
  private readonly schemaCache = new Map<
    FlowComponentDefinition,
    { mediaType: MediaType; schema: FlowComponentConfigurationSchema | undefined }
  >();
  protected inlineSchema(node: FlowGraphNode): FlowComponentConfigurationSchema | undefined {
    const mediaType = this.mediaType();
    const cached = this.schemaCache.get(node.component);
    if (cached?.mediaType === mediaType) return cached.schema;
    const schema = getFlowConfigurationSchema(node.component.configurationSchema, mediaType);
    this.schemaCache.set(node.component, { mediaType, schema });
    return schema;
  }
  protected kindLabel(kind: FlowComponentDefinition['kind']): string {
    return FLOW_COMPONENT_KIND_LABELS[kind];
  }
  protected primaryOutput(node: FlowGraphNode): FlowComponentOutput | undefined {
    const edge = this.visualConnections().find((e) => e.sourceNodeId === node.id);
    return (
      node.component.outputs.find((o) => o.id === (edge?.sourceOutputId ?? 'success')) ??
      node.component.outputs[0]
    );
  }
  protected targets(node: FlowGraphNode, output: FlowComponentOutput) {
    const binding = this.packForVisualNode(node)?.outputs.find(
      (candidate) => candidate.id === output.id,
    );
    const sourceNodeId = binding?.sourceNodeId ?? node.id;
    const sourceOutputId = binding?.sourceOutputId ?? output.id;
    return this.nodes().filter((n) =>
      canConnectFlowGraph(this.nodes(), this.connections(), {
        sourceNodeId,
        sourceOutputId,
        targetNodeId: n.id,
      }),
    );
  }
  protected add(node: FlowGraphNode, output: FlowComponentOutput): void {
    const binding = this.packForVisualNode(node)?.outputs.find(
      (candidate) => candidate.id === output.id,
    );
    const sourceNodeId = binding?.sourceNodeId ?? node.id;
    const actualOutput = binding
      ? this.nodes()
          .find((candidate) => candidate.id === sourceNodeId)
          ?.component.outputs.find((candidate) => candidate.id === binding.sourceOutputId)
      : output;
    if (actualOutput) this.branchComponentRequested.emit({ sourceNodeId, output: actualOutput });
  }
  protected disconnect(node: FlowGraphNode, output: FlowComponentOutput): void {
    const binding = this.packForVisualNode(node)?.outputs.find(
      (candidate) => candidate.id === output.id,
    );
    const edge = this.connections().find((candidate) =>
      binding
        ? candidate.sourceNodeId === binding.sourceNodeId &&
          candidate.sourceOutputId === binding.sourceOutputId
        : candidate.sourceNodeId === node.id && candidate.sourceOutputId === output.id,
    );
    if (edge) this.connectionRemoved.emit(edge.id);
  }
  protected isConnected(node: FlowGraphNode, output: FlowComponentOutput): boolean {
    if (this.projection().instanceByChildId.has(node.id))
      return this.connections().some(
        (edge) => edge.sourceNodeId === node.id && edge.sourceOutputId === output.id,
      );
    return this.visualConnections().some(
      (e) => e.sourceNodeId === node.id && e.sourceOutputId === output.id,
    );
  }
  protected move(sequence: FlowSequence, from: number, to: number): void {
    if (sequence.steps.some((step) => step.node.component.kind === 'pack')) return;
    const changed = reorderFlowSequence(
      this.nodes(),
      this.connections(),
      sequence.steps.map((s) => s.node.id),
      from,
      to,
    );
    if (!changed) {
      this.notifications.show(
        'Ce déplacement traverse une entrée, une branche ou une étape partagée. La logique du flow est conservée.',
      );
      return;
    }
    this.connectionsChanged.emit(changed);
  }
  protected canMove(sequence: FlowSequence, from: number, to: number): boolean {
    return (
      !sequence.steps.some((step) => step.node.component.kind === 'pack') &&
      reorderFlowSequence(
        this.nodes(),
        this.connections(),
        sequence.steps.map((s) => s.node.id),
        from,
        to,
      ) !== null
    );
  }
  protected drop(sequence: FlowSequence, event: CdkDragDrop<unknown>): void {
    this.move(sequence, event.previousIndex, event.currentIndex);
  }
  protected focusNode(id: string): void {
    this.cards()
      .find((card) => card.nativeElement.dataset['nodeId'] === id)
      ?.nativeElement.focus();
  }
  protected continuationSources(node: FlowGraphNode): readonly FlowContinuationSource[] {
    const pack = this.packForVisualNode(node);
    if (pack)
      return pack.outputs
        .filter(
          (binding) =>
            !this.connections().some(
              (edge) =>
                edge.sourceNodeId === binding.sourceNodeId &&
                edge.sourceOutputId === binding.sourceOutputId,
            ),
        )
        .flatMap((binding) => {
          const source = this.nodes().find((candidate) => candidate.id === binding.sourceNodeId);
          const output = source?.component.outputs.find(
            (candidate) => candidate.id === binding.sourceOutputId,
          );
          return source && output ? [{ sourceNodeId: source.id, output }] : [];
        });
    return flowContinuationSources(this.nodes(), this.connections(), node.id);
  }
  protected nodeLabel(id: string): string {
    const node = this.nodes().find((candidate) => candidate.id === id);
    return node?.name || node?.component.name || id;
  }
  protected blockLabel(component: FlowComponentDefinition): string {
    return (
      component.pluginName +
      (component.executionToolName ? ' · ' + component.executionToolName : '')
    );
  }

  protected packForVisualNode(node: FlowGraphNode): FlowComponentPackInstance | undefined {
    return this.projection().instancesByVisualId.get(node.id);
  }

  protected packContents(instance: FlowComponentPackInstance) {
    const nodes = this.nodes().filter((node) => instance.nodeIds.includes(node.id));
    const edges = this.connections().filter(
      (edge) =>
        instance.nodeIds.includes(edge.sourceNodeId) &&
        instance.nodeIds.includes(edge.targetNodeId),
    );
    return structureFlow(nodes, edges, instance.inputNodeId);
  }

  protected packExpanded(instance: FlowComponentPackInstance): boolean {
    return this.expandedPacks().has(instance.id);
  }

  protected togglePack(instance: FlowComponentPackInstance): void {
    this.expandedPacks.update((current) => {
      const next = new Set(current);
      if (next.has(instance.id)) next.delete(instance.id);
      else next.add(instance.id);
      return next;
    });
  }

  protected packRootLabel(instance: FlowComponentPackInstance): string {
    const root = this.nodes().find((node) => node.id === instance.inputNodeId);
    return root?.name || root?.component.name || instance.inputNodeId;
  }

  protected connectPackOutput(
    node: FlowGraphNode,
    output: FlowComponentOutput,
    target: FlowGraphNode,
  ): void {
    const binding = this.packForVisualNode(node)?.outputs.find(
      (candidate) => candidate.id === output.id,
    );
    this.connectionRequested.emit({
      sourceNodeId: binding?.sourceNodeId ?? node.id,
      sourceOutputId: binding?.sourceOutputId ?? output.id,
      targetNodeId: target.id,
    });
  }

  protected terminatePackOutput(node: FlowGraphNode, output: FlowComponentOutput): void {
    const binding = this.packForVisualNode(node)?.outputs.find(
      (candidate) => candidate.id === output.id,
    );
    const sourceNodeId = binding?.sourceNodeId ?? node.id;
    const sourceOutput = binding
      ? this.nodes()
          .find((candidate) => candidate.id === sourceNodeId)
          ?.component.outputs.find((candidate) => candidate.id === binding.sourceOutputId)
      : output;
    if (sourceOutput) this.terminationRequested.emit({ sourceNodeId, output: sourceOutput });
  }
}
