import { NodeGroupCriteria, NodeResponse } from './cluster';

export const LABEL_KEY_REGEX = /^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$/;

/**
 * Valide si une clé d'étiquette est conforme aux règles de nommage du cluster.
 */
export function validateLabelKey(key: string): boolean {
  return LABEL_KEY_REGEX.test(key.trim());
}

/**
 * Analyse une saisie textuelle d'étiquettes (une par ligne ou séparées par des virgules).
 */
export function parseLabels(source: string): Record<string, string | null> {
  const labels: Record<string, string | null> = {};
  for (const line of source
    .split(/[\n,]+/)
    .map((x) => x.trim())
    .filter(Boolean)) {
    const i = line.indexOf('=');
    const key = (i < 0 ? line : line.slice(0, i)).trim();
    if (!validateLabelKey(key)) {
      throw new Error(`Clé d'étiquette invalide : "${key}"`);
    }
    const val = i < 0 ? null : line.slice(i + 1).trim();
    labels[key] = val && val.length > 0 ? val : null;
  }
  return labels;
}

/**
 * Évalue si un nœud correspond aux critères d'un groupe automatique.
 * Miroir exact de l'algorithme backend `NodeGroupRepository.Matches`.
 */
export function matchesGroupCriteria(criteria: NodeGroupCriteria, node: NodeResponse): boolean {
  const inv = node.inventory;
  const rolesMatch =
    !criteria.roles ||
    criteria.roles.length === 0 ||
    criteria.roles.some((r) => node.roles.includes(r));
  if (!rolesMatch) return false;

  if (
    criteria.operatingSystem &&
    inv?.operatingSystem?.toLowerCase() !== criteria.operatingSystem.toLowerCase()
  ) {
    return false;
  }

  if (
    criteria.architecture &&
    inv?.architecture?.toLowerCase() !== criteria.architecture.toLowerCase()
  ) {
    return false;
  }

  if (criteria.minimumMemoryMb != null) {
    if (!inv?.memoryMb || inv.memoryMb < criteria.minimumMemoryMb) {
      return false;
    }
  }

  if (criteria.minimumProcessorCount != null) {
    if (!inv?.processorCount || inv.processorCount < criteria.minimumProcessorCount) {
      return false;
    }
  }

  if (criteria.minimumConcurrentTasks != null) {
    if (node.maxConcurrentTasks < criteria.minimumConcurrentTasks) {
      return false;
    }
  }

  if (criteria.requiredToolIds && criteria.requiredToolIds.length > 0) {
    const nodeToolIds = new Set(
      node.capabilities.map((c) => c.toolId.toLowerCase()),
    );
    for (const tool of criteria.requiredToolIds) {
      if (!nodeToolIds.has(tool.toLowerCase())) {
        return false;
      }
    }
  }

  return true;
}

/**
 * Filtre les nœuds qui correspondent aux critères donnés.
 */
export function filterNodesByCriteria(
  criteria: NodeGroupCriteria,
  nodes: readonly NodeResponse[],
): readonly NodeResponse[] {
  return nodes.filter((node) => matchesGroupCriteria(criteria, node));
}

/**
 * Formate les critères automatiques sous forme de puces lisibles.
 */
export function formatGroupCriteriaBadges(criteria: NodeGroupCriteria | null | undefined): string[] {
  if (!criteria) return [];
  const badges: string[] = [];

  if (criteria.roles && criteria.roles.length > 0) {
    badges.push(
      criteria.roles
        .map((r) => (r === 'worker' ? 'Workers' : r === 'server' ? 'Serveurs' : r))
        .join(', '),
    );
  }

  if (criteria.operatingSystem) {
    badges.push(`OS: ${criteria.operatingSystem}`);
  }

  if (criteria.architecture) {
    badges.push(`Arch: ${criteria.architecture}`);
  }

  if (criteria.minimumMemoryMb != null && criteria.minimumMemoryMb > 0) {
    const gb = Math.round(criteria.minimumMemoryMb / 1024);
    badges.push(gb >= 1 && criteria.minimumMemoryMb % 1024 === 0 ? `RAM ≥ ${gb} Go` : `RAM ≥ ${criteria.minimumMemoryMb} Mo`);
  }

  if (criteria.minimumProcessorCount != null && criteria.minimumProcessorCount > 0) {
    badges.push(`CPU ≥ ${criteria.minimumProcessorCount}`);
  }

  if (criteria.minimumConcurrentTasks != null && criteria.minimumConcurrentTasks > 0) {
    badges.push(`Tâches ≥ ${criteria.minimumConcurrentTasks}`);
  }

  if (criteria.requiredToolIds && criteria.requiredToolIds.length > 0) {
    badges.push(`Outils: ${criteria.requiredToolIds.join(', ')}`);
  }

  return badges;
}

/**
 * Extrait les systèmes d'exploitation distincts observés dans le cluster.
 */
export function extractAvailableOperatingSystems(nodes: readonly NodeResponse[]): string[] {
  const set = new Set<string>();
  for (const n of nodes) {
    if (n.inventory?.operatingSystem) {
      set.add(n.inventory.operatingSystem);
    }
  }
  return Array.from(set).sort();
}

/**
 * Extrait les architectures distinctes observées dans le cluster.
 */
export function extractAvailableArchitectures(nodes: readonly NodeResponse[]): string[] {
  const set = new Set<string>();
  for (const n of nodes) {
    if (n.inventory?.architecture) {
      set.add(n.inventory.architecture);
    }
  }
  return Array.from(set).sort();
}

/**
 * Extrait les identifiants d'outils distincts déclarés dans le cluster.
 */
export function extractAvailableTools(nodes: readonly NodeResponse[]): string[] {
  const set = new Set<string>();
  for (const n of nodes) {
    for (const cap of n.capabilities ?? []) {
      if (cap.toolId) {
        set.add(cap.toolId);
      }
    }
  }
  return Array.from(set).sort();
}
