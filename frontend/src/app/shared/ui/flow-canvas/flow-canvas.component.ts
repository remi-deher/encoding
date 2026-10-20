import { IconComponent } from '../icon.component';
import { FlowSimulationStatusComponent } from '../flow-simulation-status.component';
import { SimulationResult } from '../../models/flow-simulation';
import { MediaType } from '../../models/media-type';
import { flowBlockSummary } from '../../models/flow-block-summary';
import { FlowComponentDrop, FlowPaletteDrop } from '../flow-palette-drag';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  signal,
  input,
  output,
  ElementRef,
  TemplateRef,
  viewChild,
  inject,
  DestroyRef,
  afterRenderEffect,
  untracked,
} from '@angular/core';
import { CdkDrag, CdkDragHandle, CdkDragEnd } from '@angular/cdk/drag-drop';
import { MatButtonModule } from '@angular/material/button';
import {
  FlowGraphNode,
  FlowGraphConnection,
  FlowConnectionRequest,
  FlowGraphPosition,
  FlowComponentOutput,
} from '../../models/flow-component';
import { layoutFlowCanvas } from '../../models/flow-canvas';
import { canConnectFlowGraph } from '../../models/flow-graph';
import { flowComponentColors } from '../../models/flow-presentation';
import { ContextActionsDirective, ContextActionPosition } from '../context-actions.directive';
import {
  ContextActionMenuComponent,
  ContextActionMenuData,
} from '../context-action-menu.component';
import { NotificationService } from '../../services/notification.service';
@Component({
  selector: 'app-flow-canvas',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FlowComponentDrop,
    IconComponent,
    FlowSimulationStatusComponent,
    CdkDrag,
    CdkDragHandle,
    MatButtonModule,
    ContextActionsDirective,
    ContextActionMenuComponent,
  ],
  templateUrl: './flow-canvas.component.html',
  styleUrl: './flow-canvas.component.scss',
})
export class FlowCanvasComponent {
  readonly mediaType = input<MediaType>('video');
  readonly simulationResult = input<SimulationResult | null>(null);
  readonly simulationDetailsRequested = output<void>();
  protected summary(node: FlowGraphNode): string {
    return flowBlockSummary(node, this.mediaType());
  }
  readonly componentDropped = output<FlowPaletteDrop>();
  protected dropComponent(event: FlowPaletteDrop): void {
    const view = this.viewport()?.nativeElement;
    if (!view) return;
    const rect = view.getBoundingClientRect();
    this.componentDropped.emit({
      ...event,
      position: {
        x: Math.max(24, (event.point.x - rect.left + view.scrollLeft) / this.scale()),
        y: Math.max(24, (event.point.y - rect.top + view.scrollTop) / this.scale()),
      },
    });
  }
  readonly toolbar = viewChild<TemplateRef<unknown>>('toolbar');
  readonly nodes = input.required<readonly FlowGraphNode[]>();
  readonly connections = input.required<readonly FlowGraphConnection[]>();
  readonly selectedNodeId = input<string | null>(null);
  readonly selectNode = output<string>();
  readonly configure = output<string>();
  readonly connect = output<FlowConnectionRequest>();
  readonly disconnect = output<string>();
  readonly remove = output<string>();
  readonly duplicate = output<string>();
  readonly askAi = output<string>();
  readonly insert = output<{ sourceNodeId: string; output: FlowComponentOutput }>();
  readonly positionChanged = output<{ nodeId: string; position: FlowGraphPosition }>();
  readonly guided = output<string>();
  readonly positions = input<ReadonlyMap<string, FlowGraphPosition>>(new Map());
  private readonly viewport = viewChild<ElementRef<HTMLElement>>('viewport');
  private readonly notifications = inject(NotificationService);
  protected readonly layout = computed(() => layoutFlowCanvas(this.nodes(), this.connections()));
  protected readonly extent = signal({ width: 0, height: 0 });
  protected readonly size = computed(() => ({
    width: Math.max(
      this.layout().width,
      this.extent().width,
      ...[...this.positions().values()].map((p) => p.x + 900),
    ),
    height: Math.max(
      this.layout().height,
      this.extent().height,
      ...[...this.positions().values()].map((p) => p.y + 600),
    ),
  }));
  protected readonly scale = signal(1);
  protected readonly pending = signal<{ node: FlowGraphNode; output: FlowComponentOutput } | null>(
    null,
  );
  protected readonly pointer = signal<FlowGraphPosition | null>(null);
  protected readonly context = signal<ContextActionMenuData | null>(null);
  private contextTarget: { node?: FlowGraphNode; edge?: FlowGraphConnection } = {};
  private stopDrag?: () => void;
  protected readonly colors = flowComponentColors;
  constructor() {
    inject(DestroyRef).onDestroy(() => this.stopDrag?.());
    afterRenderEffect(() => {
      const id = this.selectedNodeId(),
        view = this.viewport()?.nativeElement;
      if (!id || !view) return;
      untracked(() => {
        const node = this.nodes().find((n) => n.id === id);
        if (!node) return;
        const p = this.position(node),
          left = p.x * this.scale(),
          top = p.y * this.scale();
        if (
          left < view.scrollLeft ||
          left + 300 * this.scale() > view.scrollLeft + view.clientWidth ||
          top < view.scrollTop ||
          top + 180 * this.scale() > view.scrollTop + view.clientHeight
        )
          view.scrollTo({
            left: Math.max(0, left - view.clientWidth / 2 + 150 * this.scale()),
            top: Math.max(0, top - view.clientHeight / 2 + 90 * this.scale()),
          });
      });
    });
  }
  protected edit(node: FlowGraphNode): void {
    if (node.component.kind === 'pack') this.guided.emit(node.id);
    else this.configure.emit(node.id);
  }
  protected position(node: FlowGraphNode): FlowGraphPosition {
    return (
      this.positions().get(node.id) ?? this.layout().positions.get(node.id) ?? { x: 80, y: 80 }
    );
  }
  protected move(node: FlowGraphNode, event: CdkDragEnd): void {
    const p = this.position(node),
      d = event.distance;
    this.positionChanged.emit({
      nodeId: node.id,
      position: {
        x: Math.max(24, p.x + d.x / this.scale()),
        y: Math.max(24, p.y + d.y / this.scale()),
      },
    });
    event.source.reset();
  }
  protected zoom(delta: number): void {
    this.scale.update((s) => Math.max(0.2, Math.min(1.75, s + delta)));
  }
  protected fit(): void {
    const view = this.viewport()?.nativeElement;
    if (!view) return;
    this.scale.set(
      Math.max(
        0.2,
        Math.min(
          1,
          (view.clientWidth - 40) / this.size().width,
          (view.clientHeight - 40) / this.size().height,
        ),
      ),
    );
    view.scrollTo(0, 0);
  }
  protected navigate(): void {
    const view = this.viewport()?.nativeElement;
    if (!view) return;
    if (view.scrollLeft + view.clientWidth > this.size().width * this.scale() - 180)
      this.extent.update((s) => ({ ...s, width: this.size().width + 900 }));
    if (view.scrollTop + view.clientHeight > this.size().height * this.scale() - 180)
      this.extent.update((s) => ({ ...s, height: this.size().height + 700 }));
  }
  protected pan(event: PointerEvent): void {
    if (
      event.button !== 0 ||
      !(event.target instanceof Element) ||
      event.target.closest('article,button,path')
    )
      return;
    const view = this.viewport()!.nativeElement,
      x = event.clientX,
      y = event.clientY,
      left = view.scrollLeft,
      top = view.scrollTop;
    view.setPointerCapture(event.pointerId);
    const move = (e: PointerEvent) => {
      view.scrollLeft = left - (e.clientX - x);
      view.scrollTop = top - (e.clientY - y);
    };
    const end = () => {
      view.removeEventListener('pointermove', move);
      view.removeEventListener('pointerup', end);
      view.removeEventListener('pointercancel', end);
    };
    view.addEventListener('pointermove', move);
    view.addEventListener('pointerup', end, { once: true });
    view.addEventListener('pointercancel', end, { once: true });
  }
  protected path(edge: FlowGraphConnection): string {
    const a = this.nodes().find((n) => n.id === edge.sourceNodeId),
      b = this.nodes().find((n) => n.id === edge.targetNodeId);
    if (!a || !b) return '';
    const s = this.position(a),
      t = this.position(b),
      i = a.component.outputs.findIndex((o) => o.id === edge.sourceOutputId),
      y = s.y + 100 + i * 40;
    return `M ${s.x + 300} ${y} C ${s.x + 380} ${y} ${t.x - 80} ${t.y + 50} ${t.x} ${t.y + 50}`;
  }
  protected ghost(): string {
    const pending = this.pending(),
      p = this.pointer();
    if (!pending || !p) return '';
    const a = this.position(pending.node),
      y =
        a.y +
        100 +
        pending.node.component.outputs.findIndex((o) => o.id === pending.output.id) * 40;
    return `M ${a.x + 300} ${y} C ${a.x + 380} ${y} ${p.x - 80} ${p.y} ${p.x} ${p.y}`;
  }
  protected begin(node: FlowGraphNode, output: FlowComponentOutput, event?: PointerEvent): void {
    event?.stopPropagation();
    if (
      this.connections().some((e) => e.sourceNodeId === node.id && e.sourceOutputId === output.id)
    ) {
      this.notifications.show('Déconnectez cette sortie avant de la relier à une autre entrée.');
      return;
    }
    this.pending.set({ node, output });
    if (!event) return;
    const move = (e: PointerEvent) => {
      const root = this.viewport()?.nativeElement;
      if (!root) return;
      const rect = root.getBoundingClientRect();
      this.pointer.set({
        x: (e.clientX - rect.left + root.scrollLeft) / this.scale(),
        y: (e.clientY - rect.top + root.scrollTop) / this.scale(),
      });
    };
    const up = (e: PointerEvent) => {
      const element = document
        .elementFromPoint(e.clientX, e.clientY)
        ?.closest<HTMLElement>('[data-canvas-input]');
      if (element) this.finish(element.dataset['canvasInput']!);
      this.pointer.set(null);
      this.stopDrag?.();
    };
    this.stopDrag?.();
    document.addEventListener('pointermove', move);
    document.addEventListener('pointerup', up, { once: true });
    const cancel = () => {
      this.pending.set(null);
      this.pointer.set(null);
      this.stopDrag?.();
    };
    document.addEventListener('pointercancel', cancel, { once: true });
    this.stopDrag = () => {
      document.removeEventListener('pointermove', move);
      document.removeEventListener('pointerup', up);
      document.removeEventListener('pointercancel', cancel);
      this.stopDrag = undefined;
    };
  }
  protected canFinish(node: FlowGraphNode): boolean {
    const p = this.pending();
    return (
      !!p &&
      canConnectFlowGraph(this.nodes(), this.connections(), {
        sourceNodeId: p.node.id,
        sourceOutputId: p.output.id,
        targetNodeId: node.id,
      })
    );
  }
  protected finish(id: string): void {
    const node = this.nodes().find((n) => n.id === id);
    if (!node || !this.canFinish(node)) return;
    const p = this.pending()!;
    this.connect.emit({ sourceNodeId: p.node.id, sourceOutputId: p.output.id, targetNodeId: id });
    this.pending.set(null);
    this.pointer.set(null);
  }
  protected menu(
    position: ContextActionPosition,
    node?: FlowGraphNode,
    edge?: FlowGraphConnection,
  ): void {
    this.contextTarget = { node, edge };
    this.context.set({
      ...position,
      items: node
        ? [
            { id: 'configure', label: 'Configurer', disabled: node.component.kind === 'pack' },
            {
              id: 'duplicate',
              label: 'Dupliquer',
              disabled: node.component.kind === 'input' || node.component.kind === 'pack',
            },
            { id: 'guided', label: 'Afficher dans Guidé' },
            { id: 'ai', label: 'Demander à l’IA' },
            { id: 'remove', label: 'Supprimer', disabled: node.component.kind === 'input' },
          ]
        : edge
          ? [
              { id: 'insert', label: 'Insérer une étape' },
              { id: 'disconnect', label: 'Supprimer la liaison' },
              { id: 'ai-edge', label: 'Expliquer cette liaison avec l’IA' },
            ]
          : [
              { id: 'fit', label: 'Tout afficher' },
              { id: 'zoom', label: 'Zoom à 100 %' },
            ],
    });
  }
  protected menuButton(event: MouseEvent, node: FlowGraphNode): void {
    event.stopPropagation();
    const origin = event.currentTarget as HTMLElement;
    const r = origin.getBoundingClientRect();
    this.menu({ x: r.left, y: r.bottom, origin }, node);
  }
  protected action(id: string): void {
    const { node, edge } = this.contextTarget;
    this.context.set(null);
    if (id === 'configure' && node) this.configure.emit(node.id);
    if (id === 'duplicate' && node) this.duplicate.emit(node.id);
    if (id === 'remove' && node) this.remove.emit(node.id);
    if (id === 'guided' && node) this.guided.emit(node.id);
    if (id === 'ai' && node) this.askAi.emit(node.id);
    if (id === 'ai-edge' && edge) this.askAi.emit(edge.sourceNodeId);
    if (id === 'disconnect' && edge) this.disconnect.emit(edge.id);
    if (id === 'insert' && edge) {
      const source = this.nodes().find((n) => n.id === edge.sourceNodeId),
        output = source?.component.outputs.find((o) => o.id === edge.sourceOutputId);
      if (source && output) this.insert.emit({ sourceNodeId: source.id, output });
    }
    if (id === 'fit') this.fit();
    if (id === 'zoom') this.scale.set(1);
  }
}
