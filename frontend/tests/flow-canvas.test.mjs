import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';
const { layoutFlowCanvas } = await loadModel('flow-canvas');
const node = (id) => ({ id, position: { x: 0, y: 0 } }),
  edge = (sourceNodeId, targetNodeId) => ({ sourceNodeId, targetNodeId });
test('five decision levels and reconvergences are laid out without graph mutation or overlap', () => {
  const nodes = [
    'entry',
    ...Array.from({ length: 5 }, (_, i) => ['decision' + i, 'alternate' + i]).flat(),
    'join',
    'detached',
  ].map(node);
  const edges = [
    edge('entry', 'decision0'),
    ...Array.from({ length: 5 }, (_, i) => [
      edge('decision' + i, 'alternate' + i),
      edge('alternate' + i, 'join'),
      edge('decision' + i, i === 4 ? 'join' : 'decision' + (i + 1)),
    ]).flat(),
  ];
  const before = JSON.stringify({ nodes, edges }),
    layout = layoutFlowCanvas(nodes, edges);
  assert.equal(JSON.stringify({ nodes, edges }), before);
  assert.equal(layout.positions.size, nodes.length);
  for (const e of edges)
    assert.ok(layout.positions.get(e.targetNodeId).x > layout.positions.get(e.sourceNodeId).x);
  const ps = [...layout.positions.values()];
  for (let i = 0; i < ps.length; i++)
    for (let j = i + 1; j < ps.length; j++)
      assert.ok(ps[i].x !== ps[j].x || Math.abs(ps[i].y - ps[j].y) >= 250);
});
test('detached nodes, invalid references and cyclic drafts remain visible for repair', () => {
  const nodes = ['a', 'b', 'alone'].map(node);
  const layout = layoutFlowCanvas(nodes, [edge('a', 'b'), edge('b', 'a'), edge('a', 'missing')]);
  assert.equal(layout.positions.size, 3);
  assert.ok(layout.width >= 1400);
});
