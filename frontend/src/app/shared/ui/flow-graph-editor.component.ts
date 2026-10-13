import { Component, computed, input, output } from '@angular/core';
import { MatMenuModule } from '@angular/material/menu';
import { Connection, ConnectionSettings, createEdges, createNodes, Node, Vflow } from 'ngx-vflow';
import { FlowComponentDefinition, FlowComponentOutput, FlowConnectionRequest, FlowGraphConnection, FlowGraphNode, FlowGraphPosition } from '../models/flow-component';

interface FlowCanvasNodeData {
  readonly component: FlowComponentDefinition;
  readonly activeOutputs: readonly FlowComponentOutput[];
}

interface BranchRequest {
  readonly sourceNodeId: string;
  readonly output: FlowComponentOutput;
}

@Component({
  selector: 'app-flow-graph-editor',
  imports: [MatMenuModule, Vflow],
  templateUrl: './flow-graph-editor.component.html',
  styleUrl: './flow-graph-editor.component.scss',
})
export class FlowGraphEditorComponent {
  readonly nodes = input.required<readonly FlowGraphNode[]>();
  readonly connections = input.required<readonly FlowGraphConnection[]>();
  readonly addComponentRequested = output<void>();
  readonly configurationRequested = output<string>();
  readonly branchComponentRequested = output<BranchRequest>();
  readonly connectionRequested = output<FlowConnectionRequest>();
  readonly nodePositionChanged = output<{ readonly nodeId: string; readonly position: FlowGraphPosition }>();
  readonly nodeRemoved = output<string>();
  readonly connectionRemoved = output<string>();

  protected readonly canvasNodes = computed(() => createNodes(this.nodes().map((node) => {
    const activeOutputs = this.activeOutputs(node);
    const hasAvailableOutputs = this.availableOutputs(node.id, node.component).length > 0;
    return {
      id: node.id,
      point: { ...node.position },
      type: 'html-template' as const,
      width: 280,
      height: 135 + activeOutputs.length * 25 + (hasAvailableOutputs ? 34 : 0),
      data: { component: node.component, activeOutputs } satisfies FlowCanvasNodeData,
    };
  })));
  protected readonly canvasEdges = computed(() => createEdges(this.connections().map((connection) => ({
    id: connection.id,
    source: connection.sourceNodeId,
    sourceHandle: connection.sourceOutputId,
    target: connection.targetNodeId,
    targetHandle: 'input',
    curve: 'smooth-step' as const,
  }))));

  protected readonly connectionSettings: ConnectionSettings = {
    validator: (connection) => this.isValidConnection(connection),
  };

  protected requestOutput(sourceNodeId: string, output: FlowComponentOutput): void {
    this.branchComponentRequested.emit({ sourceNodeId, output });
  }

  protected availableOutputs(nodeId: string, component: FlowComponentDefinition): readonly FlowComponentOutput[] {
    const connectedOutputIds = new Set(
      this.connections().filter((connection) => connection.sourceNodeId === nodeId).map((connection) => connection.sourceOutputId),
    );
    return component.outputs.filter((output) => !connectedOutputIds.has(output.id));
  }

  protected outputLabel(outputId: string, component: FlowComponentDefinition): string {
    return component.outputs.find((output) => output.id === outputId)?.label ?? outputId;
  }

  protected kindLabel(kind: FlowComponentDefinition['kind']): string {
    return { action: 'Action', condition: 'Condition', variable: 'Variable' }[kind];
  }

  protected onConnect(connection: Connection): void {
    if (!connection.sourceHandle || connection.targetHandle !== 'input') return;
    this.connectionRequested.emit({
      sourceNodeId: connection.source,
      sourceOutputId: connection.sourceHandle,
      targetNodeId: connection.target,
    });
  }

  protected onNodeDragEnd(event: { readonly node: Node }): void {
    const point = event.node.point();
    this.nodePositionChanged.emit({ nodeId: event.node.id, position: { x: point.x, y: point.y } });
  }

  protected onEdgesRemoved(changes: readonly { readonly id: string }[]): void {
    for (const change of changes) this.connectionRemoved.emit(change.id);
  }

  private activeOutputs(node: FlowGraphNode): readonly FlowComponentOutput[] {
    const ids = new Set(this.connections().filter((edge) => edge.sourceNodeId === node.id).map((edge) => edge.sourceOutputId));
    return node.component.outputs.filter((output) => ids.has(output.id));
  }

  private isValidConnection(connection: Connection): boolean {
    const { source, target, sourceHandle, targetHandle } = connection;
    if (!sourceHandle || targetHandle !== 'input' || source === target) return false;
    const sourceNode = this.nodes().find((node) => node.id === source);
    const targetNode = this.nodes().find((node) => node.id === target);
    if (!sourceNode || !targetNode || !sourceNode.component.outputs.some((output) => output.id === sourceHandle)) return false;
    if (this.connections().some((edge) => edge.targetNodeId === target)) return false;
    return !this.reaches(target, source, new Set());
  }

  private reaches(currentNodeId: string, soughtNodeId: string, visited: Set<string>): boolean {
    if (currentNodeId === soughtNodeId) return true;
    if (visited.has(currentNodeId)) return false;
    visited.add(currentNodeId);
    return this.connections()
      .filter((connection) => connection.sourceNodeId === currentNodeId)
      .some((connection) => this.reaches(connection.targetNodeId, soughtNodeId, visited));
  }
}
