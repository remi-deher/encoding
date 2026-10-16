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
        ffmpeg([['h264_vaapi', 'usable'], ['h264_qsv', 'usable'], ['hevc_nvenc', 'detected']], ['qsv', 'vaapi']),
      ],
    }),
  );
  assert.deepEqual(badges, ['Quick Sync', 'VAAPI', 'NVENC détecté']);
});

test('repli CPU quand aucun encodeur matériel n’est vérifié', () => {
  const badges = nodeAcceleratorBadges(
    node({ capabilities: [ffmpeg([['h264_qsv', 'detected'], ['libx264', 'usable']])] }),
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
