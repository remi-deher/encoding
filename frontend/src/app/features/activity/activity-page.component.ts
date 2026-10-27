import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  TemplateRef,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTabsModule } from '@angular/material/tabs';
import {
  catchError,
  debounceTime,
  exhaustMap,
  finalize,
  firstValueFrom,
  map,
  of,
  startWith,
  switchMap,
  tap,
  timeout,
  timer,
} from 'rxjs';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import {
  PageShellContextService,
  PageShellSearchResult,
} from '../../core/layout/page-shell-context';
import { ClusterApi } from '../../shared/services/cluster-api';
import { LibraryActivity } from '../../shared/services/library-activity';
import {
  LibraryActivityFile,
  LibraryActivityResponse,
  LibraryActivityStatus,
} from '../../shared/models/library-activity';
import { ExecutionResponse, executionStateLabel } from '../../shared/models/execution';
import { formatFileSize } from '../../shared/models/file-size';
import { formatFullDateTime, formatRelativeTime } from '../../shared/models/date-format';
import { mediaBadges, joinBadges } from '../../shared/models/media-badges';
import { ExecutionTracking } from '../../shared/services/execution-tracking';
import { ExecutionDetailPolling } from '../../shared/services/execution-detail-polling';
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
import { SubnavComponent, SubnavOption } from '../../shared/ui/subnav.component';
import { SubnavTemplateComponent } from '../../shared/templates/subnav-template.component';
import { MatDialog } from '@angular/material/dialog';

const ACTIVITY_STATUSES: readonly { value: LibraryActivityStatus; label: string }[] = [
  { value: 'untreated', label: 'Non traité' },
  { value: 'active', label: 'En cours de traitement' },
  { value: 'done', label: 'Déjà traité' },
  { value: 'failed', label: 'Échec du traitement' },
  { value: 'disabled', label: 'Traitement désactivé' },
];
const PAGE_SIZE = 50;
const PAGE_REFRESH_MS = 10000;
interface ActivityPageState {
  readonly status: 'loading' | 'ready' | 'error';
  readonly data: LibraryActivityResponse | null;
  readonly revision: number;
}

function validStatus(value: string | null): value is LibraryActivityStatus {
  return ACTIVITY_STATUSES.some((status) => status.value === value);
}

function statusFromLegacyView(view: string | null): LibraryActivityStatus {
  switch (view) {
    case 'active':
      return 'active';
    case 'errors':
      return 'failed';
    case 'history':
      return 'done';
    default:
      return 'untreated';
  }
}

function viewFromStatus(status: LibraryActivityStatus): string {
  switch (status) {
    case 'active':
      return 'active';
    case 'failed':
      return 'errors';
    case 'done':
      return 'history';
    default:
      return 'queue';
  }
}

function baseName(path: string): string {
  return path.split(/[\\/]/).pop() || path;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-activity-page',
  imports: [
    PageHeaderComponent,
    DetailDrawerComponent,
    DetailFieldListComponent,
    ExecutionPipelineLogsComponent,
    IconComponent,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatProgressBarModule,
    MatSelectModule,
    MatTabsModule,
    SubnavComponent,
    SubnavTemplateComponent,
    StatusBadgeComponent,
    DataTableComponent,
  ],
  templateUrl: './activity-page.component.html',
  styleUrl: './activity-page.component.scss',
})
export class ActivityPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly pageShellContext = inject(PageShellContextService);
  private readonly activityApi = inject(LibraryActivity);
  private readonly clusterApi = inject(ClusterApi);
  private readonly tracking = inject(ExecutionTracking);
  private readonly detailPolling = inject(ExecutionDetailPolling);
  private readonly downloads = inject(FileDownload);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly statuses = ACTIVITY_STATUSES;
  protected readonly status = signal<LibraryActivityStatus>(
    validStatus(this.route.snapshot.queryParamMap.get('state'))
      ? (this.route.snapshot.queryParamMap.get('state') as LibraryActivityStatus)
      : statusFromLegacyView(this.route.snapshot.queryParamMap.get('view')),
  );
  protected readonly search = signal('');
  protected readonly libraryFilter = signal<string | null>(
    this.route.snapshot.queryParamMap.get('library'),
  );
  protected readonly flowFilter = signal<string | null>(
    this.route.snapshot.queryParamMap.get('flow'),
  );
  protected readonly nodeFilter = signal<string | null>(
    this.route.snapshot.queryParamMap.get('node'),
  );
  protected readonly filtersOpen = signal(false);
  protected readonly refreshRevision = signal(0);
  protected readonly additionalFiles = signal<readonly LibraryActivityFile[]>([]);
  protected readonly loadingMore = signal(false);
  protected readonly actionBusy = signal(false);
  protected readonly feedback = signal('');
  protected readonly selectedActionIds = signal<ReadonlySet<string>>(new Set());
  protected readonly selectedFile = signal<LibraryActivityFile | null>(null);
  protected readonly selectedExecution = signal<ExecutionResponse | null>(null);
  protected readonly executionLoading = signal(false);
  protected readonly selectedDetailTab = signal(0);
  private readonly lastSuccessfulPage = signal<{
    readonly key: string;
    readonly data: LibraryActivityResponse;
  } | null>(null);

  protected readonly libraries = toSignal(
    this.clusterApi.listLibraries().pipe(catchError(() => of([]))),
    {
      initialValue: [],
    },
  );
  protected readonly nodes = toSignal(this.clusterApi.listNodes().pipe(catchError(() => of([]))), {
    initialValue: [],
  });
  protected readonly flowOptions = computed(() =>
    [
      ...new Set(
        this.libraries()
          .map((library) => library.flowName)
          .filter((name): name is string => !!name),
      ),
    ].sort((a, b) => a.localeCompare(b, 'fr-FR')),
  );

  private readonly request = computed(() => ({
    status: this.status(),
    libraryId: this.libraryFilter() ?? undefined,
    flow: this.flowFilter() ?? undefined,
    nodeId: this.nodeFilter() ?? undefined,
    search: this.search(),
    limit: PAGE_SIZE,
    offset: 0,
  }));
  private readonly requestKey = computed(() =>
    JSON.stringify({
      status: this.status(),
      libraryId: this.libraryFilter(),
      flow: this.flowFilter(),
      node: this.nodeFilter(),
      search: this.search().trim(),
    }),
  );
  private readonly pageState = toSignal(
    toObservable(
      computed(() => ({ query: this.request(), revision: this.refreshRevision() })),
    ).pipe(
      debounceTime(180),
      switchMap(({ query, revision }) => {
        const key = this.requestKey();
        return timer(0, PAGE_REFRESH_MS).pipe(
          exhaustMap(() =>
            this.activityApi.list(query).pipe(
              timeout(20000),
              tap((data) => this.lastSuccessfulPage.set({ key, data })),
              map((data): ActivityPageState => ({ status: 'ready', data, revision })),
              catchError(() => {
                const cached = this.lastSuccessfulPage();
                return of({
                  status: 'error',
                  data: cached?.key === key ? cached.data : null,
                  revision,
                });
              }),
            ),
          ),
          startWith({ status: 'loading', data: null, revision } as ActivityPageState),
        );
      }),
    ),
    { initialValue: { status: 'loading', data: null, revision: 0 } as ActivityPageState },
  );

  protected readonly files = computed(() => [
    ...(this.pageState().data?.files ?? []),
    ...this.additionalFiles(),
  ]);
  protected readonly counts = computed(() => this.pageState().data?.counts ?? null);
  protected readonly statusOptions = computed<readonly SubnavOption[]>(() =>
    ACTIVITY_STATUSES.map((item) => ({
      value: item.value,
      label: item.label,
      count: this.counts()?.[item.value],
    })),
  );
  protected readonly countText = computed(() => {
    const count = this.counts();
    if (!count) return 'Chargement des fichiers…';
    const total = count.untreated + count.active + count.done + count.failed + count.disabled;
    return `${total} fichier${total === 1 ? '' : 's'} suivi${total === 1 ? '' : 's'}`;
  });
  protected readonly currentStatusLabel = computed(
    () => ACTIVITY_STATUSES.find((item) => item.value === this.status())?.label ?? 'Activité',
  );
  protected readonly hasMore = computed(
    () => (this.pageState().data?.totalCount ?? 0) > this.files().length,
  );
  protected readonly selectedCancelableFiles = computed(() =>
    this.files().filter(
      (file) =>
        file.executionId &&
        file.executionState === 'running' &&
        this.selectedActionIds().has(file.executionId),
    ),
  );

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
  protected readonly drawerTitle = computed(() => {
    const file = this.selectedFile();
    return file ? baseName(file.relativePath) : 'Détails du fichier';
  });
  protected readonly drawerDescription = computed(() => {
    const file = this.selectedFile();
    if (!file) return '';
    return [file.library, this.statusLabel(file.status), file.flow].filter(Boolean).join(' · ');
  });
  protected readonly fileFields = computed<readonly DetailField[]>(() => {
    const file = this.selectedFile();
    if (!file) return [];
    return [
      { section: 'Fichier', label: 'Chemin relatif', value: file.relativePath, copiable: true },
      { section: 'Fichier', label: 'Taille', value: formatFileSize(file.bytes) },
      { section: 'Fichier', label: 'Modifié le', value: formatFullDateTime(file.modifiedAt) },
      { section: 'Bibliothèque', label: 'Nom', value: file.library },
      {
        section: 'Bibliothèque',
        label: 'État du traitement',
        value: this.statusLabel(file.status),
      },
      {
        section: 'Traitement',
        label: 'Flow attribué',
        value: file.flow ? `${file.flow} · v${file.flowVersion}` : 'Aucun flow attribué',
      },
      { section: 'Traitement', label: 'Nœud', value: file.node ?? '—' },
      {
        section: 'Traitement',
        label: 'Détail',
        value: file.detail ?? 'Aucun détail supplémentaire.',
      },
      {
        section: 'Traitement',
        label: 'Identifiant d’exécution',
        value: file.executionId ?? 'Aucune exécution',
        copiable: Boolean(file.executionId),
      },
    ];
  });
  protected readonly canCancel = computed(() =>
    ['queued', 'running'].includes(this.selectedExecution()?.state ?? ''),
  );
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
    const execution = this.selectedExecution();
    if (!execution) return [];
    const duration = execution.startedAt
      ? Math.max(
          0,
          Math.floor(
            (Date.parse(execution.finishedAt ?? execution.updatedAt) -
              Date.parse(execution.startedAt)) /
              1000,
          ),
        ) + ' s'
      : '—';
    return [
      { section: 'Exécution', label: 'Identifiant', value: execution.id, copiable: true },
      { section: 'Exécution', label: 'État', value: executionStateLabel(execution) },
      {
        section: 'Exécution',
        label: 'Progression',
        value: execution.progress === null ? '—' : execution.progress + ' %',
      },
      { section: 'Exécution', label: 'Étape ou résultat', value: execution.detail || '—' },
      {
        section: 'Média',
        label: 'Caractéristiques',
        value: joinBadges(mediaBadges(execution.beforeMetadata)) || '—',
      },
      {
        section: 'Média',
        label: 'Taille avant',
        value: this.metadataSize(execution.beforeMetadata),
      },
      {
        section: 'Média',
        label: 'Taille après',
        value: this.metadataSize(execution.afterMetadata),
      },
      {
        section: 'Média',
        label: 'Résultat publié',
        value: execution.publishedPath ?? '—',
        copiable: Boolean(execution.publishedPath),
      },
      { section: 'Source et traitement', label: 'Bibliothèque', value: execution.library },
      { section: 'Source et traitement', label: 'Média', value: execution.media },
      {
        section: 'Source et traitement',
        label: 'Flow',
        value: `${execution.flow} · v${execution.flowVersion}`,
      },
      { section: 'Source et traitement', label: 'Worker', value: execution.node ?? '—' },
      {
        section: 'Chronologie',
        label: 'Ajoutée à la file',
        value: formatFullDateTime(execution.queuedAt),
      },
      { section: 'Chronologie', label: 'Démarrée', value: formatFullDateTime(execution.startedAt) },
      {
        section: 'Chronologie',
        label: 'Terminée',
        value: formatFullDateTime(execution.finishedAt),
      },
      { section: 'Chronologie', label: 'Durée', value: duration },
      { section: 'Chronologie', label: 'Tentative', value: execution.attempt },
    ];
  });

  constructor() {
    effect(() => {
      this.requestKey();
      this.additionalFiles.set([]);
    });
    this.destroyRef.onDestroy(
      this.pageShellContext.register({
        searchPlaceholder: 'Rechercher dans Activité…',
        query: this.search,
        onSearchQueryChange: (query) => this.search.set(query),
        searchResults: (query) => this.searchResults(query),
        filter: {
          label: 'Filtres',
          activeCount: () =>
            Number(Boolean(this.libraryFilter())) +
            Number(Boolean(this.flowFilter())) +
            Number(Boolean(this.nodeFilter())),
          toggle: () => this.filtersOpen.update((open) => !open),
        },
      }),
    );
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      const requested = params.get('state');
      const state = validStatus(requested) ? requested : statusFromLegacyView(params.get('view'));
      if (state !== this.status()) this.status.set(state);
      const library = params.get('library');
      if (library !== this.libraryFilter()) this.libraryFilter.set(library);
      const flow = params.get('flow');
      if (flow !== this.flowFilter()) this.flowFilter.set(flow);
      const node = params.get('node');
      if (node !== this.nodeFilter()) this.nodeFilter.set(node);
    });
  }

  protected setStatus(value: string): void {
    if (!validStatus(value) || value === this.status()) return;
    this.status.set(value);
    this.selectedActionIds.set(new Set());
    this.close();
    this.syncQueryParams();
  }

  protected setLibrary(value: string): void {
    const libraryId = value || null;
    if (libraryId === this.libraryFilter()) return;
    this.libraryFilter.set(libraryId);
    this.selectedActionIds.set(new Set());
    this.close();
    this.syncQueryParams();
  }

  protected setFlow(value: string): void {
    const flow = value || null;
    if (flow === this.flowFilter()) return;
    this.flowFilter.set(flow);
    this.selectedActionIds.set(new Set());
    this.close();
    this.syncQueryParams();
  }

  protected setNode(value: string): void {
    const node = value || null;
    if (node === this.nodeFilter()) return;
    this.nodeFilter.set(node);
    this.selectedActionIds.set(new Set());
    this.close();
    this.syncQueryParams();
  }

  protected clearFilters(): void {
    this.libraryFilter.set(null);
    this.flowFilter.set(null);
    this.nodeFilter.set(null);
    this.selectedActionIds.set(new Set());
    this.close();
    this.syncQueryParams();
  }

  protected toggleActionSelection(file: LibraryActivityFile, checked: boolean): void {
    if (!file.executionId) return;
    this.selectedActionIds.update((current) => {
      const next = new Set(current);
      if (checked) next.add(file.executionId!);
      else next.delete(file.executionId!);
      return next;
    });
  }

  protected clearActionSelection(): void {
    this.selectedActionIds.set(new Set());
  }

  protected open(file: LibraryActivityFile): void {
    this.selectedFile.set(file);
    this.selectedExecution.set(null);
    this.selectedDetailTab.set(0);
    this.executionLoading.set(Boolean(file.executionId));
    if (!file.executionId) return;
    this.tracking
      .execution(file.executionId)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        catchError(() => of(null)),
      )
      .subscribe((execution) => {
        if (this.selectedFile()?.id !== file.id) return;
        this.selectedExecution.set(execution);
        this.executionLoading.set(false);
      });
  }

  protected close(): void {
    this.selectedFile.set(null);
    this.selectedExecution.set(null);
    this.executionLoading.set(false);
  }

  protected rowTime(file: LibraryActivityFile): string {
    const instant = file.updatedAt ?? file.modifiedAt;
    return formatRelativeTime(instant);
  }

  protected fileName(path: string): string {
    return baseName(path);
  }

  protected statusLabel(value: LibraryActivityStatus): string {
    return ACTIVITY_STATUSES.find((status) => status.value === value)?.label ?? value;
  }

  protected statusTone(
    value: LibraryActivityStatus,
  ): 'accent' | 'neutral' | 'danger' | 'success' | 'warning' {
    switch (value) {
      case 'active':
        return 'accent';
      case 'done':
        return 'success';
      case 'failed':
        return 'danger';
      case 'disabled':
        return 'warning';
      default:
        return 'neutral';
    }
  }

  protected async loadMore(): Promise<void> {
    const query = this.request();
    const expectedKey = this.requestKey();
    const offset = this.files().length;
    if (this.loadingMore() || !this.hasMore()) return;
    this.loadingMore.set(true);
    this.activityApi
      .list({ ...query, offset })
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.loadingMore.set(false)),
      )
      .subscribe({
        next: (response) => {
          if (this.requestKey() === expectedKey)
            this.additionalFiles.update((files) => [...files, ...response.files]);
        },
        error: () => this.feedback.set('Impossible de charger la suite des fichiers.'),
      });
  }

  protected async runBulkCancel(): Promise<void> {
    const targets = this.selectedCancelableFiles();
    if (!targets.length || this.actionBusy()) return;
    const accepted = await firstValueFrom(
      this.dialog
        .open(ConfirmationDialogComponent, {
          data: {
            title: `Annuler ${targets.length} exécution(s) ?`,
            message: 'Une demande sera envoyée pour les exécutions encore en cours.',
            confirmLabel: 'Annuler les exécutions',
            variant: 'danger',
          } satisfies ConfirmationDialogData,
        })
        .afterClosed(),
    );
    if (!accepted) return;
    this.actionBusy.set(true);
    const results = await Promise.allSettled(
      targets.map((file) => firstValueFrom(this.tracking.cancel(file.executionId!))),
    );
    this.actionBusy.set(false);
    this.selectedActionIds.set(new Set());
    this.tracking.refresh();
    this.refreshRevision.update((value) => value + 1);
    const succeeded = results.filter((result) => result.status === 'fulfilled').length;
    this.notifications.show(`${succeeded}/${targets.length} exécution(s) annulée(s).`);
  }

  protected cancelSelected(): void {
    const execution = this.selectedExecution();
    if (!execution) return;
    this.confirm(
      {
        title: 'Annuler cette exécution ?',
        message: `« ${execution.media} » recevra une demande d’annulation. Un traitement en cours attend la confirmation du worker.`,
        confirmLabel: 'Annuler l’exécution',
        variant: 'danger',
      },
      () => this.perform('cancel', execution.id),
    );
  }

  protected retrySelected(): void {
    const execution = this.selectedExecution();
    if (!execution) return;
    this.confirm(
      {
        title: 'Créer une nouvelle tentative ?',
        message: `Une nouvelle exécution de « ${execution.media} » sera ajoutée à la file. L’échec restera dans l’historique.`,
        confirmLabel: 'Mettre en file',
      },
      () => this.perform('retry', execution.id),
    );
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

  private searchResults(query: string): readonly PageShellSearchResult[] {
    const normalized = query.trim().toLocaleLowerCase('fr-FR');
    return this.files()
      .filter(
        (file) =>
          !normalized ||
          [file.relativePath, file.library, file.flow, file.node, this.statusLabel(file.status)]
            .filter(Boolean)
            .join(' ')
            .toLocaleLowerCase('fr-FR')
            .includes(normalized),
      )
      .slice(0, 8)
      .map((file) => ({
        id: `activity:${file.id}`,
        label: baseName(file.relativePath),
        description: `${file.library} · ${this.statusLabel(file.status)}`,
        group: 'Activité',
        glyph: '▣',
        run: () => this.open(file),
      }));
  }

  private syncQueryParams(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        state: this.status(),
        view: viewFromStatus(this.status()),
        library: this.libraryFilter(),
        flow: this.flowFilter(),
        node: this.nodeFilter(),
        type: null,
        request: null,
      },
      queryParamsHandling: 'merge',
    });
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
    if (this.actionBusy()) return;
    this.actionBusy.set(true);
    const succeed = () => {
      this.actionBusy.set(false);
      this.refreshRevision.update((value) => value + 1);
      this.tracking.refresh();
      this.notifications.show(
        action === 'cancel'
          ? 'Demande d’annulation enregistrée.'
          : 'Nouvelle tentative mise en file.',
      );
      if (action === 'retry') this.setStatus('active');
    };
    const fail = () => {
      this.actionBusy.set(false);
      this.feedback.set('La demande a échoué ou l’état de l’exécution a changé.');
    };
    if (action === 'cancel')
      this.tracking
        .cancel(id)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({ next: succeed, error: fail });
    else
      this.tracking
        .retry(id)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({ next: succeed, error: fail });
  }

  protected metadataSize(metadata: ExecutionResponse['beforeMetadata'] | undefined): string {
    const size = metadata?.format?.size;
    return size != null ? formatFileSize(Number(size)) : '—';
  }
}
