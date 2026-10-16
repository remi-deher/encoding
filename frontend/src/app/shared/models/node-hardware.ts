import { joinBadges } from './media-badges';
import { NodeResponse } from './cluster';
import type {
  ResourceCardChip,
  ResourceCardSection,
  ResourceCardTone,
} from '../ui/resource-card.component';

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

const ACCELERATION_TONES: Readonly<Record<string, ResourceCardTone>> = {
  nvenc: 'lime',
  qsv: 'blue',
  vaapi: 'purple',
};

/** « Intel(R) Xeon(R) CPU E5620 @ 2.40GHz » devient « Intel Xeon E5620 @ 2.40GHz ». */
export function tidyProcessorName(name: string): string {
  return name
    .replace(/\((R|TM)\)/gi, '')
    .replace(/\bCPU\b\s*/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

function acceleratorSection(node: NodeResponse): ResourceCardSection {
  const label = 'Accélérateur graphique';
  if (!node.roles.includes('worker'))
    return { label, hint: 'Non applicable : la fonction worker n’est pas activée.' };
  if (!node.capabilities.length)
    return { label, hint: 'Non déclaré : cet agent n’a pas encore remonté ses capacités.' };
  const [primary, ...others] = nodeAcceleratorBadges(node).filter(
    (item) => !item.endsWith(' détecté'),
  );
  const pending = nodeAcceleratorBadges(node).filter((item) => item.endsWith(' détecté'));
  const toneOf = (item: string): ResourceCardTone =>
    ACCELERATION_TONES[
      Object.keys(ACCELERATION_LABELS).find((key) => ACCELERATION_LABELS[key] === item) ?? ''
    ] ?? 'neutral';
  const chip = (item: string): ResourceCardChip => ({ label: item, tone: toneOf(item) });
  return {
    label,
    primary: chip(primary),
    chips: others.map(chip),
    hint:
      primary === ACCELERATION_LABELS['cpu']
        ? 'Aucun accélérateur matériel vérifié : encodage sur CPU.'
        : 'Vérifié par un encodage de test.',
    folded: pending.length
      ? {
          summary: `+${pending.length} non vérifié(s)`,
          items: pending,
          hint: 'Détectés dans FFmpeg mais non vérifiés : jamais utilisés en mode automatique.',
        }
      : undefined,
  };
}

function hardwareSection(node: NodeResponse): ResourceCardSection {
  const inventory = node.inventory;
  if (!inventory)
    return {
      label: 'Matériel déclaré',
      hint: 'Non renseigné : disponible après le prochain signal du nœud.',
    };
  const gpus = inventory.gpus;
  return {
    label: 'Matériel déclaré',
    facts: [
      { label: 'OS', value: `${inventory.operatingSystem} · ${inventory.architecture}` },
      {
        label: 'CPU',
        value: `${tidyProcessorName(inventory.processorModel?.trim() || 'Processeur')} · ${inventory.processorCount} cœurs`,
      },
      { label: 'RAM', value: formatMemory(inventory.memoryMb) },
      {
        label: 'GPU',
        value: gpus?.length
          ? gpus.map((gpu) => gpu.name).join(', ')
          : gpus
            ? 'Aucun détecté'
            : 'Non déclaré',
      },
    ],
  };
}

export interface NodeProcessingInput {
  /** Tâches actives observées ; `null` tant que la source n'a pas répondu. */
  readonly activeTasks: number | null;
  readonly libraryAccess: string;
}

function processingSection(node: NodeResponse, input: NodeProcessingInput): ResourceCardSection {
  const label = 'Traitements';
  const hint = `Bibliothèques : ${input.libraryAccess}`;
  if (!node.roles.includes('worker'))
    return { label, hint: 'Non applicable : la fonction worker n’est pas activée.' };
  const max = node.maxConcurrentTasks;
  if (input.activeTasks === null || node.status === 'offline')
    return {
      label,
      facts: [{ label: 'Max.', value: `${max} tâche(s)` }],
      hint: `${node.status === 'offline' ? 'Hors ligne : charge inconnue.' : 'Charge indisponible.'} ${hint}`,
    };
  return {
    label,
    meter: {
      value: input.activeTasks,
      max,
      label: `${input.activeTasks} places de traitement occupées sur ${max}`,
      valueLabel: `${input.activeTasks} / ${max} tâche(s)`,
    },
    hint,
  };
}

/** Blocs de la carte d'un membre du cluster : accélérateur, matériel, traitements. */
export function nodeMemberSections(
  node: NodeResponse,
  input: NodeProcessingInput,
): ResourceCardSection[] {
  return [acceleratorSection(node), hardwareSection(node), processingSection(node, input)];
}
