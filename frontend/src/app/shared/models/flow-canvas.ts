import { FlowGraphConnection, FlowGraphNode, FlowGraphPosition } from './flow-component';

export interface FlowCanvasLayout {
  readonly positions: ReadonlyMap<string, FlowGraphPosition>;
  readonly width: number;
  readonly height: number;
}

/** A projection only: never changes the graph, including detached nodes and convergences. */
export function layoutFlowCanvas(
  nodes: readonly FlowGraphNode[],
  edges: readonly FlowGraphConnection[],
): FlowCanvasLayout {
  const ids = new Set(nodes.map((node) => node.id));
  const valid = edges.filter((edge) => ids.has(edge.sourceNodeId) && ids.has(edge.targetNodeId));
  const degree = new Map(
    nodes.map((node) => [node.id, valid.filter((e) => e.targetNodeId === node.id).length]),
  );
  const ranks = new Map(nodes.map((node) => [node.id, 0]));
  const pending = nodes.filter((node) => degree.get(node.id) === 0).map((node) => node.id);
  for (let i = 0; i < pending.length; i++) {
    const id = pending[i];
    for (const edge of valid.filter((candidate) => candidate.sourceNodeId === id)) {
      ranks.set(edge.targetNodeId, Math.max(ranks.get(edge.targetNodeId)!, ranks.get(id)! + 1));
      degree.set(edge.targetNodeId, degree.get(edge.targetNodeId)! - 1);
      if (degree.get(edge.targetNodeId) === 0) pending.push(edge.targetNodeId);
    }
  }
  // Invalid/cyclic imported drafts must remain visible for repair.
  for (const node of nodes) if (!pending.includes(node.id)) pending.push(node.id);
  const positions = new Map<string, FlowGraphPosition>();
  const lanes = new Map<number, number>();
  for (const id of pending) {
    const rank = ranks.get(id)!;
    const lane = lanes.get(rank) ?? 0;
    positions.set(id, { x: 96 + rank * 380, y: 96 + lane * 250 });
    lanes.set(rank, lane + 1);
  }
  return {
    positions,
    width: Math.max(1400, ...[...positions.values()].map((p) => p.x + 800)),
    height: Math.max(900, ...[...positions.values()].map((p) => p.y + 600)),
  };
}
