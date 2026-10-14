import { toSignal, toObservable, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FileDownload } from '../../shared/services/file-download';
import { MatButtonModule } from '@angular/material/button';
import { catchError, exhaustMap, forkJoin, map, of, switchMap, startWith, timer } from 'rxjs';
import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import { EXECUTION_STATE_LABELS } from '../../shared/models/execution';
import { formatFileSize } from '../../shared/models/file-size';
import { joinBadges, mediaBadges } from '../../shared/models/media-badges';
import { NotificationService } from '../../shared/services/notification.service';
import { Component, computed, inject, signal, DestroyRef } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import {
  DataTableActionEvent,
  DataTableColumn,
  DataTableRow,
  DataTableRowAction,
  DataTableSelectionAction,
  DataTableSelectionActionEvent,
} from '../../shared/ui/data-table.component';
import {
  ExecutionMonitorTemplateComponent,
  ExecutionMonitorView,
} from '../../shared/templates/execution-monitor-template.component';

const VIEWS: readonly ExecutionMonitorView[] = ['queue', 'active', 'errors', 'cancelled', 'history'];

interface ExecutionRecord extends DataTableRow {
  readonly id: string;
  readonly media: string;
  readonly library: string;
  readonly flow: string;
  readonly node: string;
  readonly state: string;
  readonly group: ExecutionMonitorView;
  readonly detail: string;
  readonly queuedAt: string;
  readonly startedAt: string;
  readonly finishedAt: string;
  readonly duration: string;
  readonly attempt: number;
}

@Component({
  selector: 'app-task-monitoring-page',
  imports: [ExecutionMonitorTemplateComponent, MatButtonModule],
  templateUrl: './task-monitoring-page.component.html',
})
export class TaskMonitoringPageComponent {
  private readonly downloads = inject(FileDownload);
  private readonly tracking = inject(ExecutionTracking);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly trackingState = toSignal(this.tracking.state$, {
    initialValue: INITIAL_EXECUTION_TRACKING,
  });
  protected readonly busy = signal(false);
  protected readonly selectedExecutionId = signal<string | null>(null);
  protected readonly eventState = toSignal(
    toObservable(this.selectedExecutionId).pipe(
      switchMap((id) =>
        id
          ? timer(0, 5000).pipe(
              exhaustMap(() =>
                this.tracking.events(id).pipe(
                  map((events) => ({
                    message: '',
                    rows: events.map((e) => ({
                      id: e.id,
                      revision: e.revision,
                      state: EXECUTION_STATE_LABELS[e.state],
                      detail: e.detail,
                      createdAt: this.date(e.createdAt),
                    })),
                  })),
                  catchError(() =>
                    of({ message: 'Impossible de charger la chronologie.', rows: [] }),
                  ),
                ),
              ),
              startWith({ message: 'Chargement de la chronologie…', rows: [] }),
            )
          : of({ message: '', rows: [] }),
      ),
    ),
    { initialValue: { message: 'Chargement de la chronologie…', rows: [] } },
  );
  protected readonly artifactsState = toSignal(
    toObservable(this.selectedExecutionId).pipe(
      switchMap((id) =>
        id
          ? timer(0, 5000).pipe(
              exhaustMap(() =>
                this.tracking.artifacts(id).pipe(
                  map((items) => ({
                    message: '',
                    rows: items.map((item) => ({
                      id: item.sha256,
                      name: item.name,
                      size: formatFileSize(item.bytes),
                    })),
                  })),
                  catchError(() =>
                    of({ message: 'Impossible de charger les fichiers produits.', rows: [] }),
                  ),
                ),
              ),
            )
          : of({ message: '', rows: [] }),
      ),
    ),
    { initialValue: { message: '', rows: [] } },
  );
  protected readonly pipelineState = toSignal(
    toObservable(this.selectedExecutionId).pipe(
      switchMap((id) =>
        id
          ? timer(0, 3000).pipe(
              exhaustMap(() =>
                this.tracking.pipeline(id).pipe(
                  map((steps) => ({ message: '', steps })),
                  catchError(() =>
                    of({ message: 'Impossible de charger les commandes de cette exécution.', steps: [] }),
                  ),
                ),
              ),
              startWith({ message: 'Chargement du pipeline…', steps: [] }),
            )
          : of({ message: '', steps: [] }),
      ),
    ),
    { initialValue: { message: '', steps: [] } },
  );
  protected downloadArtifact(row: DataTableRow): void {
    const id = this.selectedExecutionId();
    if (id)
      this.downloads.fromUrl(
        '/api/v1/executions/' +
          encodeURIComponent(id) +
          '/artifacts/' +
          encodeURIComponent(String(row['id'])),
      );
  }
  protected readonly executions = computed<readonly ExecutionRecord[]>(() =>
    (this.trackingState().data?.executions ?? []).map((e) => ({
      id: e.id,
      media: e.media,
      library: e.library,
      flow: e.flow + ' · v' + e.flowVersion,
      node: e.node ?? '—',
      state: EXECUTION_STATE_LABELS[e.state],
      group:
        e.state === 'queued'
          ? 'queue'
          : e.state === 'running' || e.state === 'cancelling'
            ? 'active'
            : e.state === 'failed'
              ? 'errors'
              : e.state === 'cancelled'
                ? 'cancelled'
                : 'history',
      detail: e.detail || '—',
      queuedAt: this.date(e.queuedAt),
      startedAt: this.date(e.startedAt),
      finishedAt: this.date(e.finishedAt),
      duration: e.startedAt
        ? Math.max(
            0,
            Math.floor(
              (Date.parse(e.finishedAt ?? this.trackingState().data!.observedAt) -
                Date.parse(e.startedAt)) /
                1000,
            ),
          ) + ' s'
        : '—',
      beforeSize: e.beforeMetadata?.format?.size != null
        ? formatFileSize(Number(e.beforeMetadata.format.size))
        : '—',
      afterSize: e.afterMetadata?.format?.size != null
        ? formatFileSize(Number(e.afterMetadata.format.size))
        : '—',
      publishedPath: e.publishedPath ?? '—',
      videoEncodingsJson: JSON.stringify((e.videoEncodings ?? []).map((encoding, index) => ({
        id: `${encoding.flowNodeId}-${encoding.encoderId}-${index}`,
        flowNode: encoding.flowNodeId,
        worker: encoding.workerId,
        codec: encoding.codec.toUpperCase(),
        encoder: encoding.encoderId,
        ffmpeg: encoding.ffmpegVersion,
        acceleration: encoding.acceleration,
        quality: `${encoding.qualityMode} (${encoding.qualityOption}) : ${encoding.quality}`,
        preset: encoding.presetOption && encoding.presetValue
          ? `${encoding.presetOption} : ${encoding.presetValue}`
          : 'Sans preset',
        profile: `${encoding.profileName} · v${encoding.profileVersion}`,
        priority: encoding.priority + 1,
        resolvedAt: this.date(encoding.resolvedAt),
      }))),
      mediaBadges: joinBadges(mediaBadges(e.beforeMetadata)),
      attempt: e.attempt,
      progress: e.progress === null ? '—' : e.progress + ' %',
    })),
  );
  private readonly route = inject(ActivatedRoute);
  protected readonly selectedView = signal<ExecutionMonitorView>(
    VIEWS.find((view) => view === this.route.snapshot.queryParamMap.get('view')) ?? 'queue',
  );
  protected readonly feedback = computed(() =>
    this.trackingState().status === 'loading'
      ? 'Chargement des exécutions…'
      : this.trackingState().status === 'error'
        ? 'Actualisation impossible. Les dernières données reçues peuvent être obsolètes.'
        : '',
  );

  protected readonly queueExecutions = computed(() =>
    this.executions().filter((execution) => execution.group === 'queue'),
  );
  protected readonly activeExecutions = computed(() =>
    this.executions().filter((execution) => execution.group === 'active'),
  );
  protected readonly errorExecutions = computed(() =>
    this.executions().filter((execution) => execution.group === 'errors'),
  );
  protected readonly cancelledExecutions = computed(() =>
    this.executions().filter((execution) => execution.group === 'cancelled'),
  );
  protected readonly historyExecutions = computed(() =>
    this.executions().filter((execution) => execution.group === 'history'),
  );
  protected readonly countText = computed(() => {
    const data = this.trackingState().data;
    return data
      ? data.queuedCount +
          ' à traiter · ' +
          data.activeCount +
          ' en cours · ' +
          data.failedCount +
          ' erreurs'
      : 'Données indisponibles';
  });

  protected readonly queueColumns: readonly DataTableColumn[] = [
    { key: 'media', label: 'Média', sortable: true },
    { key: 'library', label: 'Bibliothèque', sortable: true },
    { key: 'flow', label: 'Flow', sortable: true },
    { key: 'state', label: 'État', sortable: true },
    { key: 'detail', label: 'Disponibilité', sortable: true },
    { key: 'queuedAt', label: 'Ajoutée à la file', sortable: true },
  ];
  protected readonly activeColumns: readonly DataTableColumn[] = [
    { key: 'media', label: 'Média', sortable: true },
    { key: 'mediaBadges', label: 'Caractéristiques', badges: true },
    { key: 'library', label: 'Bibliothèque', sortable: true },
    { key: 'flow', label: 'Flow', sortable: true },
    { key: 'node', label: 'Worker', sortable: true },
    { key: 'detail', label: 'Étape actuelle', sortable: true },
    { key: 'progress', label: 'Progression', sortable: true },
    { key: 'startedAt', label: 'Démarrée', sortable: true },
  ];
  protected readonly errorColumns: readonly DataTableColumn[] = [
    { key: 'media', label: 'Média', sortable: true },
    { key: 'flow', label: 'Flow', sortable: true },
    { key: 'node', label: 'Worker', sortable: true },
    { key: 'detail', label: 'Erreur', sortable: true },
    { key: 'finishedAt', label: 'Échouée le', sortable: true },
  ];
  protected readonly cancelledColumns: readonly DataTableColumn[] = [
    { key: 'media', label: 'Média', sortable: true },
    { key: 'flow', label: 'Flow', sortable: true },
    { key: 'node', label: 'Worker', sortable: true },
    { key: 'detail', label: 'Détail', sortable: true },
    { key: 'finishedAt', label: 'Annulée le', sortable: true },
    { key: 'duration', label: 'Durée', sortable: true },
  ];
  protected readonly historyColumns: readonly DataTableColumn[] = [
    { key: 'state', label: 'État', sortable: true },
    { key: 'media', label: 'Média', sortable: true },
    { key: 'library', label: 'Bibliothèque', sortable: true },
    { key: 'flow', label: 'Flow', sortable: true },
    { key: 'node', label: 'Worker', sortable: true },
    { key: 'finishedAt', label: 'Terminée le', sortable: true },
    { key: 'duration', label: 'Durée', sortable: true },
  ];
  protected readonly errorActions: readonly DataTableRowAction[] = [
    {
      id: 'retry',
      label: 'Relancer',
      visibleWhen: () => !this.busy(),
      confirmation: (row) => ({
        title: 'Relancer cette exécution ?',
        message: `Une nouvelle exécution de « ${row['media']} » sera ajoutée à la file.`,
        confirmLabel: 'Relancer',
      }),
    },
  ];
  protected readonly selectionActions: readonly DataTableSelectionAction[] = [
    {
      id: 'cancel-selected',
      label: 'Annuler les exécutions',
      visibleWhen: (rows) =>
        !this.busy() &&
        rows.every(
          (row) =>
            (row['group'] === 'queue' || row['group'] === 'active') &&
            row['state'] !== EXECUTION_STATE_LABELS.cancelling,
        ),
      confirmation: (rows) => ({
        title: 'Annuler les exécutions sélectionnées ?',
        message: `Les ${rows.length} exécutions recevront une demande d’annulation. Les traitements en cours attendront la confirmation du worker.`,
        confirmLabel: 'Annuler les exécutions',
      }),
    },
    {
      id: 'retry-selected',
      label: 'Relancer les exécutions',
      visibleWhen: (rows) => !this.busy() && rows.every((row) => row['group'] === 'errors'),
      confirmation: (rows) => ({
        title: 'Relancer les exécutions sélectionnées ?',
        message: `Une nouvelle tentative sera ajoutée à la file pour ${rows.length} exécution${rows.length > 1 ? 's' : ''}.`,
        confirmLabel: 'Relancer',
      }),
    },
  ];

  protected onViewChange(view: ExecutionMonitorView): void {
    this.selectedView.set(view);
  }

  protected refresh(): void {
    this.tracking.refresh();
  }
  protected onErrorAction(event: DataTableActionEvent): void {
    if (event.actionId === 'retry') this.perform('retry', [String(event.row['id'])]);
  }
  protected onSelectionAction(event: DataTableSelectionActionEvent): void {
    if (event.actionId === 'cancel-selected')
      this.perform(
        'cancel',
        event.rows.map((r) => String(r['id'])),
      );
    if (event.actionId === 'retry-selected')
      this.perform(
        'retry',
        event.rows.map((r) => String(r['id'])),
      );
  }
  private perform(action: 'cancel' | 'retry', ids: readonly string[]): void {
    if (this.busy() || !ids.length) return;
    this.busy.set(true);
    forkJoin(
      ids.map((id) =>
        (action === 'cancel'
          ? this.tracking.cancel(id).pipe(map(() => true))
          : this.tracking.retry(id).pipe(map(() => true))
        ).pipe(catchError(() => of(false))),
      ),
    )
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((results) => {
        this.busy.set(false);
        this.tracking.refresh();
        const success = results.filter(Boolean).length;
        this.notifications.show(
          success === ids.length
            ? action === 'cancel'
              ? 'Demandes d’annulation enregistrées.'
              : 'Relances ajoutées à la file.'
            : success +
                ' sur ' +
                ids.length +
                ' demandes enregistrées. Les autres ont échoué ou leur état a changé.',
        );
      });
  }
  private date(value: string | null): string {
    return value ? new Date(value).toLocaleString('fr-FR') : '—';
  }
}
