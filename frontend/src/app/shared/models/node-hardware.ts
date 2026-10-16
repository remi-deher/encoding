import { joinBadges } from './media-badges';
import { NodeResponse } from './cluster';

export function formatMemory(memoryMb: number): string {
  return memoryMb >= 1024 ? `${Math.round((memoryMb / 1024) * 10) / 10} Go` : `${memoryMb} Mo`;
}

/** Ordre de préférence de l'affichage : celui des priorités par défaut des profils d'encodage. */
const ACCELERATION_ORDER = ['nvenc', 'qsv', 'vaapi'] as const;
const ACCELERATION_LABELS: Readonly<Record<string, string>> = {
  nvenc: 'NVENC',
  qsv: 'Quick Sync',
  vaapi: 'VAAPI',
  cpu: 'CPU',
};

function accelerationOf(encoderId: string): string | null {
  const suffix = encoderId.split('_').pop() ?? '';
  return ACCELERATION_ORDER.find((item) => item === suffix) ?? null;
}

/**
 * Accélérateurs d'un nœud : le premier encodeur vérifié par mire, puis les autres vérifiés,
 * puis ceux seulement détectés (jamais utilisés en mode automatique). CPU quand rien n'est vérifié.
 */
export function nodeAcceleratorBadges(node: NodeResponse): string[] {
  if (!node.roles.includes('worker')) return ['Non applicable'];
  if (!node.capabilities.length) return ['Non déclaré'];
  const usable = new Set<string>();
  const detected = new Set<string>();
  for (const tool of node.capabilities) {
    for (const check of tool.encoderChecks ?? []) {
      const acceleration = accelerationOf(check.encoderId);
      if (acceleration) (check.status === 'usable' ? usable : detected).add(acceleration);
    }
    for (const acceleration of tool.accelerationTypes) {
      if (ACCELERATION_LABELS[acceleration] && acceleration !== 'cpu') usable.add(acceleration);
    }
  }
  const verified = ACCELERATION_ORDER.filter((item) => usable.has(item));
  const pending = ACCELERATION_ORDER.filter((item) => !usable.has(item) && detected.has(item));
  return [
    ...(verified.length ? verified.map((item) => ACCELERATION_LABELS[item]) : ['CPU']),
    ...pending.map((item) => `${ACCELERATION_LABELS[item]} détecté`),
  ];
}

/** Matériel déclaré : système, processeur, mémoire installée, puis processeur graphique. */
export function nodeHardwareLabel(node: NodeResponse): string {
  const inventory = node.inventory;
  if (!inventory) return 'Non renseigné';
  const gpus = (inventory.gpus ?? []).map((gpu) => gpu.name);
  return [
    `${inventory.operatingSystem} ${inventory.architecture}`,
    `${inventory.processorModel?.trim() || 'Processeur'} · ${inventory.processorCount} cœurs`,
    `${formatMemory(inventory.memoryMb)} de RAM`,
    ...(gpus.length ? [`GPU : ${gpus.join(', ')}`] : []),
  ].join(' · ');
}

export const nodeAcceleratorValue = (node: NodeResponse): string =>
  joinBadges(nodeAcceleratorBadges(node));
