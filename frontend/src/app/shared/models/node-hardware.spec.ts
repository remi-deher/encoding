import { NodeResponse } from './cluster';
import { nodeAcceleratorBadges, nodeHardwareLabel } from './node-hardware';

function node(extra: Partial<NodeResponse> = {}): NodeResponse {
  return {
    id: 'a',
    name: 'a',
    roles: ['worker'],
    maxConcurrentTasks: 1,
    capabilities: [],
    status: 'online',
    lastSeenAt: '2026-10-16T10:00:00Z',
    ...extra,
  };
}
const ffmpeg = (checks: [string, 'usable' | 'detected'][], acceleration: string[] = []) => ({
  toolId: 'ffmpeg',
  version: '7',
  mediaTypes: ['video'],
  accelerationTypes: acceleration,
  encoderChecks: checks.map(([encoderId, status]) => ({ encoderId, status })),
});

describe('nodeAcceleratorBadges', () => {
  it('is not applicable without the worker role', () => {
    expect(nodeAcceleratorBadges(node({ roles: ['server'] }))).toEqual(['Non applicable']);
  });

  it('does not invent an accelerator when nothing is declared', () => {
    expect(nodeAcceleratorBadges(node())).toEqual(['Non déclaré']);
  });

  it('puts the preferred verified accelerator first and marks detected-only ones', () => {
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
    expect(badges).toEqual(['Quick Sync', 'VAAPI', 'NVENC détecté']);
  });

  it('falls back to CPU when no hardware encoder is verified', () => {
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
    expect(badges).toEqual(['CPU', 'Quick Sync détecté']);
  });
});

describe('nodeHardwareLabel', () => {
  it('reports missing inventory explicitly', () => {
    expect(nodeHardwareLabel(node())).toBe('Non renseigné');
  });

  it('combines OS, CPU, RAM and GPU', () => {
    const label = nodeHardwareLabel(
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
    );
    expect(label).toBe(
      'Linux x64 · Intel Core i5-9500T · 6 cœurs · 32 Go de RAM · GPU : Intel UHD Graphics 630',
    );
  });
});
