import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';
const { nodeAcceleratorBadges, nodeHardwareLabel } = await loadModel('node-hardware');

const node = (extra = {}) => ({ roles: ['worker'], capabilities: [], ...extra });
const ffmpeg = (checks, accelerationTypes = []) => ({
  toolId: 'ffmpeg',
  version: '7',
  mediaTypes: ['video'],
  accelerationTypes,
  encoderChecks: checks.map(([encoderId, status]) => ({ encoderId, status })),
});

test('un nœud sans worker n’a pas d’accélérateur applicable', () => {
  assert.deepEqual(nodeAcceleratorBadges(node({ roles: ['server'] })), ['Non applicable']);
});

test('aucune capacité déclarée : pas de CPU inventé', () => {
  assert.deepEqual(nodeAcceleratorBadges(node()), ['Non déclaré']);
});

test('l’accélérateur vérifié le plus prioritaire passe en premier, les détectés sont signalés', () => {
  const badges = nodeAcceleratorBadges(
    node({
      capabilities: [
        ffmpeg(
          [
            ['h264_vaapi', 'usable'],
            ['h264_qsv', 'usable'],
            ['hevc_nvenc', 'detected'],
          ],
          ['qsv', 'vaapi'],
        ),
      ],
    }),
  );
  assert.deepEqual(badges, ['Quick Sync', 'VAAPI', 'NVENC détecté']);
});

test('repli CPU quand aucun encodeur matériel n’est vérifié', () => {
  const badges = nodeAcceleratorBadges(
    node({
      capabilities: [
        ffmpeg([
          ['h264_qsv', 'detected'],
          ['libx264', 'usable'],
        ]),
      ],
    }),
  );
  assert.deepEqual(badges, ['CPU', 'Quick Sync détecté']);
});

test('matériel : inventaire absent puis complet', () => {
  assert.equal(nodeHardwareLabel(node()), 'Non renseigné');
  assert.equal(
    nodeHardwareLabel(
      node({
        inventory: {
          operatingSystem: 'Linux',
          architecture: 'x64',
          processorCount: 6,
          memoryMb: 32768,
          processorModel: 'Intel Core i5-9500T',
          gpus: [{ name: 'Intel UHD Graphics 630' }],
        },
      }),
    ),
    'Linux x64 · Intel Core i5-9500T · 6 cœurs · 32 Go de RAM · GPU : Intel UHD Graphics 630',
  );
});

const { nodeMemberSections, tidyProcessorName } = await loadModel('node-hardware');
const inventory = {
  operatingSystem: 'Ubuntu 24.04.5 LTS',
  architecture: 'X64',
  processorCount: 8,
  memoryMb: 24000,
  processorModel: 'Intel(R) Xeon(R) CPU E5620 @ 2.40GHz',
  gpus: [{ name: 'AMD Radeon HD 6570' }],
};

test('le nom du processeur est nettoyé', () => {
  assert.equal(
    tidyProcessorName('Intel(R) Xeon(R) CPU E5620 @ 2.40GHz'),
    'Intel Xeon E5620 @ 2.40GHz',
  );
});

test('carte : CPU principal, aucun accélérateur non vérifié replié', () => {
  const [accelerator, hardware, processing] = nodeMemberSections(
    node({
      status: 'online',
      maxConcurrentTasks: 1,
      inventory,
      capabilities: [
        ffmpeg([
          ['libx264', 'usable'],
          ['h264_qsv', 'detected'],
          ['h264_vaapi', 'detected'],
        ]),
      ],
    }),
    { activeTasks: 0, libraryAccess: 'À configurer' },
  );
  assert.equal(accelerator.primary.label, 'CPU');
  assert.equal(accelerator.folded, undefined);
  assert.deepEqual(
    hardware.facts.map((fact) => fact.label),
    ['OS', 'CPU', 'RAM', 'GPU'],
  );
  assert.equal(hardware.facts[1].value, 'Intel Xeon E5620 @ 2.40GHz · 8 cœurs');
  assert.equal(hardware.facts[3].value, 'AMD Radeon HD 6570');
  assert.equal(processing.meter.valueLabel, '0 / 1 tâche(s)');
});

test('carte : un accélérateur vérifié devient principal avec son ton', () => {
  const [accelerator] = nodeMemberSections(
    node({
      status: 'online',
      maxConcurrentTasks: 1,
      capabilities: [
        ffmpeg(
          [
            ['h264_qsv', 'usable'],
            ['h264_vaapi', 'usable'],
          ],
          ['qsv', 'vaapi'],
        ),
      ],
    }),
    { activeTasks: 0, libraryAccess: 'x' },
  );
  assert.deepEqual([accelerator.primary.label, accelerator.primary.tone], ['Quick Sync', 'blue']);
  assert.equal(accelerator.chips[0].label, 'VAAPI');
  assert.equal(accelerator.folded, undefined);
});

test('carte : charge inconnue ou nœud hors ligne n’affiche pas de jauge', () => {
  const offline = nodeMemberSections(node({ status: 'offline', maxConcurrentTasks: 2 }), {
    activeTasks: 1,
    libraryAccess: 'x',
  })[2];
  assert.equal(offline.meter, undefined);
  assert.match(offline.hint, /Hors ligne/);
  const unknown = nodeMemberSections(node({ status: 'online', maxConcurrentTasks: 2 }), {
    activeTasks: null,
    libraryAccess: 'x',
  })[2];
  assert.equal(unknown.meter, undefined);
  assert.match(unknown.hint, /indisponible/);
});

test('carte : sans matériel déclaré, le dit explicitement', () => {
  const hardware = nodeMemberSections(node(), { activeTasks: null, libraryAccess: 'x' })[1];
  assert.match(hardware.hint, /Non renseigné/);
});
