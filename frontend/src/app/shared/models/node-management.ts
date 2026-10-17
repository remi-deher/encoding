import { DetailCard } from '../ui/detail-card-grid.component';
import { formatMemory } from './node-hardware';
import {
  ResourceSheetBadge,
  ResourceSheetMetric,
  ResourceSheetNotice,
  ResourceSheetSection,
} from '../ui/resource-sheet.component';
import { RecordFormData } from '../ui/record-form.component';
import { GuidedStep } from '../ui/guided-setup.component';
import { DiscoveredCluster, LocalNodePairing, LocalNodeStatus } from './local-node';
import { NodeModeStatus } from './node-mode';
import { NodeResponse } from './cluster';

export const NODE_MANAGEMENT_TABS = [
  { value: 'node', label: 'Ce nœud' },
  { value: 'cluster', label: 'Cluster' },
  { value: 'groups', label: 'Groupes de nœuds' },
];
export const CREATE_CLUSTER_FORM: RecordFormData = {
  title: 'Créer un cluster',
  description: 'Ce nœud devient le premier membre du nouveau cluster.',
  submitLabel: 'Créer le cluster',
  reviewTitle: 'Créer ce cluster ?',
  fields: [
    {
      id: 'name',
      label: 'Nom du cluster',
      type: 'text',
      required: true,
      maxLength: 128,
      hint: 'Un nom reconnaissable pour toutes vos installations.',
    },
  ],
};

function informationCard(
  id: string,
  title: string,
  subtitle: string,
  items: readonly { label: string; description: string }[],
): DetailCard {
  return { id, title, subtitle, icon: 'server', tags: [], sections: [{ title: 'Détails', items }] };
}

/** Formate un délai écoulé ; une date invalide reste « indisponible », jamais zéro. */
export function formatLastSignal(
  value: string | null | undefined,
  now = Date.now(),
): string | null {
  const time = value ? new Date(value).getTime() : NaN;
  if (Number.isNaN(time)) return null;
  const seconds = Math.max(0, Math.floor((now - time) / 1000));
  if (seconds < 5) return 'À l’instant';
  if (seconds < 60) return `Il y a ${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `Il y a ${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `Il y a ${hours} h`;
  return new Intl.DateTimeFormat('fr-FR', { dateStyle: 'medium', timeStyle: 'short' }).format(
    new Date(time),
  );
}

export interface LocalNodeSheetInput {
  readonly status: LocalNodeStatus | null;
  readonly node: NodeResponse | null;
  readonly mode: NodeModeStatus | null;
  readonly standalone: boolean;
  readonly clusterName: string | null;
  readonly agentUnavailable: boolean;
  /** Tâches actives observées ; `null` tant que la source n'a pas répondu. */
  readonly activeTasks: number | null;
  readonly now?: number;
}

/**
 * Fiche du nœud local, fondée uniquement sur les états observés : une fonction inconnue n'est jamais présentée
 * comme inactive, et une valeur périmée est identifiée comme dernière valeur connue.
 */
export function localNodeSheet(input: LocalNodeSheetInput): {
  readonly title: string;
  readonly subtitle: string;
  readonly notices: readonly ResourceSheetNotice[];
  readonly sections: readonly ResourceSheetSection[];
} {
  const { status, node, standalone, agentUnavailable } = input;
  const roles =
    input.mode ??
    (status
      ? { server: status.roles.includes('server'), worker: status.roles.includes('worker') }
      : null);
  const desired = node?.desiredRoles ?? null;
  const roleBadges = (role: 'server' | 'worker'): readonly ResourceSheetBadge[] => {
    const enabled = roles ? roles[role] : undefined;
    const badges: ResourceSheetBadge[] =
      enabled === undefined
        ? [{ label: 'État indisponible', tone: 'warning' }]
        : agentUnavailable
          ? [{ label: `Dernier état connu : ${enabled ? 'actif' : 'inactif'}`, tone: 'warning' }]
          : [{ label: enabled ? 'Actif' : 'Inactif', tone: enabled ? 'success' : 'neutral' }];
    if (desired && desired.includes(role) !== (enabled ?? false))
      badges.push({
        label: `Demandé : ${desired.includes(role) ? 'actif' : 'inactif'}`,
        tone: 'accent',
      });
    return badges;
  };
  const workerOff = roles?.worker === false;
  const max = status?.maxConcurrentTasks ?? node?.maxConcurrentTasks ?? null;
  const processing: ResourceSheetMetric = workerOff
    ? {
        label: 'Traitements actifs',
        value: 'Non applicable',
        unavailable: true,
        detail: 'La fonction worker est désactivée.',
      }
    : input.activeTasks === null || max === null
      ? {
          label: 'Traitements actifs',
          value: 'Indisponible',
          unavailable: true,
          detail: max === null ? undefined : `${max} traitement(s) simultané(s) au maximum`,
        }
      : {
          label: 'Traitements actifs',
          value: `${input.activeTasks} / ${max}`,
          stale: agentUnavailable,
          detail: agentUnavailable
            ? 'Dernière valeur connue, pas une occupation actuelle'
            : undefined,
          meter: {
            value: input.activeTasks,
            max,
            label: `${input.activeTasks} places de traitement occupées sur ${max}`,
            stale: agentUnavailable,
          },
        };
  const signal = formatLastSignal(node?.lastSeenAt, input.now);
  const inventory = node?.inventory ?? status?.inventory ?? null;
  const gpus = inventory?.gpus ?? [];
  const tools = status?.capabilities ?? [];
  const notices: ResourceSheetNotice[] = [];
  if (agentUnavailable)
    notices.push({
      tone: 'danger',
      title: 'L’agent de ce nœud ne répond pas',
      description:
        'Les informations ci-dessous sont les dernières connues ; elles peuvent ne plus être exactes.',
    });
  if (status?.switchFailure)
    notices.push({
      tone: 'danger',
      title: 'Rattachement annulé',
      description: status.switchFailure,
    });
  if (
    desired &&
    roles &&
    (desired.includes('server') !== roles.server || desired.includes('worker') !== roles.worker)
  )
    notices.push({
      tone: 'accent',
      title: 'Changement de fonctions en attente',
      description: 'Les fonctions appliquées ne changent qu’après confirmation du nœud.',
    });
  const sections: ResourceSheetSection[] = [
    {
      id: 'state',
      title: 'État et activité',
      caption: agentUnavailable ? 'Agent injoignable' : 'Agent joignable',
      items: [
        { label: 'Serveur', badges: roleBadges('server') },
        { label: 'Worker', badges: roleBadges('worker') },
      ],
      metrics: [
        processing,
        signal
          ? { label: 'Dernier signal', value: signal, stale: agentUnavailable }
          : {
              label: 'Dernier signal',
              value: 'Indisponible',
              unavailable: true,
              detail: node ? undefined : 'Le suivi du nœud est assuré par le serveur du cluster.',
            },
      ],
    },
    {
      id: 'hardware',
      title: 'Matériel déclaré',
      metrics: inventory
        ? [
            {
              label: 'Processeur',
              value: inventory.processorModel?.trim() || `${inventory.processorCount} cœurs`,
              detail: `${inventory.processorCount} cœurs logiques · ${formatMemory(inventory.memoryMb)} de mémoire installée · ${inventory.operatingSystem} · ${inventory.architecture}`,
            },
            gpus.length
              ? {
                  label: gpus.length > 1 ? 'Processeurs graphiques' : 'Processeur graphique',
                  value: gpus.map((gpu) => gpu.name).join(' · '),
                  detail:
                    gpus
                      .map((gpu) =>
                        [gpu.device, gpu.driver ? `pilote ${gpu.driver}` : null]
                          .filter(Boolean)
                          .join(' · '),
                      )
                      .filter(Boolean)
                      .join(' ; ') || undefined,
                }
              : {
                  label: 'Processeur graphique',
                  value: inventory.gpus ? 'Aucun détecté' : 'Non déclaré',
                  unavailable: true,
                  detail: inventory.gpus ? undefined : 'Cet agent ne remonte pas encore ses GPU.',
                },
          ]
        : [
            {
              label: 'Matériel',
              value: 'Non renseigné',
              unavailable: true,
              detail: 'Disponible après le prochain signal du nœud.',
            },
          ],
    },
    {
      id: 'capabilities',
      title: 'Capacités de traitement',
      notice: workerOff
        ? 'La fonction worker est désactivée : aucun outil n’est déclaré.'
        : tools.length
          ? undefined
          : 'Aucun outil déclaré.',
      items: workerOff
        ? []
        : tools.map((tool) => {
            const usable =
              tool.encoderChecks?.filter((check) => check.status === 'usable').length ?? 0;
            const detected =
              tool.encoderChecks?.filter((check) => check.status === 'detected').length ?? 0;
            const badges: ResourceSheetBadge[] = [];
            if (usable) badges.push({ label: `${usable} encodeur(s) vérifié(s)`, tone: 'success' });
            if (detected)
              badges.push({ label: `${detected} détecté(s), non vérifié(s)`, tone: 'neutral' });
            for (const acceleration of tool.accelerationTypes)
              badges.push({ label: acceleration.toUpperCase(), tone: 'accent' });
            return { label: `${tool.toolId} ${tool.version}`, badges };
          }),
      action: workerOff || !node ? undefined : { id: 'capabilities', label: 'Fiche des capacités' },
    },
    {
      id: 'identity',
      title: 'Identité et appartenance',
      fields: [
        { label: 'Nom technique', value: status?.name ?? 'Indisponible' },
        { label: 'Identifiant du nœud', value: status?.nodeId ?? 'Indisponible', mono: true },
        {
          label: 'Cluster',
          value: standalone
            ? 'Aucun (nœud autonome)'
            : (input.clusterName ?? status?.clusterName ?? 'Cluster'),
        },
      ],
    },
  ];
  return {
    title: node?.displayName?.trim() || status?.name || 'Ce nœud',
    subtitle: standalone
      ? 'Nœud autonome'
      : `Membre de ${input.clusterName ?? status?.clusterName ?? 'son cluster'}`,
    notices,
    sections,
  };
}

export const AUTONOMOUS_CARDS: readonly DetailCard[] = [
  {
    ...informationCard(
      'autonomous-node',
      'Votre installation reste utilisable',
      'Aucun rattachement obligatoire',
      [
        {
          label: 'Un nœud autonome',
          description:
            'Gérez les fonctions et les capacités de cette installation depuis l’onglet Ce nœud.',
        },
        {
          label: 'Une configuration partagée quand vous le souhaitez',
          description:
            'Un cluster permet de gérer plusieurs nœuds et de leur répartir les traitements.',
        },
      ],
    ),
    icon: 'system',
  },
];

export function clusterAddressForm(address: string): RecordFormData {
  return {
    title: 'Adresse du cluster',
    description: '',
    submitLabel: 'Vérifier la connexion',
    review: false,
    fields: [
      {
        id: 'address',
        label: 'Adresse du cluster',
        type: 'text',
        required: true,
        hint: 'Exemple : http://192.168.1.51:8090',
      },
    ],
    initialValues: { address: address },
  };
}

interface JoinPresentation {
  readonly addressForm: RecordFormData;
  readonly target: DiscoveredCluster | null;
  readonly status: LocalNodeStatus | null;
  readonly pairing: LocalNodePairing | null;
  readonly clusters: readonly DiscoveredCluster[];
  readonly localServerActive: boolean;
  readonly canChoose: boolean;
}

export function clusterJoinSteps(data: JoinPresentation): readonly GuidedStep[] {
  const name = data.target?.name ?? data.status?.clusterName ?? 'le cluster d’accueil';
  const expires =
    data.pairing?.expiresAt ?? data.status?.pairingExpiresAt ?? data.status?.switching?.deadline;
  const expiry = expires
    ? new Intl.DateTimeFormat('fr-FR', { dateStyle: 'short', timeStyle: 'short' }).format(
        new Date(expires),
      )
    : null;
  const code = data.status?.verificationCode;
  return [
    {
      id: 'address',
      label: 'Choisir le cluster',
      description:
        'Choisissez un cluster détecté ou saisissez l’adresse d’un nœud serveur du cluster à rejoindre.',
      ...(data.clusters.length
        ? {
            choices: {
              columns: [
                { key: 'name', label: 'Cluster' },
                { key: 'address', label: 'Adresse' },
                { key: 'state', label: 'Disponibilité' },
              ],
              rows: data.clusters.map((cluster) => ({
                id: cluster.id,
                name: cluster.name,
                address: cluster.apiBaseUrl,
                state: cluster.reachable === false ? 'Ne répond plus' : 'À vérifier',
                manual: cluster.source === 'manual',
              })),
              actions: [
                { id: 'forget', label: 'Oublier', visibleWhen: (row) => row['manual'] === true },
              ],
            },
          }
        : {}),
      form: data.addressForm,
      note: data.canChoose
        ? 'La vérification contacte ce cluster. Aucune demande de rattachement n’est encore envoyée.'
        : 'La fonction worker est indisponible. Activez-la depuis Ce nœud ou rétablissez sa connexion avant de continuer.',
    },
    {
      id: 'identity',
      label: 'Vérifier et confirmer',
      description:
        'Le cluster répond. Comparez son nom et son identifiant avec ceux indiqués sur le serveur d’accueil.',
      cards: data.target
        ? [
            informationCard('join-target', data.target.name, 'Cluster joignable', [
              { label: 'Adresse', description: data.target.apiBaseUrl },
              { label: 'Identifiant du cluster', description: data.target.id },
            ]),
          ]
        : [],
      note: data.localServerActive
        ? 'Après approbation, le serveur local reste actif. Les configurations et états convergent avec le cluster par le maillage SQLite. Les fichiers médias et résultats ne sont pas répliqués et doivent rester accessibles selon les accès configurés. La configuration locale n’est pas importée automatiquement. Un changement en cours peut être annulé avant l’approbation.'
        : 'Le cluster d’accueil doit approuver votre demande. Les accès aux bibliothèques et les capacités de ce nœud restent nécessaires pour recevoir des traitements.',
      actions: [
        { id: 'back', label: 'Modifier l’adresse' },
        { id: 'request', label: 'Envoyer la demande', primary: true },
      ],
    },
    {
      id: 'waiting',
      label: 'Approbation',
      description: `Demande envoyée à ${name}. Un administrateur doit la vérifier depuis Nœuds → Cluster → Demandes de rattachement.`,
      cards: [
        informationCard(
          'join-pending',
          'En attente d’approbation',
          'Le rattachement précédent est conservé pendant la vérification',
          [
            { label: 'Nœud demandeur', description: data.status?.name ?? 'Ce nœud' },
            { label: 'Code à comparer', description: code ?? 'Lecture du code de vérification…' },
            ...(expiry ? [{ label: 'Demande valable jusqu’au', description: expiry }] : []),
            ...(data.pairing?.pairingKey || data.status?.pairingKey
              ? [
                  {
                    label: 'Clé de jumelage · méthode secondaire',
                    description: data.pairing?.pairingKey ?? data.status!.pairingKey!,
                  },
                ]
              : []),
          ],
        ),
      ],
      note: 'L’approbation peut arrêter cette interface locale. En cas de perte de connexion, consultez le cluster d’accueil pour connaître le résultat ; une déconnexion ne prouve pas une réussite.',
      actions: [
        { id: 'later', label: 'Fermer et suivre plus tard' },
        { id: 'cancel', label: 'Annuler la demande' },
      ],
    },
    {
      id: 'done',
      label: 'Rattachement terminé',
      description: `Le nœud a confirmé son rattachement à ${name}.`,
      cards: [
        informationCard('join-complete', `Membre de ${name}`, 'Rattachement confirmé par le nœud', [
          {
            label: 'Traitement des bibliothèques',
            description:
              'Le cluster attribue les tâches selon les capacités et les accès aux médias déclarés pour ce nœud.',
          },
        ]),
      ],
      actions: [{ id: 'finish', label: 'Voir le cluster', primary: true }],
    },
  ];
}
