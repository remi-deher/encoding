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
import { MatDialog } from '@angular/material/dialog';
import { SearchFieldComponent } from '../../shared/ui/search-field.component';
import { MatTabsModule } from '@angular/material/tabs';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, catchError, of, switchMap, timer } from 'rxjs';
import { EXECUTION_STATE_LABELS } from '../../shared/models/execution';
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
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { StatusBadgeComponent } from '../../shared/ui/status-badge.component';
import {
  ACTIVITY_GROUPS,
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

const VIEW_TO_GROUP: Readonly<Record<string, ActivityGroup>> = {
  queue: 'queue',
  active: 'active',
  errors: 'errors',
  cancelled: 'cancelled',
  history: 'done',
};
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
    SearchFieldComponent,
    MatTabsModule,
    PageHeaderComponent,
    RouterLink,
    StatusBadgeComponent,
  ],
  templateUrl: './activity-page.component.html',
  styleUrl: './activity-page.component.scss',
})
export class ActivityPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly tracking = inject(ExecutionTracking);
  private readonly detailPolling = inject(ExecutionDetailPolling);
  private readonly aiTools = inject(AiToolsService);
  private readonly downloads = inject(FileDownload);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly groups = ACTIVITY_GROUPS;
  protected readonly trackingState = toSignal(this.tracking.state$, {
    initialValue: INITIAL_EXECUTION_TRACKING,
  });
  private readonly aiRefresh = signal(0);
  protected readonly aiState = signal<{
    readonly items: readonly AiAssistantActivity[];
    readonly error: boolean;
  }>({ items: [], error: false });

  protected readonly kindFilter = signal<KindFilter>(
    this.route.snapshot.queryParamMap.get('type') === 'ai' ? 'ai' : 'all',
  );
  protected readonly groupFilter = signal<GroupFilter>(
    VIEW_TO_GROUP[this.route.snapshot.queryParamMap.get('view') ?? ''] ?? 'all',
  );
  protected readonly search = signal('');
  protected readonly filtersOpen = signal(false);
  protected readonly activeFilters = computed(() => {
    const filters: { id: string; label: string; clear: () => void }[] = [];
    const kind = this.kindFilter();
    if (kind !== 'all') {
      filters.push({
        id: 'kind',
        label: kind === 'ai' ? 'Assistant IA' : 'Traitements',
        clear: () => this.setKind('all'),
      });
    }
    const group = this.groupFilter();
    if (group !== 'all') {
      const label = ACTIVITY_GROUPS.find((item) => item.id === group)?.label ?? group;
      filters.push({ id: 'group', label, clear: () => this.setGroup('all') });
    }
    return filters;
  });
  protected readonly doneLimit = signal(DONE_PAGE_SIZE);
  protected readonly busy = signal(false);
  protected readonly selectedKey = signal<string | null>(
    this.route.snapshot.queryParamMap.get('request')
      ? 'ai:' + this.route.snapshot.queryParamMap.get('request')
      : null,
  );
  protected readonly selectedDetailTab = signal(0);

  protected readonly allItems = computed<readonly ActivityItem[]>(() => [
    ...(this.trackingState().data?.executions ?? []).map(executionItem),
    ...this.aiState().items.map(aiItem),
  ]);
  /** Éléments du type et de la recherche courants, avant filtre d’état (pour les compteurs). */
  private readonly scoped = computed(() => {
    const kind = this.kindFilter();
    const query = this.search().trim().toLocaleLowerCase('fr');
    return this.allItems().filter(
      (item) =>
        (kind === 'all' || item.kind === kind) &&
        (!query ||
          [item.title, item.subtitle, item.stateLabel, item.execution?.library]
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
    return counts;
  });
  protected readonly kindCounts = computed(() => {
    const items = this.allItems();
    return {
      all: items.length,
      execution: items.filter((item) => item.kind === 'execution').length,
      ai: items.filter((item) => item.kind === 'ai').length,
    };
  });
  protected readonly sections = computed(() => {
    const filter = this.groupFilter();
    const scoped = this.scoped();
    return ACTIVITY_GROUPS.filter((group) => filter === 'all' || filter === group.id)
      .map((group) => {
        const all = scoped
          .filter((item) => item.group === group.id)
          .sort((a, b) =>
            group.id === 'queue' ? a.sortTime - b.sortTime : b.sortTime - a.sortTime,
          );
        const items = group.id === 'done' ? all.slice(0, this.doneLimit()) : all;
        return { ...group, items, total: all.length };
      })
      .filter((section) => section.total > 0);
  });
  protected readonly countText = computed(() => {
    const data = this.trackingState().data;
    return data
      ? `${data.queuedCount} à traiter · ${data.activeCount} en cours · ${data.failedCount} erreurs`
      : 'Données indisponibles';
  });
  protected readonly feedback = computed(() => {
    const state = this.trackingState();
    if (state.status === 'loading') return 'Chargement de l’activité…';
    if (state.status === 'error')
      return 'Actualisation impossible. Les dernières données reçues peuvent être obsolètes.';
    if (this.aiState().error)
      return 'Les demandes de l’assistant sont momentanément indisponibles.';
    return '';
  });

  protected readonly selected = computed(
    () => this.allItems().find((item) => item.key === this.selectedKey()) ?? null,
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
      { section: 'Exécution', label: 'État', value: EXECUTION_STATE_LABELS[e.state] },
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
    toObservable(this.aiRefresh)
      .pipe(
        switchMap(() =>
          timer(0, AI_REFRESH_MS).pipe(
            switchMap(() => this.aiTools.listAssistantActivity().pipe(catchError(() => of(null)))),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((items) =>
        this.aiState.update((state) =>
          items ? { items, error: false } : { items: state.items, error: true },
        ),
      );
  }

  protected setKind(kind: KindFilter): void {
    this.kindFilter.set(kind);
    this.doneLimit.set(DONE_PAGE_SIZE);
  }
  protected setGroup(group: GroupFilter): void {
    this.groupFilter.set(group);
    this.doneLimit.set(DONE_PAGE_SIZE);
  }
  protected clearFilters(): void {
    this.setKind('all');
    this.setGroup('all');
  }
  protected showMoreDone(): void {
    this.doneLimit.update((limit) => limit + DONE_PAGE_SIZE);
  }
  protected refresh(): void {
    this.tracking.refresh();
    this.aiRefresh.update((value) => value + 1);
  }
  protected open(item: ActivityItem): void {
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

  protected canRetryItem(item: ActivityItem): boolean {
    return item.execution?.state === 'failed';
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
        title: 'Relancer cette exécution ?',
        message: `Une nouvelle exécution de « ${media} » sera ajoutée à la file.`,
        confirmLabel: 'Relancer',
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
      next: () =>
        this.done(
          action === 'cancel' ? 'Demande d’annulation enregistrée.' : 'Relance ajoutée à la file.',
        ),
      error: () => this.done('La demande a échoué ou l’état de l’exécution a changé.'),
    });
  }
  private done(message: string): void {
    this.busy.set(false);
    this.tracking.refresh();
    this.notifications.show(message);
  }
}
