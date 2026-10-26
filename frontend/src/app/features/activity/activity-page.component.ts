import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  TemplateRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatDialog } from '@angular/material/dialog';
import { SearchFieldComponent } from '../../shared/ui/search-field.component';
import { MatTabsModule } from '@angular/material/tabs';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  Observable,
  catchError,
  exhaustMap,
  firstValueFrom,
  of,
  switchMap,
  timeout,
  timer,
} from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { SummaryTilesComponent, SummaryTile } from '../../shared/ui/summary-tiles.component';
import { executionStateLabel } from '../../shared/models/execution';
import { AiAssistantActivity } from '../../shared/models/ai-tools';
import { formatFullDateTime } from '../../shared/models/date-format';
import { joinBadges, mediaBadges } from '../../shared/models/media-badges';
import { AiToolsService } from '../../shared/services/ai-tools.service';
import { ExecutionDetailPolling } from '../../shared/services/execution-detail-polling';
import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import { FileDownload } from '../../shared/services/file-download';
import { NotificationService } from '../../shared/services/notification.service';
import {
  ConfirmationDialogComponent,
  ConfirmationDialogData,
} from '../../shared/ui/confirmation-dialog.component';
import {
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
} from '../../shared/ui/data-table.component';
import { DetailDrawerComponent } from '../../shared/ui/detail-drawer/detail-drawer';
import { DetailField, DetailFieldListComponent } from '../../shared/ui/detail-field-list.component';
import { ExecutionPipelineLogsComponent } from '../../shared/ui/execution-pipeline-logs.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { StatusBadgeComponent } from '../../shared/ui/status-badge.component';
import { CategoryTabOption } from '../../shared/ui/category-tabs.component';
import { SubnavComponent } from '../../shared/ui/subnav.component';
import {
  ACTIVITY_GROUPS,
  ACTIVITY_STATES,
  ActivityGroup,
  ActivityItem,
  ActivityKind,
  aiDuration,
  aiErrorName,
  aiItem,
  aiOperationName,
  aiProviderName,
  aiStatusName,
  executionItem,
  metadataSize,
} from './activity-items';

type KindFilter = 'all' | ActivityKind;
type GroupFilter = 'all' | ActivityGroup;
type ActivityRole = 'operate' | 'diagnose';

const VIEW_TO_GROUP: Readonly<Record<string, ActivityGroup>> = {
  queue: 'queue',
  active: 'active',
  errors: 'errors',
  cancelled: 'cancelled',
  history: 'done',
};
const ROLE_GROUPS: Readonly<Record<ActivityRole, readonly ActivityGroup[]>> = {
  operate: ['queue', 'active'],
  diagnose: ['errors', 'cancelled', 'done'],
};
const GROUP_TO_ROLE: Readonly<Record<ActivityGroup, ActivityRole>> = {
  queue: 'operate',
  active: 'operate',
  errors: 'diagnose',
  cancelled: 'diagnose',
  done: 'diagnose',
};

function roleFromView(view: string | null, kind: string | null): ActivityRole {
  const group = view ? VIEW_TO_GROUP[view] : undefined;
  if (view === 'active' && kind === 'ai') return 'diagnose';
  if (group) return GROUP_TO_ROLE[group];
  if (view === 'operate' || view === 'diagnose') return view;
  return kind === 'ai' ? 'diagnose' : 'operate';
}
/** Nombre de lignes « Terminés » affichées avant « Afficher plus ». */
const DONE_PAGE_SIZE = 25;
const AI_REFRESH_MS = 15000;

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-activity-page',
  imports: [
    DataTableComponent,
    DetailDrawerComponent,
    DetailFieldListComponent,
    ExecutionPipelineLogsComponent,
    IconComponent,
    MatButtonModule,
    MatCheckboxModule,
    MatChipsModule,
    MatProgressBarModule,
    SummaryTilesComponent,
    SearchFieldComponent,
    MatTabsModule,
    SubnavComponent,
    RouterLink,
    StatusBadgeComponent,
  ],
  templateUrl: './activity-page.component.html',
  styleUrl: './activity-page.component.scss',
})
export class ActivityPageComponent {
  protected readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly tracking = inject(ExecutionTracking);
  private readonly detailPolling = inject(ExecutionDetailPolling);
  private readonly aiTools = inject(AiToolsService);
  private readonly downloads = inject(FileDownload);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly groups = ACTIVITY_GROUPS;
  protected readonly roleTabs: readonly CategoryTabOption[] = [
    { value: 'operate', label: 'Exploiter' },
    { value: 'diagnose', label: 'Diagnostiquer' },
  ];
  protected readonly roleFilter = signal<ActivityRole>(
    roleFromView(
      this.route.snapshot.queryParamMap.get('view'),
      this.route.snapshot.queryParamMap.get('type'),
    ),
  );
  protected readonly visibleGroups = computed(() =>
    this.groups.filter(
      (group) =>
        ROLE_GROUPS[this.roleFilter()].includes(group.id) ||
        (this.roleFilter() === 'diagnose' &&
          this.kindFilter() !== 'execution' &&
          this.auth.isAdmin() &&
          group.id === 'active'),
    ),
  );
  protected readonly activityHeading = computed(() =>
    this.roleFilter() === 'operate'
      ? 'Opérations à prendre en charge'
      : 'Résultats à diagnostiquer',
  );
  protected readonly activitySubheading = computed(() =>
    this.roleFilter() === 'operate'
      ? 'Annulez une opération autorisée ou ouvrez son suivi détaillé.'
      : 'Consultez les tentatives, leurs événements, leurs résultats et les demandes IA.',
  );
  private readonly aiRefresh = signal(0);
  protected readonly aiState = signal<{
    readonly items: readonly AiAssistantActivity[];
    readonly error: boolean;
    readonly loaded: boolean;
  }>({ items: [], error: false, loaded: false });

  protected readonly kindFilter = signal<KindFilter>(
    this.route.snapshot.queryParamMap.get('type') === 'ai'
      ? 'ai'
      : this.route.snapshot.queryParamMap.get('type') === 'execution'
        ? 'execution'
        : 'all',
  );
  protected readonly groupFilter = signal<GroupFilter>(
    VIEW_TO_GROUP[this.route.snapshot.queryParamMap.get('view') ?? ''] ?? 'all',
  );
  protected readonly search = signal('');
  protected readonly pageOffset = signal(0);
  protected readonly pageNumber = computed(() => this.pageOffset() / DONE_PAGE_SIZE + 1);
  protected readonly trackingState = toSignal(
    toObservable(
      computed(() => ({
        states:
          this.kindFilter() === 'ai' ||
          (this.roleFilter() === 'diagnose' && this.groupFilter() === 'active')
            ? []
            : this.groupFilter() === 'all'
              ? ROLE_GROUPS[this.roleFilter()].flatMap((group) => ACTIVITY_STATES[group])
              : ACTIVITY_STATES[this.groupFilter() as ActivityGroup],
        offset: this.pageOffset(),
      })),
    ).pipe(
      switchMap(({ states, offset }) =>
        this.tracking.observeStates(states, DONE_PAGE_SIZE, offset),
      ),
    ),
    {
      initialValue: INITIAL_EXECUTION_TRACKING,
    },
  );
  private readonly openedItem = signal<ActivityItem | null>(null);
  protected readonly summary = computed<readonly SummaryTile[]>(() => {
    const data = this.trackingState().data;
    if (this.roleFilter() === 'operate')
      return [
        {
          label: 'À traiter',
          value: data ? String(data.queuedCount) : '—',
          detail: 'Exécutions en attente dans la file',
        },
        {
          label: 'En cours',
          value: data ? String(data.activeCount) : '—',
          detail: 'Traitements actifs sur le cluster',
        },
      ];
    return [
      {
        label: 'Erreurs',
        value: data ? String(data.failedCount) : '—',
        detail: 'Tentatives à examiner',
        tone: data?.failedCount ? 'attention' : 'default',
      },
      {
        label: 'Annulés',
        value: data ? String(data.cancelledCount) : '—',
        detail: 'Traitements annulés',
      },
      {
        label: 'Terminés',
        value: data ? String(data.succeededCount + (data.deferredCount ?? 0)) : '—',
        detail: 'Traitements réussis ou différés',
      },
    ];
  });
  protected readonly busy = signal(false);
  protected readonly selectedKey = signal<string | null>(
    this.route.snapshot.queryParamMap.get('request')
      ? 'ai:' + this.route.snapshot.queryParamMap.get('request')
      : null,
  );
  protected readonly selectedDetailTab = signal(0);
  protected readonly selectedActionIds = signal<ReadonlySet<string>>(new Set());
  protected readonly selectedActionItems = computed(() =>
    this.allItems().filter(
      (item) => item.execution && this.selectedActionIds().has(item.execution.id),
    ),
  );
  protected readonly selectedCancelableItems = computed(() =>
    this.selectedActionItems().filter((item) =>
      ['queued', 'running'].includes(item.execution?.state ?? ''),
    ),
  );

  protected readonly allItems = computed<readonly ActivityItem[]>(() => [
    ...(this.trackingState().data?.executions ?? []).map(executionItem),
    ...this.aiState().items.map(aiItem),
  ]);
  /** Éléments du type et de la recherche courants, avant filtre d’état (pour les compteurs). */
  private readonly scoped = computed(() => {
    const kind = this.kindFilter();
    const query = this.search().trim().toLocaleLowerCase('fr');
    const role = this.roleFilter();
    return this.allItems().filter(
      (item) =>
        (role === 'operate'
          ? ROLE_GROUPS.operate.includes(item.group) && item.kind === 'execution'
          : ROLE_GROUPS.diagnose.includes(item.group) ||
            (item.kind === 'ai' && item.group === 'active')) &&
        (kind === 'all' || item.kind === kind) &&
        (!query ||
          [
            item.title,
            item.subtitle,
            item.stateLabel,
            item.execution?.library,
            item.execution?.media,
          ]
            .filter(Boolean)
            .some((value) => String(value).toLocaleLowerCase('fr').includes(query))),
    );
  });
  protected readonly counts = computed(() => {
    const counts: Record<GroupFilter, number> = {
      all: 0,
      active: 0,
      queue: 0,
      errors: 0,
      cancelled: 0,
      done: 0,
    };
    for (const item of this.scoped()) {
      counts.all++;
      counts[item.group]++;
    }
    const data = this.trackingState().data;
    if (data && this.kindFilter() !== 'ai' && !this.search().trim()) {
      const executionCounts = {
        active: data.activeCount,
        queue: data.queuedCount,
        errors: data.failedCount,
        cancelled: data.cancelledCount,
        done: data.succeededCount + (data.deferredCount ?? 0),
      };
      if (this.roleFilter() === 'diagnose') executionCounts.active = 0;
      for (const group of ACTIVITY_GROUPS)
        counts[group.id] =
          executionCounts[group.id] +
          this.scoped().filter((item) => item.kind === 'ai' && item.group === group.id).length;
      counts.all = this.visibleGroups().reduce((total, group) => total + counts[group.id], 0);
    }
    return counts;
  });
  protected readonly sections = computed(() => {
    const filter = this.groupFilter();
    const scoped = this.scoped();
    return this.visibleGroups()
      .filter((group) => filter === 'all' || filter === group.id)
      .map((group) => {
        const all = scoped
          .filter((item) => item.group === group.id)
          .sort((a, b) =>
            group.id === 'queue' ? a.sortTime - b.sortTime : b.sortTime - a.sortTime,
          );
        const items = all;
        const more =
          this.kindFilter() !== 'ai' &&
          (this.trackingState().moreStates ?? []).some((state) =>
            ACTIVITY_STATES[group.id].includes(state),
          );
        return { ...group, items, total: this.counts()[group.id], more };
      })
      .filter((section) => section.items.length > 0);
  });
  protected readonly countText = computed(() => {
    if (this.kindFilter() === 'ai')
      return this.aiState().loaded
        ? `${this.counts().all} demande(s) chargée(s)`
        : 'Chargement de l’assistant…';
    const data = this.trackingState().data;
    if (!data) return 'Données indisponibles';
    if (this.roleFilter() === 'operate')
      return `${data.queuedCount} à traiter · ${data.activeCount} en cours`;
    const aiRunningCount =
      this.kindFilter() === 'execution'
        ? 0
        : this.scoped().filter((item) => item.kind === 'ai' && item.group === 'active').length;
    return `${data.failedCount} ${data.failedCount === 1 ? 'erreur' : 'erreurs'} · ${data.cancelledCount} annulée(s) · ${data.succeededCount + (data.deferredCount ?? 0)} terminée(s)${aiRunningCount ? ` · ${aiRunningCount} demande(s) IA en cours` : ''}`;
  });
  protected readonly emptyStateReady = computed(() => {
    if (this.roleFilter() === 'operate' || this.kindFilter() === 'execution')
      return this.trackingState().status === 'ready';
    if (this.kindFilter() === 'ai') return this.aiState().loaded && !this.aiState().error;
    return (
      this.trackingState().status === 'ready' &&
      (!this.auth.isAdmin() || this.aiState().loaded || this.aiState().error)
    );
  });
  protected readonly emptyStateTitle = computed(() =>
    this.roleFilter() === 'operate'
      ? 'Aucune opération à prendre en charge'
      : 'Aucun résultat à diagnostiquer',
  );
  protected readonly emptyStateDescription = computed(() =>
    this.roleFilter() === 'operate'
      ? 'La file et les traitements actifs sont vides.'
      : 'Aucun élément ne correspond à ces filtres.',
  );
  protected readonly feedback = computed(() => {
    const state = this.trackingState();
    if (this.kindFilter() !== 'ai' && state.status === 'loading')
      return this.roleFilter() === 'operate'
        ? 'Chargement des opérations…'
        : 'Chargement de l’historique…';
    if (this.kindFilter() !== 'ai' && state.status === 'error')
      return 'Actualisation impossible. Les dernières données reçues peuvent être obsolètes.';
    if (
      this.roleFilter() === 'diagnose' &&
      this.aiState().error &&
      this.kindFilter() !== 'execution'
    )
      return 'Les demandes de l’assistant sont momentanément indisponibles.';
    if (this.roleFilter() === 'diagnose' && this.kindFilter() === 'ai' && !this.aiState().loaded)
      return 'Chargement des demandes de l’assistant…';
    return '';
  });

  protected readonly selected = computed(
    () =>
      this.allItems().find((item) => item.key === this.selectedKey()) ??
      (this.openedItem()?.key === this.selectedKey() ? this.openedItem() : null),
  );
  protected readonly selectedExecution = computed(() => this.selected()?.execution ?? null);
  protected readonly selectedAi = computed(() => this.selected()?.ai ?? null);

  private readonly detailState = this.detailPolling.observe(
    toObservable(computed(() => this.selectedExecution()?.id ?? null)),
    toObservable(this.selectedDetailTab),
  );
  protected readonly eventState = toSignal(this.detailState.events, {
    initialValue: { message: 'Chargement de la chronologie…', rows: [] },
  });
  protected readonly artifactsState = toSignal(this.detailState.artifacts, {
    initialValue: { message: '', rows: [] },
  });
  protected readonly pipelineState = toSignal(this.detailState.pipeline, {
    initialValue: { message: '', steps: [] },
  });

  protected readonly drawerContent = viewChild<TemplateRef<unknown>>('detailContent');
  protected readonly drawerFooter = viewChild<TemplateRef<unknown>>('detailFooter');

  protected readonly drawerTitle = computed(() => this.selected()?.title ?? 'Détails');
  protected readonly drawerDescription = computed(() => {
    const item = this.selected();
    if (!item) return '';
    return [item.kind === 'ai' ? 'Assistant IA' : 'Traitement', item.stateLabel].join(' · ');
  });
  protected readonly canCancel = computed(() => {
    const state = this.selectedExecution()?.state;
    return state === 'queued' || state === 'running';
  });
  protected readonly canRetry = computed(() => this.selectedExecution()?.state === 'failed');

  protected readonly eventColumns: readonly DataTableColumn[] = [
    { key: 'revision', label: 'Ordre' },
    { key: 'createdAt', label: 'Date' },
    { key: 'state', label: 'État' },
    { key: 'detail', label: 'Détail' },
  ];
  protected readonly artifactColumns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Fichier' },
    { key: 'size', label: 'Taille' },
  ];
  protected readonly artifactActions = [{ id: 'download', label: 'Télécharger' }];
  protected readonly videoEncodingColumns: readonly DataTableColumn[] = [
    { key: 'flowNode', label: 'Bloc' },
    { key: 'codec', label: 'Codec' },
    { key: 'encoder', label: 'Encodeur' },
    { key: 'ffmpeg', label: 'Version FFmpeg' },
    { key: 'acceleration', label: 'Accélération' },
    { key: 'quality', label: 'Qualité appliquée' },
    { key: 'preset', label: 'Preset appliqué' },
    { key: 'profile', label: 'Profil figé' },
    { key: 'worker', label: 'Identifiant worker' },
  ];

  protected readonly videoEncodingRows = computed<readonly DataTableRow[]>(() =>
    (this.selectedExecution()?.videoEncodings ?? []).map((encoding, index) => ({
      id: `${encoding.flowNodeId}-${encoding.encoderId}-${index}`,
      flowNode: encoding.flowNodeId,
      worker: encoding.workerId,
      codec: encoding.codec.toUpperCase(),
      encoder: encoding.encoderId,
      ffmpeg: encoding.ffmpegVersion,
      acceleration: encoding.acceleration,
      quality: `${encoding.qualityMode} (${encoding.qualityOption}) : ${encoding.quality}`,
      preset:
        encoding.presetOption && encoding.presetValue
          ? `${encoding.presetOption} : ${encoding.presetValue}`
          : 'Sans preset',
      profile: `${encoding.profileName} · v${encoding.profileVersion}`,
    })),
  );

  protected readonly executionFields = computed<readonly DetailField[]>(() => {
    const e = this.selectedExecution();
    if (!e) return [];
    const duration = e.startedAt
      ? Math.max(
          0,
          Math.floor(
            (Date.parse(e.finishedAt ?? this.trackingState().data?.observedAt ?? e.updatedAt) -
              Date.parse(e.startedAt)) /
              1000,
          ),
        ) + ' s'
      : '—';
    return [
      { section: 'Exécution', label: 'Identifiant', value: e.id, copiable: true },
      { section: 'Exécution', label: 'État', value: executionStateLabel(e) },
      {
        section: 'Attribution',
        label: 'Serveur propriétaire',
        value: e.ownership?.ownerPeerId ?? '—',
        copiable: true,
      },
      {
        section: 'Attribution',
        label: 'Époque',
        value: e.ownership ? String(e.ownership.epoch) : '—',
      },
      {
        section: 'Exécution',
        label: 'Progression',
        value: e.progress === null ? '—' : e.progress + ' %',
      },
      { section: 'Exécution', label: 'Étape ou résultat', value: e.detail || '—' },
      {
        section: 'Média',
        label: 'Caractéristiques',
        value: joinBadges(mediaBadges(e.beforeMetadata)) || '—',
      },
      { section: 'Média', label: 'Taille avant', value: metadataSize(e.beforeMetadata) },
      { section: 'Média', label: 'Taille après', value: metadataSize(e.afterMetadata) },
      {
        section: 'Média',
        label: 'Résultat publié',
        value: e.publishedPath ?? '—',
        copiable: Boolean(e.publishedPath),
      },
      { section: 'Source et traitement', label: 'Bibliothèque', value: e.library },
      { section: 'Source et traitement', label: 'Média', value: e.media },
      {
        section: 'Source et traitement',
        label: 'Flow',
        value: `${e.flow} · v${e.flowVersion}`,
      },
      { section: 'Source et traitement', label: 'Worker', value: e.node ?? '—' },
      { section: 'Chronologie', label: 'Ajoutée à la file', value: formatFullDateTime(e.queuedAt) },
      { section: 'Chronologie', label: 'Démarrée', value: formatFullDateTime(e.startedAt) },
      { section: 'Chronologie', label: 'Terminée', value: formatFullDateTime(e.finishedAt) },
      { section: 'Chronologie', label: 'Durée', value: duration },
      { section: 'Chronologie', label: 'Tentative', value: e.attempt },
    ];
  });
  protected readonly beforeSize = computed(() =>
    metadataSize(this.selectedExecution()?.beforeMetadata),
  );
  protected readonly afterSize = computed(() =>
    metadataSize(this.selectedExecution()?.afterMetadata),
  );

  protected readonly aiFields = computed<readonly DetailField[]>(() => {
    const a = this.selectedAi();
    if (!a) return [];
    return [
      { section: 'Demande', label: 'Identifiant', value: a.requestId, copiable: true },
      ...(a.conversationTitle
        ? [{ section: 'Demande', label: 'Conversation', value: a.conversationTitle }]
        : []),
      { section: 'Demande', label: 'Opération', value: aiOperationName(a.operation) },
      { section: 'Demande', label: 'Fournisseur', value: aiProviderName(a.provider) },
      { section: 'Demande', label: 'État', value: aiStatusName(a.status) },
      {
        section: 'Demande',
        label: 'Catégorie d’erreur',
        value: a.errorCategory ? aiErrorName(a.errorCategory) : '—',
      },
      { section: 'Demande', label: 'Outils', value: a.tools?.length ? a.tools.join(', ') : '—' },
      { section: 'Chronologie', label: 'Début', value: formatFullDateTime(a.startedAt) },
      {
        section: 'Chronologie',
        label: 'Dernière mise à jour',
        value: formatFullDateTime(a.updatedAt),
      },
      {
        section: 'Chronologie',
        label: 'Durée',
        value: a.durationMilliseconds !== null ? aiDuration(a.durationMilliseconds) : '—',
      },
    ];
  });

  constructor() {
    toObservable(
      computed(() => ({
        refresh: this.aiRefresh(),
        enabled:
          this.auth.isAdmin() &&
          this.roleFilter() === 'diagnose' &&
          this.kindFilter() !== 'execution',
      })),
    )
      .pipe(
        switchMap(({ enabled }) =>
          enabled
            ? timer(0, AI_REFRESH_MS).pipe(
                exhaustMap(() =>
                  this.aiTools.listAssistantActivity().pipe(
                    timeout(20000),
                    catchError(() => of(null)),
                  ),
                ),
              )
            : of([]),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((items) =>
        this.aiState.update((state) =>
          items ? { items, error: false, loaded: true } : { ...state, error: true },
        ),
      );
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      this.applyQueryParams(params.get('view'), params.get('type'));
    });
  }

  protected setKind(kind: KindFilter): void {
    const selectedKind = kind === 'ai' && !this.auth.isAdmin() ? 'all' : kind;
    if (selectedKind === 'ai' && this.roleFilter() !== 'diagnose') {
      this.roleFilter.set('diagnose');
      this.groupFilter.set('all');
    }
    this.kindFilter.set(selectedKind);
    this.pageOffset.set(0);
    this.clearActionSelection();
    this.close();
    this.syncViewInUrl();
  }
  protected setRole(value: string): void {
    if (value !== 'operate' && value !== 'diagnose') return;
    const role = value;
    if (role === this.roleFilter()) return;
    this.roleFilter.set(role);
    this.groupFilter.set('all');
    if (role === 'operate') this.kindFilter.set('all');
    this.pageOffset.set(0);
    this.clearActionSelection();
    this.close();
    this.syncViewInUrl();
  }
  protected setGroup(group: GroupFilter): void {
    if (group !== 'all') {
      const diagnosticAiActivity =
        group === 'active' && this.roleFilter() === 'diagnose' && this.auth.isAdmin();
      if (diagnosticAiActivity) this.kindFilter.set('ai');
      else {
        const role = GROUP_TO_ROLE[group];
        this.roleFilter.set(role);
        if (role === 'operate' && this.kindFilter() === 'ai') this.kindFilter.set('all');
      }
    }
    this.groupFilter.set(group);
    this.pageOffset.set(0);
    this.clearActionSelection();
    this.close();
    this.syncViewInUrl();
  }
  protected nextPage(group: ActivityGroup): void {
    const offset = this.groupFilter() === group ? this.pageOffset() : 0;
    this.roleFilter.set(GROUP_TO_ROLE[group]);
    this.groupFilter.set(group);
    this.pageOffset.set(offset + DONE_PAGE_SIZE);
    this.close();
    this.syncViewInUrl();
  }
  protected previousPage(): void {
    this.pageOffset.update((offset) => Math.max(0, offset - DONE_PAGE_SIZE));
  }
  private applyQueryParams(view: string | null, type: string | null): void {
    const role = roleFromView(view, type);
    const requestedGroup = view ? VIEW_TO_GROUP[view] : undefined;
    const group =
      requestedGroup &&
      (ROLE_GROUPS[role].includes(requestedGroup) ||
        (role === 'diagnose' && requestedGroup === 'active' && type === 'ai'))
        ? requestedGroup
        : 'all';
    const kind: KindFilter =
      type === 'ai' && role === 'diagnose' && this.auth.isAdmin()
        ? 'ai'
        : type === 'execution'
          ? 'execution'
          : 'all';
    const changed =
      role !== this.roleFilter() || group !== this.groupFilter() || kind !== this.kindFilter();
    this.roleFilter.set(role);
    this.groupFilter.set(group);
    this.kindFilter.set(kind);
    if (changed) {
      this.pageOffset.set(0);
      this.clearActionSelection();
      this.close();
    }
  }
  private syncViewInUrl(): void {
    const group = this.groupFilter();
    const view = group === 'all' ? this.roleFilter() : group === 'done' ? 'history' : group;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        view,
        type: this.kindFilter() === 'all' ? null : this.kindFilter(),
        request: null,
      },
      queryParamsHandling: 'merge',
    });
  }
  protected refresh(): void {
    this.tracking.refresh();
    this.aiRefresh.update((value) => value + 1);
  }
  protected open(item: ActivityItem): void {
    this.openedItem.set(item);
    this.selectedDetailTab.set(0);
    this.selectedKey.set(item.key);
  }
  protected close(): void {
    this.selectedKey.set(null);
  }
  protected downloadArtifact(row: DataTableRow): void {
    const id = this.selectedExecution()?.id;
    if (id)
      this.downloads.fromUrl(
        '/api/v1/executions/' +
          encodeURIComponent(id) +
          '/artifacts/' +
          encodeURIComponent(String(row['id'])),
      );
  }

  protected canCancelItem(item: ActivityItem): boolean {
    return item.execution?.state === 'queued' || item.execution?.state === 'running';
  }
  protected toggleActionSelection(item: ActivityItem, checked: boolean): void {
    const id = item.execution?.id;
    if (!id) return;
    this.selectedActionIds.update((current) => {
      const next = new Set(current);
      if (checked) next.add(id);
      else next.delete(id);
      return next;
    });
  }
  protected clearActionSelection(): void {
    this.selectedActionIds.set(new Set());
  }
  protected async runBulkCancel(): Promise<void> {
    if (this.busy()) return;
    const targets = this.selectedCancelableItems();
    if (!targets.length) return;
    const accepted = await firstValueFrom(
      this.dialog
        .open(ConfirmationDialogComponent, {
          data: {
            title: `Annuler ${targets.length} exécution(s) ?`,
            message: 'Une demande sera envoyée pour les exécutions encore en attente ou en cours.',
            confirmLabel: 'Annuler les exécutions',
            variant: 'danger',
          },
        })
        .afterClosed(),
    );
    if (!accepted) return;
    this.busy.set(true);
    const results = await Promise.allSettled(
      targets.map((item) => firstValueFrom(this.tracking.cancel(item.execution!.id))),
    );
    this.busy.set(false);
    this.selectedActionIds.set(new Set());
    this.tracking.refresh();
    const succeeded = results.filter((result) => result.status === 'fulfilled').length;
    this.done(`${succeeded}/${targets.length} exécution(s) annulée(s).`);
  }
  protected cancelSelected(): void {
    const e = this.selectedExecution();
    if (e)
      this.confirm(
        {
          title: 'Annuler cette exécution ?',
          message: `« ${e.media} » recevra une demande d’annulation. Un traitement en cours attend la confirmation du worker.`,
          confirmLabel: 'Annuler l’exécution',
          variant: 'danger',
        },
        () => this.perform('cancel', e.id),
      );
  }
  protected retry(id: string, media: string): void {
    this.confirm(
      {
        title: 'Créer une nouvelle tentative ?',
        message: `Une nouvelle exécution de « ${media} » sera ajoutée à la file. La tentative en échec restera dans l’historique.`,
        confirmLabel: 'Mettre en file',
      },
      () => this.perform('retry', id),
    );
  }

  private confirm(data: ConfirmationDialogData, onConfirm: () => void): void {
    this.dialog
      .open(ConfirmationDialogComponent, { data })
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((confirmed: boolean | undefined) => {
        if (confirmed) onConfirm();
      });
  }

  private perform(action: 'cancel' | 'retry', id: string): void {
    if (this.busy()) return;
    this.busy.set(true);
    const request: Observable<unknown> =
      action === 'cancel' ? this.tracking.cancel(id) : this.tracking.retry(id);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.done(
          action === 'cancel'
            ? 'Demande d’annulation enregistrée.'
            : 'Nouvelle tentative mise en file.',
        );
        if (action === 'retry') this.setGroup('queue');
      },
      error: () => this.done('La demande a échoué ou l’état de l’exécution a changé.'),
    });
  }
  private done(message: string): void {
    this.busy.set(false);
    this.tracking.refresh();
    this.notifications.show(message);
  }
}
