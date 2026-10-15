import { DetailCard } from '../ui/detail-card-grid.component';
import { RecordFormData } from '../ui/record-form.component';
import { GuidedStep } from '../ui/guided-setup.component';
import { DiscoveredCluster, LocalNodePairing, LocalNodeStatus } from './local-node';
import { NodeModeStatus } from './node-mode';
import { NodeResponse } from './cluster';

export const NODE_MANAGEMENT_TABS = [
  { value: 'node', label: 'Ce nœud' },
  { value: 'cluster', label: 'Cluster' },
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

/** Présentation fondée uniquement sur les états observés, sans supposer qu'un nœud est en ligne. */
export function localNodeCards(
  status: LocalNodeStatus | null,
  node: NodeResponse | null,
  mode: NodeModeStatus | null,
  standalone: boolean,
): readonly DetailCard[] {
  const roleState = (enabled: boolean | undefined) =>
    enabled === undefined ? 'État indisponible' : enabled ? 'Actif' : 'Inactif';
  const roles =
    mode ??
    (status
      ? { server: status.roles.includes('server'), worker: status.roles.includes('worker') }
      : null);
  return [
    informationCard('local-functions', 'Rôle du nœud', 'Fonctions de cette installation', [
      {
        label: `Serveur · ${roleState(roles?.server)}`,
        description: 'Interface web, configuration partagée et orchestration.',
      },
      {
        label: `Worker · ${roleState(roles?.worker)}`,
        description: 'Traitement des médias avec les outils de ce nœud.',
      },
    ]),
    informationCard('local-identity', 'Cette installation', 'Identité et appartenance', [
      {
        label: node?.displayName?.trim() || status?.name || 'Ce nœud',
        description: status?.nodeId ?? 'Identité de l’agent indisponible.',
      },
      {
        label: standalone ? 'Nœud autonome' : status?.clusterName || 'Cluster',
        description: standalone
          ? 'Aucun cluster associé.'
          : (status?.clusterId ?? 'Identité du cluster indisponible.'),
      },
    ]),
    {
      ...informationCard(
        'local-capabilities',
        'Capacités de traitement',
        'Observées sur cette installation',
        [
          {
            label: node?.inventory
              ? `${node.inventory.processorCount} cœurs · ${node.inventory.memoryMb} Mo de mémoire`
              : 'Matériel non renseigné',
            description: node?.inventory
              ? `${node.inventory.operatingSystem} · ${node.inventory.architecture}`
              : 'Consultez les capacités du nœud après son prochain signal.',
          },
          {
            label: status?.capabilities.length
              ? status.capabilities.map((tool) => `${tool.toolId} ${tool.version}`).join(' · ')
              : 'Aucun outil déclaré',
            description: status
              ? `${status.maxConcurrentTasks} traitement(s) simultané(s) au maximum.`
              : 'La fonction worker est nécessaire pour déclarer les outils de traitement.',
          },
        ],
      ),
      icon: 'tool',
    },
  ];
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
        ? 'Après approbation, ce nœud devient worker du cluster d’accueil et son serveur local s’arrête. La configuration et les bibliothèques locales ne sont pas importées automatiquement. Un changement en cours peut être annulé avant l’approbation.'
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
