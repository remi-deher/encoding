import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';
const { localNodeSheet, formatLastSignal } = await loadModel('node-management');

const NOW = Date.parse('2026-10-15T12:00:00Z');
const status = {
  nodeId: 'n1', name: 'nas', roles: ['server', 'worker'], maxConcurrentTasks: 2,
  capabilities: [{ toolId: 'ffmpeg', version: '7.1', mediaTypes: [], accelerationTypes: ['vaapi'],
    encoderChecks: [{ encoderId: 'libx265', status: 'usable' }, { encoderId: 'hevc_vaapi', status: 'detected' }] }],
  clusterName: 'Maison', switchFailure: null,
};
const node = {
  id: 'n1', displayName: 'NAS principal', lastSeenAt: '2026-10-15T11:59:56Z', desiredRoles: null,
  inventory: { operatingSystem: 'Linux', architecture: 'X64', processorCount: 16, memoryMb: 32768,
    processorModel: 'AMD Ryzen 7 5700G', gpus: [{ name: 'AMD 0x1638', driver: 'amdgpu', device: 'card0' }] },
};
const base = { status, node, mode: { server: true, worker: true }, standalone: false, clusterName: 'Maison',
  agentUnavailable: false, activeTasks: 1, now: NOW };
const section = (sheet, id) => sheet.sections.find((s) => s.id === id);

test('a healthy member shows load, hardware, CPU model, GPU and tools', () => {
  const sheet = localNodeSheet(base);
  assert.equal(sheet.title, 'NAS principal');
  assert.equal(sheet.subtitle, 'Membre de Maison');
  assert.equal(sheet.notices.length, 0);
  const [load, signal] = section(sheet, 'state').metrics;
  assert.equal(load.value, '1 / 2');
  assert.equal(load.meter.stale, false);
  assert.equal(signal.value, 'À l’instant');
  const [cpu, gpu] = section(sheet, 'hardware').metrics;
  assert.equal(cpu.value, 'AMD Ryzen 7 5700G');
  assert.match(cpu.detail, /32 Go/);
  assert.match(gpu.detail, /pilote amdgpu/);
  const tool = section(sheet, 'capabilities').items[0];
  assert.deepEqual(tool.badges.map((b) => b.label), ['1 encodeur(s) vérifié(s)', '1 détecté(s), non vérifié(s)', 'VAAPI']);
});

test('a disabled worker has no processing metric and no tools', () => {
  const sheet = localNodeSheet({ ...base, mode: { server: true, worker: false }, activeTasks: null });
  assert.equal(section(sheet, 'state').metrics[0].value, 'Non applicable');
  assert.equal(section(sheet, 'capabilities').items.length, 0);
});

test('an unreachable agent keeps last known values and never shows zero', () => {
  const sheet = localNodeSheet({ ...base, agentUnavailable: true, activeTasks: null, now: NOW + 14 * 60_000 });
  assert.equal(sheet.notices[0].tone, 'danger');
  assert.match(section(sheet, 'state').items[0].badges[0].label, /Dernier état connu/);
  assert.equal(section(sheet, 'state').metrics[0].value, 'Indisponible');
  const stale = localNodeSheet({ ...base, agentUnavailable: true });
  assert.equal(section(stale, 'state').metrics[0].meter.stale, true);
});

test('a pending role change is shown apart from the applied roles', () => {
  const sheet = localNodeSheet({ ...base, node: { ...node, desiredRoles: ['server'] } });
  assert.equal(sheet.notices[0].title, 'Changement de fonctions en attente');
  const worker = section(sheet, 'state').items[1].badges.map((b) => b.label);
  assert.deepEqual(worker, ['Actif', 'Demandé : inactif']);
});

test('an old agent without GPU data says so instead of claiming none', () => {
  const old = { ...node, inventory: { ...node.inventory, processorModel: null, gpus: null } };
  const [cpu, gpu] = section(localNodeSheet({ ...base, node: old }), 'hardware').metrics;
  assert.equal(cpu.value, '16 cœurs');
  assert.equal(gpu.value, 'Non déclaré');
});

test('a standalone node names no cluster and an invalid date stays unknown', () => {
  const sheet = localNodeSheet({ ...base, standalone: true, clusterName: null });
  assert.equal(sheet.subtitle, 'Nœud autonome');
  assert.equal(formatLastSignal('n’importe quoi'), null);
});
