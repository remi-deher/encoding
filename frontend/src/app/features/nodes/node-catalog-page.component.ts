import { firstValueFrom } from 'rxjs';
import { ClusterApi } from '../../shared/services/cluster-api';
import { EnrollmentWindow } from '../../shared/models/auth';
import {
  NodeRole,
  NodeResponse,
  LibraryResponse,
  NodeToolCapability,
  NodeActivityResponse,
  NodeProcessingSettings,
  NODE_ROLE_LABELS,
} from '../../shared/models/cluster';
import { NodeSchedule } from '../../shared/models/node-schedule';
import { WeeklyScheduleFormComponent } from '../../shared/ui/weekly-schedule-form.component';
import { MatDialog } from '@angular/material/dialog';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import { DrawerActionButtonComponent } from '../../shared/ui/drawer-action-button/drawer-action-button.component';
import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { AuthService } from '../../core/auth/auth.service';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatTabsModule } from '@angular/material/tabs';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { takeUntilDestroyed, toSignal, toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { RouterLink } from '@angular/router';
import {
  catchError,
  EMPTY,
  finalize,
  forkJoin,
  of,
  switchMap,
  timer,
  startWith,
  exhaustMap,
} from 'rxjs';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import {
  DataTableColumn,
  DataTableComponent,
  DataTableActionEvent,
  DataTableRow,
  DataTableRowAction,
  DataTableToggleEvent,
} from '../../shared/ui/data-table.component';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { DetailField, DetailFieldListComponent } from '../../shared/ui/detail-field-list.component';
import { RecordFormComponent, RecordFormData } from '../../shared/ui/record-form.component';
import { NotificationService } from '../../shared/services/notification.service';
import {
  LibraryNodeAccessFormComponent,
  LibraryNodeAccessOption,
  LibraryNodeAccessValue,
} from '../../shared/ui/library-node-access-form.component';
import {
  LibraryNodeAccessMode,
  LibraryNodeAccessResponse,
} from '../../shared/ui/library-node-access-types';

interface NodeLibraryRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly isEnabled: boolean;
  readonly mode: LibraryNodeAccessMode;
  readonly modeLabel: string;
  readonly pathLabel: string;
}

interface PendingNodeEnrollmentResponse {
  readonly id: string;
  readonly name: string;
  readonly roles: readonly NodeRole[];
  readonly maxConcurrentTasks: number;
  readonly capabilities: readonly NodeToolCapability[];
  readonly createdAt: string;
  readonly expiresAt: string;
}

interface PendingNodeEnrollmentRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly rolesLabel: string;
  readonly capabilitiesLabel: string;
  readonly expiresAtLabel: string;
}

interface NodeRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly roleFilter: string;
  readonly roleLabel: string;
  readonly capabilities: string;
  readonly libraryAccess: string;
  readonly workerLoad: string;
  readonly queueStatus: string;
  readonly state: string;
  readonly lastSignal: string;
}

const NODE_ROLE_OPTIONS: readonly { readonly value: NodeRole; readonly label: string }[] = [
  { value: 'server', label: 'Serveur' },
  { value: 'worker', label: 'Worker' },
];

@Component({
  selector: 'app-node-catalog-page',
  imports: [
    DataTableComponent,
    DetailFieldListComponent,
    ManagementCatalogTemplateComponent,
    CategoryTabsComponent,
    MatButtonModule,
    MatTabsModule,
    MatFormFieldModule,
    MatSelectModule,
    RecordFormComponent,
    LibraryNodeAccessFormComponent,
    DrawerActionButtonComponent,
    DatePipe,
    WeeklyScheduleFormComponent,
    RouterLink,
  ],
  templateUrl: './node-catalog-page.component.html',
  styleUrl: './node-catalog-page.component.scss',
})
export class NodeCatalogPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly http = inject(HttpClient);
  private readonly clusterApi = inject(ClusterApi);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });
  private readonly nodes = signal<readonly NodeResponse[]>([]);
  private readonly pendingEnrollments = signal<readonly PendingNodeEnrollmentResponse[]>([]);
  private readonly allLibraries = signal<readonly LibraryResponse[]>([]);
  private readonly nodeAccessByLibrary = signal<
    ReadonlyMap<string, readonly LibraryNodeAccessResponse[]>
  >(new Map());
  private readonly nodeAccessActivationBusy = signal<ReadonlySet<string>>(new Set());
  private readonly libraryAccessLoadState = signal<'loading' | 'ready' | 'error'>('loading');

  protected readonly isAdmin = inject(AuthService).isAdmin;
  protected readonly enrollmentWindow = signal<EnrollmentWindow>({
    isOpen: false,
    openUntil: null,
  });
  protected readonly loadState = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly selectedRole = signal('all');
  protected readonly actionOpen = signal(false);
  protected readonly actionType = signal<
    'enrollment' | 'library-access' | 'create-library' | 'processing' | 'schedule' | 'identity'
  >('enrollment');
  protected readonly nodeLibraryLoading = signal(false);
  protected readonly accessFormBusy = signal(false);
  protected readonly selectedNodeLibrary = signal<LibraryResponse | null>(null);
  protected readonly selectedAvailableLibraryId = signal('');
  protected readonly enrollmentView = signal<'requests' | 'key' | 'review'>('requests');
  protected readonly selectedEnrollment = signal<PendingNodeEnrollmentResponse | null>(null);
  protected readonly selectedNode = signal<NodeRecord | null>(null);
  protected readonly identityForm = signal<RecordFormData | null>(null);
  protected readonly createFormData: RecordFormData = {
    title: 'Ajouter un nœud worker',
    description:
      'Examinez les demandes des workers avant de les accepter. Si un worker n’apparaît pas, générez une clé depuis sa console locale et saisissez-la ici ; elle expire au bout de 10 minutes.',
    submitLabel: 'Accepter le nœud',
    reviewTitle: 'Vérifier le jumelage',
    fields: [
      {
        id: 'pairingKey',
        label: 'Clé générée sur le worker',
        type: 'text',
        required: true,
        maxLength: 16,
      },
    ],
  };
  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Nœuds');
  protected readonly catalogMessage = computed(() =>
    this.loadState() === 'error'
      ? 'Impossible de joindre l’API du cluster. Vérifiez que le backend et PostgreSQL sont démarrés.'
      : 'Chargement des nœuds du cluster…',
  );
  protected readonly detailTitle = computed(
    () => this.rawNode()?.displayName?.trim() || this.rawNode()?.name || 'Détails du nœud',
  );
  protected readonly detailDescription = computed(
    () => this.selectedNode()?.roleLabel ?? 'État observé par le cluster',
  );

  protected readonly removalBusy = signal(false);
  protected readonly rawNode = computed(
    () => this.nodes().find((node) => node.id === this.selectedNode()?.id) ?? null,
  );
  protected readonly nodeActivity = toSignal(
    toObservable(computed(() => this.selectedNode()?.id ?? null)).pipe(
      switchMap((id) =>
        id
          ? timer(0, 5000).pipe(
              exhaustMap(() =>
                this.http
                  .get<NodeActivityResponse>('/api/v1/nodes/' + id + '/activity')
                  .pipe(catchError(() => of(null))),
              ),
              startWith(null),
            )
          : of(null),
      ),
    ),
    { initialValue: null },
  );
  protected readonly summaryFields = computed<readonly DetailField[]>(() => {
    const node = this.rawNode();
    const activity = this.nodeActivity();
    if (!node) return [];
    return [
      { label: 'Identifiant du nœud', value: node.id },
      { label: 'Présence', value: node.status === 'online' ? 'En ligne' : 'Hors ligne' },
      { label: 'Rôles', value: node.roles.map((role) => NODE_ROLE_LABELS[role]).join(' · ') },
      {
        label: 'Traitements actifs / limite',
        value: node.roles.includes('worker')
          ? (activity ? activity.active : '—') +
            ' / ' +
            (this.nodeProcessingState()?.effectiveMaxConcurrentTasks ?? node.maxConcurrentTasks)
          : 'Fonctions serveur',
      },
      {
        label: 'Bibliothèques accessibles',
        value: this.nodeLibraries().filter(
          (library) => library.isEnabled && library.mode !== 'unsupported',
        ).length,
      },
    ];
  });
  protected readonly nodeProcessingState = toSignal(
    toObservable(computed(() => this.selectedNode()?.id ?? null)).pipe(
      switchMap((id) =>
        id
          ? timer(0, 5000).pipe(
              exhaustMap(() => this.clusterApi.nodeProcessing(id).pipe(catchError(() => of(null)))),
              startWith(null),
            )
          : of(null),
      ),
    ),
    { initialValue: null },
  );
  protected readonly processingFields = computed<readonly DetailField[]>(() => {
    const node = this.rawNode();
    const state = this.nodeProcessingState();
    if (!node) return [];
    const schedule = state?.schedule;
    const openShare = schedule?.enabled
      ? Math.round(((schedule.slots.split('1').length - 1) * 100) / schedule.slots.length)
      : null;
    return [
      {
        label: 'Limite globale',
        value: state
          ? state.effectiveMaxConcurrentTasks +
            (state.maxConcurrentTasks == null ? ' (annoncée par le worker)' : ' (définie)')
          : node.maxConcurrentTasks + ' (annoncée par le worker)',
      },
      {
        label: 'Calendrier',
        value: !state
          ? '—'
          : openShare == null
            ? 'Désactivé : démarrage possible à toute heure'
            : 'Actif : ' + openShare + ' % de la semaine autorisée (' + schedule!.timeZone + ')',
      },
    ];
  });
  protected readonly processingForm = signal<RecordFormData | null>(null);
  private processingRevision = 0;
  private processingSettings: NodeProcessingSettings | null = null;
  protected readonly scheduleBusy = signal(false);
  protected readonly scheduleValue = signal<NodeSchedule | null>(null);
  protected async configureNodeProcessing(): Promise<void> {
    const node = this.rawNode();
    if (!node || this.removalBusy()) return;
    try {
      const settings = await firstValueFrom(this.clusterApi.nodeProcessing(node.id));
      if (this.rawNode()?.id !== node.id) return;
      this.processingRevision = settings.revision;
      this.processingSettings = settings;
      this.processingForm.set({
        title: 'Limite globale du nœud',
        description:
          'Ce plafond concerne toutes ses bibliothèques. Les limites de bibliothèque et les limites par nœud/bibliothèque s’appliquent aussi.',
        submitLabel: 'Enregistrer',
        fields: [
          {
            id: 'maximum',
            label: 'Fichiers traités simultanément, toutes bibliothèques',
            type: 'number',
            min: 1,
            max: 1000,
            step: 1,
            hint:
              'Vide : utiliser la limite locale annoncée par le worker (' +
              node.maxConcurrentTasks +
              ').',
          },
        ],
        initialValues: {
          maximum: settings.maxConcurrentTasks == null ? '' : String(settings.maxConcurrentTasks),
        },
      });
      this.actionType.set('processing');
      this.actionOpen.set(true);
    } catch {
      this.notifications.show('Impossible de charger la limite du nœud.');
    }
  }
  protected async configureNodeSchedule(): Promise<void> {
    const node = this.rawNode();
    if (!node || this.removalBusy()) return;
    try {
      const settings = await firstValueFrom(this.clusterApi.nodeProcessing(node.id));
      if (this.rawNode()?.id !== node.id) return;
      this.processingRevision = settings.revision;
      this.processingSettings = settings;
      this.scheduleValue.set(settings.schedule);
      this.actionType.set('schedule');
      this.actionOpen.set(true);
    } catch {
      this.notifications.show('Impossible de charger le calendrier du nœud.');
    }
  }
  protected async saveNodeSchedule(schedule: NodeSchedule): Promise<void> {
    const node = this.rawNode();
    const settings = this.processingSettings;
    if (!node || !settings || this.scheduleBusy()) return;
    this.scheduleBusy.set(true);
    try {
      await firstValueFrom(
        this.clusterApi.saveNodeProcessing(
          node.id,
          settings.maxConcurrentTasks,
          this.processingRevision,
          schedule,
        ),
      );
      this.actionOpen.set(false);
      this.notifications.show('Calendrier du nœud enregistré.');
    } catch {
      this.notifications.show('Calendrier invalide ou configuration modifiée ailleurs.');
    } finally {
      this.scheduleBusy.set(false);
    }
  }
  protected async saveNodeProcessing(values: Record<string, string>): Promise<void> {
    const node = this.rawNode();
    if (!node) return;
    try {
      await firstValueFrom(
        this.clusterApi.saveNodeProcessing(
          node.id,
          values['maximum'] ? Number(values['maximum']) : null,
          this.processingRevision,
          this.processingSettings!.schedule,
        ),
      );
      this.actionOpen.set(false);
      this.notifications.show('Limite globale du nœud enregistrée.');
    } catch {
      this.notifications.show('Limite invalide ou configuration modifiée ailleurs.');
    }
  }

  private async editNodeIdentity(node: NodeRecord): Promise<void> {
    try {
      const identity = await firstValueFrom(
        this.http.get<{
          displayName: string | null;
          labels: Readonly<Record<string, string | null>>;
          effectiveLabels: Readonly<Record<string, string | null>>;
          groupIds: readonly string[];
        }>(`/api/v1/nodes/${node.id}/identity`),
      );
      this.selectedNode.set(node);
      this.identityForm.set({
        title: 'Nom et étiquettes',
        description: `Groupes actuels : ${identity.groupIds.length}. Étiquettes effectives : ${
          Object.entries(identity.effectiveLabels)
            .map(([key, value]) => (value == null ? key : `${key}=${value}`))
            .join(', ') || 'aucune'
        }.`,
        submitLabel: 'Enregistrer',
        fields: [
          { id: 'displayName', label: 'Nom d’affichage', type: 'text', maxLength: 128 },
          {
            id: 'labels',
            label: 'Étiquettes locales',
            type: 'textarea',
            hint: 'Une par ligne : gpu=nvidia ou production.',
          },
        ],
        initialValues: {
          displayName: identity.displayName ?? '',
          labels: Object.entries(identity.labels)
            .map(([key, value]) => (value == null ? key : `${key}=${value}`))
            .join('\n'),
        },
      });
      this.actionType.set('identity');
      this.actionOpen.set(true);
    } catch {
      this.notifications.show('Impossible de charger l’identité du nœud.');
    }
  }

  protected async saveNodeIdentity(values: Record<string, string>): Promise<void> {
    const node = this.selectedNode();
    if (!node) return;
    const labels: Record<string, string | null> = {};
    for (const line of values['labels']
      .split(/[\n,]+/)
      .map((value) => value.trim())
      .filter(Boolean)) {
      const equals = line.indexOf('=');
      const key = (equals < 0 ? line : line.slice(0, equals)).trim();
      if (!/^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$/.test(key)) {
        this.notifications.show('Clé d’étiquette invalide.');
        return;
      }
      labels[key] = equals < 0 ? null : line.slice(equals + 1).trim();
    }
    try {
      await firstValueFrom(
        this.http.put(`/api/v1/nodes/${node.id}/identity`, {
          displayName: values['displayName'] || null,
          labels,
        }),
      );
      this.nodes.update((nodes) =>
        nodes.map((item) =>
          item.id === node.id
            ? { ...item, displayName: values['displayName'] || null, labels }
            : item,
        ),
      );
      this.actionOpen.set(false);
      this.notifications.show('Identité du nœud enregistrée.');
    } catch {
      this.notifications.show('Les étiquettes ou le nom du nœud sont invalides.');
    }
  }
  protected readonly overviewFields = computed<readonly DetailField[]>(() => {
    const node = this.rawNode();
    const activity = this.nodeActivity();
    if (!node) return [];
    return [
      { section: 'Identité et connexion', label: 'Identifiant', value: node.id },
      ...(node.inventory
        ? [
            {
              section: 'Inventaire déclaré',
              label: 'Système',
              value: node.inventory.operatingSystem,
            },
            {
              section: 'Inventaire déclaré',
              label: 'Architecture',
              value: node.inventory.architecture,
            },
            {
              section: 'Inventaire déclaré',
              label: 'Processeurs disponibles',
              value: node.inventory.processorCount,
            },
            {
              section: 'Inventaire déclaré',
              label: 'Mémoire totale disponible',
              value: `${node.inventory.memoryMb} Mo`,
            },
          ]
        : []),
      {
        section: 'Identité et connexion',
        label: 'Dernier signal reçu',
        value: new Date(node.lastSeenAt).toLocaleString('fr-FR'),
      },
      {
        section: 'Activité du nœud',
        label: 'Traitements réussis',
        value: activity?.succeeded ?? 'Indisponible',
      },
      { section: 'Activité du nœud', label: 'Échecs', value: activity?.failed ?? 'Indisponible' },
      {
        section: 'Activité du nœud',
        label: 'Annulations',
        value: activity?.cancelled ?? 'Indisponible',
      },
      {
        section: 'Activité du nœud',
        label: 'Tâches alimentées par ce nœud',
        value: activity?.sourceTasks ?? 'Indisponible',
      },
      {
        section: 'Activité du nœud',
        label: 'Dernière activité',
        value: activity?.lastActivity
          ? new Date(activity.lastActivity).toLocaleString('fr-FR')
          : activity
            ? 'Aucune activité enregistrée'
            : 'Indisponible',
      },
    ];
  });
  protected readonly capabilityFields = computed<readonly DetailField[]>(() => {
    const capabilities = this.rawNode()?.capabilities ?? [];
    const tools = capabilities.filter((capability) => !capability.toolId.startsWith('plugin/'));
    return [
      { label: 'Outils détectés', value: tools.length },
      {
        label: 'Plugins déclarés',
        value: capabilities.filter((capability) => capability.toolId.startsWith('plugin/')).length,
      },
      {
        label: 'Accélérations vérifiées',
        value:
          [...new Set(tools.flatMap((tool) => tool.accelerationTypes))].join(' · ') ||
          'Aucune déclarée',
      },
      {
        label: 'Encodeurs vérifiés utilisables',
        value: [
          ...new Set(
            tools
              .flatMap((tool) => tool.encoderChecks ?? [])
              .filter((check) => check.status === 'usable')
              .map((check) => check.encoderId),
          ),
        ].length,
      },
    ];
  });
  protected readonly capabilityRows = computed<readonly DataTableRow[]>(() =>
    (this.rawNode()?.capabilities ?? []).map((tool) => ({
      id: tool.toolId,
      name: tool.toolId,
      version: tool.version,
      category: tool.toolId.startsWith('plugin/') ? 'Plugin' : 'Outil',
      media: tool.mediaTypes.join(' · ') || 'Non déclaré',
      acceleration: tool.accelerationTypes.join(' · ') || 'Aucune déclarée',
      encoders: tool.encoders
        ? [...new Set(tool.encoders)].filter((name) => name !== '=').length
        : 'Non déclarés',
      verifiedEncoders: tool.encoderChecks
        ? `${tool.encoderChecks.filter((check) => check.status === 'usable').length} utilisable(s) · ${tool.encoderChecks.filter((check) => check.status === 'detected').length} détecté(s)`
        : 'Vérification non déclarée',
    })),
  );
  protected readonly capabilityColumns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Outil / Plugin' },
    { key: 'version', label: 'Version' },
    { key: 'media', label: 'Médias' },
    { key: 'acceleration', label: 'Accélération vérifiée' },
    { key: 'encoders', label: 'Encodeurs' },
    { key: 'verifiedEncoders', label: 'Encodeurs vérifiés' },
  ];
  protected readonly capabilityActions: readonly DataTableRowAction[] = [
    {
      id: 'encoders',
      label: 'Voir les encodeurs',
      visibleWhen: (row) => Number(row['encoders']) > 0,
    },
  ];
  protected showEncoders(event: DataTableActionEvent): void {
    const tool = this.rawNode()?.capabilities.find((tool) => tool.toolId === event.row['id']);
    if (!tool) return;
    this.dialog.open(ConfirmationDialogComponent, {
      data: {
        title: tool.toolId + ' · État des encodeurs',
        mode: 'information',
        items: [...new Set(tool.encoders ?? [])]
          .filter((name) => name !== '=')
          .sort()
          .map((name) => {
            const check = tool.encoderChecks?.find((item) => item.encoderId === name);
            return {
              label: name,
              detail:
                check?.status === 'usable'
                  ? 'Vérifié utilisable par un encodage synthétique'
                  : check?.status === 'detected'
                    ? 'Détecté, mais le test d’encodage n’a pas réussi'
                    : 'Déclaré par FFmpeg · capacité non vérifiée',
            };
          }),
        message:
          'Les vérifications utilisent une image synthétique et ne lisent aucun média de la bibliothèque.',
      },
    });
  }
  protected readonly nodeActions: readonly DataTableRowAction[] = [
    { id: 'identity', label: 'Nom et étiquettes', visibleWhen: () => this.isAdmin() },
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger',
      visibleWhen: () => !this.removalBusy(),
      confirmation: (row) => ({
        title: 'Supprimer ce nœud ?',
        message:
          'Le nœud « ' +
          row['name'] +
          ' » sera retiré du cluster et ses accès révoqués. Les fichiers et l’historique seront conservés. Pour le rattacher de nouveau, son identité locale devra être renouvelée.',
        confirmLabel: 'Supprimer le nœud',
      }),
    },
  ];
  protected onNodeAction(event: DataTableActionEvent): void {
    if (event.actionId === 'identity') {
      const node = this.filteredNodes().find((item) => item.id === event.row['id']);
      if (node) void this.editNodeIdentity(node);
      return;
    }
    if (event.actionId !== 'delete' || this.removalBusy()) return;
    const id = String(event.row['id']);
    this.removalBusy.set(true);
    this.http
      .delete<void>('/api/v1/nodes/' + id)
      .pipe(
        catchError((error: { error?: { title?: string } }) => {
          this.notifications.show(error.error?.title ?? 'Impossible de supprimer ce nœud.');
          return EMPTY;
        }),
        finalize(() => this.removalBusy.set(false)),
      )
      .subscribe(() => {
        this.nodes.update((nodes) => nodes.filter((node) => node.id !== id));
        if (this.selectedNode()?.id === id) this.onDetailClosed();
        this.loadLibraryAccessSummary();
        this.notifications.show('Nœud supprimé du cluster.');
      });
  }
  protected confirmNodeRemoval(): void {
    const row = this.selectedNode();
    if (!row || this.removalBusy()) return;
    this.dialog
      .open(ConfirmationDialogComponent, {
        data: this.nodeActions.find((action) => action.id === 'delete')!.confirmation!(row),
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (confirmed) this.onNodeAction({ actionId: 'delete', row });
      });
  }
  protected readonly actionTitle = computed(() =>
    this.actionType() === 'schedule'
      ? 'Calendrier du nœud'
      : this.actionType() === 'identity'
        ? 'Nom et étiquettes du nœud'
        : this.actionType() === 'processing'
          ? 'Limite globale du nœud'
          : this.actionType() === 'library-access'
            ? `Accès à ${this.selectedNodeLibrary()?.name ?? 'la bibliothèque'}`
            : this.actionType() === 'create-library'
              ? 'Déclarer une bibliothèque'
              : this.createFormData.title,
  );
  protected readonly actionDescription = computed(() =>
    this.actionType() === 'schedule'
      ? 'Plages horaires pendant lesquelles ce nœud peut démarrer de nouveaux traitements.'
      : this.actionType() === 'identity'
        ? 'Le nom affiché est distinct du nom envoyé par heartbeat. Les étiquettes locales remplacent celles des groupes.'
        : this.actionType() === 'processing'
          ? 'Plafond simultané, toutes bibliothèques confondues.'
          : this.actionType() === 'library-access'
            ? 'Renseignez le chemin local de cette bibliothèque pour ce nœud.'
            : this.actionType() === 'create-library'
              ? 'Le serveur crée la définition partagée. Vous pourrez ensuite autoriser les nœuds et configurer leurs chemins.'
              : this.createFormData.description,
  );
  protected readonly createLibraryFormData: RecordFormData = {
    title: 'Déclarer une bibliothèque',
    description:
      'Créez une définition partagée côté serveur. Cette création ne donne pas encore accès aux médias à ce nœud.',
    submitLabel: 'Déclarer la bibliothèque',
    fields: [{ id: 'name', label: 'Nom', type: 'text', required: true, maxLength: 128 }],
  };
  protected readonly nodeLibraryColumns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Bibliothèque', sortable: true },
    {
      key: 'isEnabled',
      label: 'Accès',
      toggle: {
        showStatusLabel: false,
        ariaLabel: (row, checked) =>
          `${checked ? 'Désactiver' : row['mode'] === 'unsupported' ? 'Configurer' : 'Activer'} l’accès à ${row['name']} sur ce nœud`,
        disabledWhen: (row) =>
          this.nodeAccessActivationBusy().has(`${this.selectedNode()?.id}:${row['id']}`),
        confirmation: (row, checked) =>
          checked
            ? undefined
            : {
                title: 'Désactiver cette bibliothèque sur ce nœud ?',
                message: `« ${row['name']} » restera déclarée et ses accès sur les autres nœuds ne changeront pas.`,
                confirmLabel: 'Désactiver sur ce nœud',
              },
      },
    },
    { key: 'modeLabel', label: 'Mode autorisé', sortable: true },
    { key: 'pathLabel', label: 'Chemin sur ce nœud' },
  ];
  protected readonly nodeLibraryActions: readonly DataTableRowAction[] = [
    { id: 'edit', label: 'Modifier' },
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger',
      confirmation: (row) => ({
        title: 'Retirer cette bibliothèque du nœud ?',
        message: `L’accès de ce nœud à « ${row['name']} » sera supprimé. La bibliothèque et ses accès sur les autres nœuds resteront inchangés.`,
        confirmLabel: 'Retirer du nœud',
        headerColor: '#b42318',
        confirmColor: '#b42318',
      }),
    },
  ];
  protected readonly nodeLibraries = computed<readonly NodeLibraryRecord[]>(() => {
    const node = this.selectedNode();
    if (!node) return [];
    return this.allLibraries().flatMap((library) => {
      const access = this.nodeAccessByLibrary()
        .get(library.id)
        ?.find((item) => item.nodeId === node.id);
      if (!access) return [];
      const localMode =
        access.mode === 'direct'
          ? 'Accès direct'
          : access.mode === 'transfer'
            ? 'Transfert worker'
            : 'Aucun accès configuré';
      return [
        {
          id: library.id,
          name: library.name,
          isEnabled: access.isEnabled,
          mode: access.mode,
          modeLabel: `${localMode}${access.mode !== 'unsupported' && !access.isEnabled ? ' · Désactivée sur ce nœud' : ''}${library.isActive ? '' : ' · Désactivée globalement'}`,
          pathLabel:
            access.localPath ??
            (access.mode === 'transfer'
              ? 'Espace temporaire du worker'
              : access.mode === 'unsupported'
                ? 'Aucun accès actif'
                : 'Chemin à configurer'),
        },
      ];
    });
  });
  protected readonly availableLibraries = computed(() => {
    const allowedIds = new Set(this.nodeLibraries().map((library) => library.id));
    return this.allLibraries().filter((library) => library.isActive && !allowedIds.has(library.id));
  });
  protected readonly nodeLibraryRows = computed<readonly DataTableRow[]>(() =>
    this.nodeLibraries(),
  );
  protected readonly selectedNodeLibraryAccess = computed(() => {
    const nodeId = this.selectedNode()?.id;
    const libraryId = this.selectedNodeLibrary()?.id;
    return nodeId && libraryId
      ? (this.nodeAccessByLibrary()
          .get(libraryId)
          ?.find((item) => item.nodeId === nodeId) ?? null)
      : null;
  });

  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom du nœud', sortable: true },
    { key: 'roleLabel', label: 'Rôle', sortable: true },
    { key: 'capabilities', label: 'Capacités observées', sortable: true },
    { key: 'libraryAccess', label: 'Accès aux bibliothèques', sortable: true },
    { key: 'state', label: 'État', sortable: true },
    { key: 'lastSignal', label: 'Dernier signal', sortable: true },
  ];

  protected readonly roleOptions = computed<readonly CategoryTabOption[]>(() => {
    const nodes = this.nodes();
    return [
      { value: 'all', label: 'Tous', count: nodes.length },
      ...NODE_ROLE_OPTIONS.map((role) => ({
        ...role,
        count: nodes.filter((node) => node.roles.includes(role.value)).length,
      })),
    ];
  });

  protected readonly filteredNodes = computed(() => {
    const role = this.selectedRole();
    return this.nodes()
      .filter((node) => role === 'all' || node.roles.includes(role as NodeRole))
      .map((node) => this.toRecord(node));
  });

  protected readonly enrollmentColumns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Worker détecté', sortable: true },
    { key: 'rolesLabel', label: 'Rôle annoncé', sortable: true },
    { key: 'capabilitiesLabel', label: 'Capacités', sortable: true },
    { key: 'expiresAtLabel', label: 'Expiration', sortable: true },
  ];
  protected readonly enrollmentRows = computed<readonly PendingNodeEnrollmentRecord[]>(() =>
    this.pendingEnrollments().map((request) => ({
      id: request.id,
      name: request.name,
      rolesLabel: request.roles.map((role) => NODE_ROLE_LABELS[role]).join(' · '),
      capabilitiesLabel: request.capabilities.length
        ? request.capabilities.map((item) => `${item.toolId} ${item.version}`).join(' · ')
        : 'Aucun outil déclaré',
      expiresAtLabel: this.formatExpiry(request.expiresAt),
    })),
  );

  constructor() {
    this.loadLibraryAccessSummary();
    timer(0, 10_000)
      .pipe(
        switchMap(() =>
          forkJoin({
            nodes: this.clusterApi.listNodes(),
            pending: this.http.get<readonly PendingNodeEnrollmentResponse[]>(
              '/api/v1/nodes/enrollment-requests',
            ),
          }).pipe(
            catchError(() => {
              this.loadState.set(this.nodes().length ? 'ready' : 'error');
              return EMPTY;
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ nodes, pending }) => {
        this.nodes.set(nodes);
        this.pendingEnrollments.set(pending);
        this.loadState.set('ready');
      });
  }

  protected onRoleChange(role: string): void {
    this.selectedRole.set(role);
  }

  protected addNode(): void {
    this.actionType.set('enrollment');
    this.enrollmentView.set('requests');
    this.selectedEnrollment.set(null);
    this.actionOpen.set(true);
    this.refreshEnrollmentWindow();
  }

  private refreshEnrollmentWindow(): void {
    this.clusterApi
      .enrollmentWindow()
      .pipe(catchError(() => EMPTY))
      .subscribe((window) => this.enrollmentWindow.set(window));
  }

  protected openEnrollmentWindow(): void {
    this.clusterApi
      .openEnrollmentWindow(10)
      .pipe(
        catchError(() => {
          this.notifications.show('La fenêtre d’enrôlement n’a pas pu être ouverte.');
          return EMPTY;
        }),
      )
      .subscribe((window) => {
        this.enrollmentWindow.set(window);
        this.notifications.show('L’enrôlement direct est ouvert pendant 10 minutes.');
      });
  }

  protected closeEnrollmentWindow(): void {
    this.clusterApi
      .closeEnrollmentWindow()
      .pipe(
        catchError(() => {
          this.notifications.show('La fenêtre d’enrôlement n’a pas pu être fermée.');
          return EMPTY;
        }),
      )
      .subscribe(() => {
        this.enrollmentWindow.set({ isOpen: false, openUntil: null });
        this.notifications.show('L’enrôlement direct est fermé.');
      });
  }

  protected closeCreateNode(): void {
    this.actionOpen.set(false);
    this.selectedEnrollment.set(null);
    this.selectedNodeLibrary.set(null);
  }

  protected showPairingKey(): void {
    this.selectedEnrollment.set(null);
    this.enrollmentView.set('key');
  }

  protected showPendingRequests(): void {
    this.enrollmentView.set('requests');
  }

  protected onEnrollmentOpen(row: DataTableRow): void {
    const request = this.pendingEnrollments().find((item) => item.id === row['id']) ?? null;
    this.selectedEnrollment.set(request);
    if (request) this.enrollmentView.set('review');
  }

  protected approveEnrollment(): void {
    const request = this.selectedEnrollment();
    if (!request) return;
    this.http
      .post<NodeResponse>(`/api/v1/nodes/enrollment-requests/${request.id}/approve`, {})
      .pipe(
        catchError((error: { status?: number }) => {
          this.notifications.show(
            error.status === 404
              ? 'Cette demande a expiré ou a déjà été traitée. Actualisez la liste.'
              : 'Le worker n’a pas pu être accepté. Vérifiez que le serveur est disponible.',
          );
          return EMPTY;
        }),
      )
      .subscribe((node) => {
        this.nodes.update((current) => [
          ...current.filter((existing) => existing.id !== node.id),
          node,
        ]);
        this.pendingEnrollments.update((current) => current.filter((item) => item.id !== node.id));
        this.selectedEnrollment.set(null);
        this.enrollmentView.set('requests');
        this.loadState.set('ready');
        this.notifications.show(`Le nœud « ${node.name} » a rejoint le cluster.`);
      });
  }

  protected acceptNode(value: Record<string, string>): void {
    const pairingKey = value['pairingKey']?.trim().toUpperCase();
    if (!pairingKey) return;
    this.actionOpen.set(false);
    this.http
      .post<NodeResponse>('/api/v1/nodes/enrollment-requests/approve', { pairingKey })
      .pipe(
        catchError((error: { status?: number }) => {
          this.notifications.show(
            error.status === 404
              ? 'Cette clé est invalide, expirée ou déjà utilisée. Générez-en une nouvelle depuis le worker.'
              : 'Le nœud n’a pas pu être accepté. Vérifiez que le serveur est disponible.',
          );
          return EMPTY;
        }),
      )
      .subscribe((node) => {
        this.nodes.update((current) => [
          ...current.filter((existing) => existing.id !== node.id),
          node,
        ]);
        this.pendingEnrollments.update((current) => current.filter((item) => item.id !== node.id));
        this.loadState.set('ready');
        this.notifications.show(`Le nœud « ${node.name} » a rejoint le cluster.`);
      });
  }

  protected formatExpiry(value: string): string {
    const seconds = Math.max(0, Math.ceil((new Date(value).getTime() - Date.now()) / 1000));
    if (seconds < 60) return `Dans ${seconds} s`;
    return `Dans ${Math.ceil(seconds / 60)} min`;
  }

  protected formatExpiryTime(value: string): string {
    return new Intl.DateTimeFormat('fr-FR', { hour: '2-digit', minute: '2-digit' }).format(
      new Date(value),
    );
  }

  protected rolesLabel(roles: readonly NodeRole[]): string {
    return roles.map((role) => NODE_ROLE_LABELS[role]).join(' · ');
  }

  protected capabilitiesLabel(capabilities: readonly NodeToolCapability[]): string {
    return capabilities.length
      ? capabilities.map((item) => `${item.toolId} ${item.version}`).join(' · ')
      : 'Aucun outil détecté';
  }

  protected onNodeOpen(row: DataTableRow): void {
    this.selectedNode.set(this.filteredNodes().find((node) => node.id === row['id']) ?? null);
    this.loadNodeLibraries();
  }

  protected onDetailClosed(): void {
    this.selectedNode.set(null);
  }

  protected nodeAccessOption(node: NodeRecord): LibraryNodeAccessOption {
    const original = this.nodes().find((item) => item.id === node.id);
    return { id: node.id, name: node.name, roles: original?.roles ?? [] };
  }

  protected openAddLibrary(): void {
    this.actionType.set('create-library');
    this.actionOpen.set(true);
  }

  protected addExistingLibrary(): void {
    const library = this.allLibraries().find(
      (item) => item.id === this.selectedAvailableLibraryId(),
    );
    if (library) this.editNodeLibraryAccess(library);
  }

  protected editNodeLibraryAccess(library: NodeLibraryRecord | LibraryResponse): void {
    this.selectedNodeLibrary.set(
      this.allLibraries().find((item) => item.id === library.id) ?? (library as LibraryResponse),
    );
    this.actionType.set('library-access');
    this.actionOpen.set(true);
  }

  protected onNodeLibraryToggle(event: DataTableToggleEvent): void {
    const node = this.selectedNode();
    const library = this.allLibraries().find((item) => item.id === event.row['id']);
    if (!node || !library) return;
    if (event.checked && event.row['mode'] === 'unsupported') {
      this.editNodeLibraryAccess(library);
      return;
    }
    const busyKey = `${node.id}:${library.id}`;
    this.nodeAccessActivationBusy.update((current) => new Set(current).add(busyKey));
    this.http
      .put<LibraryNodeAccessResponse>(
        `/api/v1/libraries/${library.id}/node-access/${node.id}/activation`,
        { isEnabled: event.checked },
      )
      .pipe(
        catchError(() => {
          this.notifications.show('L’accès de ce nœud n’a pas pu être modifié.');
          return EMPTY;
        }),
        finalize(() =>
          this.nodeAccessActivationBusy.update((current) => {
            const next = new Set(current);
            next.delete(busyKey);
            return next;
          }),
        ),
      )
      .subscribe((updated) => {
        this.nodeAccessByLibrary.update((current) =>
          new Map(current).set(
            library.id,
            (current.get(library.id) ?? []).map((item) =>
              item.nodeId === node.id ? updated : item,
            ),
          ),
        );
        this.notifications.show(
          `Accès à « ${library.name} » ${event.checked ? 'réactivé' : 'désactivé'} pour ${node.name}.`,
        );
      });
  }

  protected onNodeLibraryAction(event: DataTableActionEvent): void {
    const node = this.selectedNode();
    const library = this.allLibraries().find((item) => item.id === event.row['id']);
    if (!node || !library) return;
    if (event.actionId === 'edit') {
      this.editNodeLibraryAccess(library);
      return;
    }

    const existing = this.nodeAccessByLibrary().get(library.id) ?? [];
    const access = existing.find((item) => item.nodeId === node.id);
    if (!access) return;
    const configured = existing
      .filter((item) => event.actionId !== 'delete' || item.nodeId !== node.id)
      .map((item): LibraryNodeAccessValue => ({
        nodeId: item.nodeId,
        mode: item.mode,
        isEnabled: item.isEnabled,
        localPath: item.localPath,
        canRead: item.canRead,
        canWrite: item.canWrite,
        transferSourceNodeId: item.transferSourceNodeId,
      }));
    this.saveNodeLibraryConfiguration(library, node, configured, event.actionId);
  }

  protected createLibraryFromNode(value: Record<string, string>): void {
    this.http
      .post<LibraryResponse>('/api/v1/libraries', value)
      .pipe(
        catchError((error: { status?: number }) => {
          this.notifications.show(
            error.status === 409
              ? 'Une bibliothèque porte déjà ce nom.'
              : 'La bibliothèque n’a pas pu être déclarée sur le serveur.',
          );
          return EMPTY;
        }),
      )
      .subscribe((library) => {
        this.allLibraries.update((current) => [...current, library]);
        this.notifications.show(
          `La bibliothèque « ${library.name} » est déclarée sur le serveur. Autorisez ce nœud pour configurer son accès.`,
        );
        this.editNodeLibraryAccess(library);
      });
  }

  protected saveNodeLibraryAccess(values: readonly LibraryNodeAccessValue[]): void {
    const node = this.selectedNode();
    const library = this.selectedNodeLibrary();
    if (!node || !library || values.length !== 1) return;
    const existing = this.nodeAccessByLibrary().get(library.id) ?? [];
    const merged = [
      ...existing
        .filter((item) => item.nodeId !== node.id)
        .map((item) => ({
          nodeId: item.nodeId,
          mode: item.mode,
          isEnabled: item.isEnabled,
          localPath: item.localPath,
          canRead: item.canRead,
          canWrite: item.canWrite,
          transferSourceNodeId: item.transferSourceNodeId,
        })),
      values[0],
    ];
    this.saveNodeLibraryConfiguration(library, node, merged, 'edit');
  }

  private saveNodeLibraryConfiguration(
    library: LibraryResponse,
    node: NodeRecord,
    configured: readonly LibraryNodeAccessValue[],
    action: string,
  ): void {
    this.accessFormBusy.set(true);
    this.http
      .put<readonly LibraryNodeAccessResponse[]>(`/api/v1/libraries/${library.id}/node-access`, {
        nodes: configured,
      })
      .pipe(
        catchError((error: { error?: { errors?: Record<string, string[]> } }) => {
          this.accessFormBusy.set(false);
          const validation = error.error?.errors
            ? Object.values(error.error.errors).flat()[0]
            : undefined;
          this.notifications.show(
            validation ?? 'La configuration de cette bibliothèque n’a pas pu être enregistrée.',
          );
          return EMPTY;
        }),
      )
      .subscribe((accesses) => {
        this.accessFormBusy.set(false);
        this.nodeAccessByLibrary.update((current) => new Map(current).set(library.id, accesses));
        this.actionOpen.set(false);
        this.notifications.show(
          action === 'delete'
            ? `Bibliothèque « ${library.name} » retirée du nœud ${node.name}.`
            : `Accès à « ${library.name} » enregistré pour ${node.name}.`,
        );
      });
  }

  private loadNodeLibraries(): void {
    const node = this.selectedNode();
    if (!node) return;
    this.nodeLibraryLoading.set(true);
    this.clusterApi
      .listLibraries()
      .pipe(
        switchMap((libraries) =>
          forkJoin({
            libraries: Promise.resolve(libraries),
            accessLists: libraries.length
              ? forkJoin(
                  libraries.map((library) =>
                    this.http.get<readonly LibraryNodeAccessResponse[]>(
                      `/api/v1/libraries/${library.id}/node-access`,
                    ),
                  ),
                )
              : Promise.resolve([] as readonly (readonly LibraryNodeAccessResponse[])[]),
          }),
        ),
        catchError(() => {
          this.nodeLibraryLoading.set(false);
          this.notifications.show(
            'Les bibliothèques autorisées pour ce nœud n’ont pas pu être chargées.',
          );
          return EMPTY;
        }),
      )
      .subscribe(({ libraries, accessLists }) => {
        this.allLibraries.set(libraries);
        this.nodeAccessByLibrary.set(
          new Map(libraries.map((library, index) => [library.id, accessLists[index]])),
        );
        this.libraryAccessLoadState.set('ready');
        this.nodeLibraryLoading.set(false);
      });
  }

  private loadLibraryAccessSummary(): void {
    this.clusterApi
      .listLibraries()
      .pipe(
        switchMap((libraries) =>
          forkJoin({
            libraries: of(libraries),
            accessLists: libraries.length
              ? forkJoin(
                  libraries.map((library) =>
                    this.http.get<readonly LibraryNodeAccessResponse[]>(
                      `/api/v1/libraries/${library.id}/node-access`,
                    ),
                  ),
                )
              : of([] as readonly (readonly LibraryNodeAccessResponse[])[]),
          }),
        ),
        catchError(() => {
          this.libraryAccessLoadState.set('error');
          return EMPTY;
        }),
      )
      .subscribe(({ libraries, accessLists }) => {
        this.allLibraries.set(libraries);
        this.nodeAccessByLibrary.set(
          new Map(libraries.map((library, index) => [library.id, accessLists[index]])),
        );
        this.libraryAccessLoadState.set('ready');
      });
  }

  private toRecord(node: NodeResponse): NodeRecord {
    const roleLabel = node.roles.map((role) => NODE_ROLE_LABELS[role]).join(' · ');
    const capabilityLabels = node.capabilities.map((capability) => {
      const acceleration = capability.accelerationTypes.length
        ? ` · ${capability.accelerationTypes.join(', ')}`
        : '';
      return `${capability.toolId} ${capability.version}${acceleration}`;
    });
    const worker = node.roles.includes('worker');
    return {
      id: node.id,
      name: node.displayName?.trim() || node.name,
      roleFilter: `|${node.roles.join('|')}|`,
      roleLabel,
      capabilities: capabilityLabels.length
        ? capabilityLabels.join(' · ')
        : 'Aucune capacité déclarée',
      libraryAccess: this.libraryAccessSummary(node.id),
      workerLoad: worker ? `${node.maxConcurrentTasks} tâche(s) max.` : 'Non applicable',
      queueStatus: 'Non disponible',
      state: node.status === 'online' ? 'En ligne' : 'Hors ligne',
      lastSignal: this.formatLastSeen(node.lastSeenAt),
    };
  }

  private libraryAccessSummary(nodeId: string): string {
    const loadState = this.libraryAccessLoadState();
    if (loadState === 'loading') return 'Chargement…';
    if (loadState === 'error') return 'Indisponible';
    const accesses = this.allLibraries().flatMap((library) => {
      const access = this.nodeAccessByLibrary()
        .get(library.id)
        ?.find((item) => item.nodeId === nodeId);
      return access && access.isEnabled && access.mode !== 'unsupported'
        ? [{ library, access }]
        : [];
    });
    if (!accesses.length) return 'À configurer';
    const activeCount = accesses.filter(({ library }) => library.isActive).length;
    const inactiveCount = accesses.length - activeCount;
    return inactiveCount
      ? `${activeCount} active(s) · ${inactiveCount} désactivée(s)`
      : `${activeCount} bibliothèque(s) active(s)`;
  }

  private formatLastSeen(value: string): string {
    const elapsedSeconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
    if (elapsedSeconds < 5) return 'À l’instant';
    if (elapsedSeconds < 60) return `Il y a ${elapsedSeconds} s`;
    const minutes = Math.floor(elapsedSeconds / 60);
    if (minutes < 60) return `Il y a ${minutes} min`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `Il y a ${hours} h`;
    return new Intl.DateTimeFormat('fr-FR', { dateStyle: 'medium', timeStyle: 'short' }).format(
      new Date(value),
    );
  }
}
