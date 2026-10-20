import { FlowGraphConnection, FlowGraphNode, FlowGraphPosition } from './flow-component';

export interface FlowCanvasLayout {
  readonly positions: ReadonlyMap<string, FlowGraphPosition>;
  readonly width: number;
  readonly height: number;
}

export interface FlowCanvasNodeSize {
  readonly width: number;
  readonly height: number;
}

/** A projection only: never changes the graph, including detached nodes and convergences. */
export function layoutFlowCanvas(
  nodes: readonly FlowGraphNode[],
  edges: readonly FlowGraphConnection[],
  sizes: ReadonlyMap<string, FlowCanvasNodeSize> = new Map(),
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
  const rows = new Map<number, string[]>();
  for (const id of pending) {
    const rank = ranks.get(id)!;
    rows.set(rank, [...(rows.get(rank) ?? []), id]);
  }
  const size = (id: string) =>
    sizes.get(id) ?? {
      width: Math.max(
        340,
        (nodes.find((node) => node.id === id)?.component?.outputs.length ?? 0) * 104 + 24,
      ),
      height: 240,
    };
  const rowWidth = (ids: readonly string[]) =>
    ids.reduce((total, id) => total + size(id).width + 64, -64);
  const width = Math.max(340, ...[...rows.values()].map(rowWidth));
  let y = 64;
  for (const [, ids] of [...rows].sort(([a], [b]) => a - b)) {
    let x = 64 + (width - rowWidth(ids)) / 2;
    for (const id of ids) {
      positions.set(id, { x, y });
      x += size(id).width + 64;
    }
    y += Math.max(...ids.map((id) => size(id).height)) + 88;
  }
  return {
    positions,
    width: Math.max(1400, width + 128),
    height: Math.max(900, y),
  };
}
