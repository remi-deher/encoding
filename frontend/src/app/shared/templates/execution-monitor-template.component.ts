import {
  Component,
  TemplateRef,
  computed,
  contentChild,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { MatTabsModule } from '@angular/material/tabs';
import { DetailDrawerComponent } from '../ui/detail-drawer/detail-drawer';
import {
  DataTableActionEvent,
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
  DataTableRowAction,
  DataTableSelectionAction,
  DataTableSelectionActionEvent,
} from '../ui/data-table.component';
import { DetailField, DetailFieldListComponent } from '../ui/detail-field-list.component';
import { PageHeaderComponent } from '../ui/page-header.component';
import { ExecutionPipelineLogsComponent } from '../ui/execution-pipeline-logs.component';
import { ExecutionPipelineStep } from '../models/execution';

export type ExecutionMonitorView = 'queue' | 'active' | 'errors' | 'cancelled' | 'history';

const EXECUTION_MONITOR_VIEWS: readonly ExecutionMonitorView[] = [
  'queue',
  'active',
  'errors',
  'cancelled',
  'history',
];

@Component({
  selector: 'app-execution-monitor-template',
  imports: [
    DataTableComponent,
    DetailDrawerComponent,
    DetailFieldListComponent,
    MatTabsModule,
    PageHeaderComponent,
    ExecutionPipelineLogsComponent,
  ],
  template: `
    <app-page-header [title]="title()" [actions]="pageActions()" />

    <section class="monitor-panel" aria-labelledby="execution-panel-heading">
      <div class="monitor-panel-heading">
        <div>
          <h2 id="execution-panel-heading">Suivi des exécutions</h2>
          <p>{{ sectionDescription() }}</p>
        </div>
        <span class="execution-count">{{ countText() }}</span>
      </div>

      @if (feedback()) {
        <p class="execution-feedback" role="status">{{ feedback() }}</p>
      }

      <mat-tab-group
        class="execution-tabs"
        [selectedIndex]="selectedIndex()"
        (selectedIndexChange)="onSelectedIndexChange($event)"
      >
        <mat-tab>
          <ng-template mat-tab-label>
            À traiter <span class="tab-count">{{ queueCount() }}</span>
          </ng-template>
          <div class="tab-content">
            <app-data-table
              [columns]="queueColumns()"
              [rows]="queueRows()"
              [selectable]="true"
              [selectionActions]="selectionActions()"
              [rowOpenable]="true"
              rowOpenLabel="Voir"
              (rowOpen)="openExecution($event)"
              (selectionAction)="selectionAction.emit($event)"
              emptyMessage="La file d’attente est vide."
            />
          </div>
        </mat-tab>

        <mat-tab>
          <ng-template mat-tab-label>
            En cours <span class="tab-count">{{ activeCount() }}</span>
          </ng-template>
          <div class="tab-content">
            <app-data-table
              [columns]="activeColumns()"
              [rows]="activeRows()"
              [selectable]="true"
              [selectionActions]="selectionActions()"
              [rowOpenable]="true"
              rowOpenLabel="Voir"
              (rowOpen)="openExecution($event)"
              (selectionAction)="selectionAction.emit($event)"
              emptyMessage="Aucun flow n’est actuellement traité par un worker."
            />
          </div>
        </mat-tab>

        <mat-tab>
          <ng-template mat-tab-label>
            Erreurs <span class="tab-count">{{ errorCount() }}</span>
          </ng-template>
          <div class="tab-content">
            <app-data-table
              [columns]="errorColumns()"
              [rows]="errorRows()"
              [rowActions]="errorActions()"
              [selectable]="true"
              [selectionActions]="selectionActions()"
              [rowOpenable]="true"
              rowOpenLabel="Voir"
              emptyMessage="Aucune exécution en erreur."
              (rowOpen)="openExecution($event)"
              (selectionAction)="selectionAction.emit($event)"
              (rowAction)="errorAction.emit($event)"
            />
          </div>
        </mat-tab>

        <mat-tab>
          <ng-template mat-tab-label>
            Annulés <span class="tab-count">{{ cancelledCount() }}</span>
          </ng-template>
          <div class="tab-content">
            <app-data-table
              [columns]="cancelledColumns()"
              [rows]="cancelledRows()"
              [selectable]="true"
              [selectionActions]="selectionActions()"
              [rowOpenable]="true"
              rowOpenLabel="Voir"
              (rowOpen)="openExecution($event)"
              (selectionAction)="selectionAction.emit($event)"
              emptyMessage="Aucune exécution annulée."
            />
          </div>
        </mat-tab>

        <mat-tab>
          <ng-template mat-tab-label>
            Historique <span class="tab-count">{{ historyCount() }}</span>
          </ng-template>
          <div class="tab-content">
            <app-data-table
              [columns]="historyColumns()"
              [rows]="historyRows()"
              [selectable]="true"
              [selectionActions]="selectionActions()"
              [rowOpenable]="true"
              rowOpenLabel="Voir"
              (rowOpen)="openExecution($event)"
              (selectionAction)="selectionAction.emit($event)"
              emptyMessage="Aucune exécution réussie."
            />
          </div>
        </mat-tab>
      </mat-tab-group>
    </section>

    @if (note()) {
      <p class="monitor-note" role="note">{{ note() }}</p>
    }

    <app-detail-drawer
      [opened]="selectedExecution() !== null"
      [title]="detailTitle()"
      [description]="detailDescription()"
      [content]="executionDetailTemplate()"
      (closed)="selectedId.set(null); executionOpened.emit(null)"
    >
      <ng-template #executionDetail>
        <mat-tab-group class="execution-detail-tabs" aria-label="Informations de l’exécution">
          <mat-tab label="Synthèse">
            <div class="execution-detail-tab">
              <app-detail-field-list [fields]="executionDetailFields()" />
            </div>
          </mat-tab>
          <mat-tab label="Pipeline">
            <div class="execution-detail-tab">
              <app-execution-pipeline-logs
                [executionId]="pipelineExecutionId()"
                [steps]="pipelineSteps()"
                [feedback]="pipelineFeedback()"
              />
            </div>
          </mat-tab>
          <mat-tab label="Événements">
            <div class="execution-detail-tab">
              <h3>Historique de l’exécution</h3>
              @if (eventFeedback()) {
                <p role="status">{{ eventFeedback() }}</p>
              }
              <app-data-table
                [columns]="eventColumns"
                [rows]="eventRows()"
                emptyMessage="Aucun événement enregistré."
              />
            </div>
          </mat-tab>
          <mat-tab label="Résultats">
            <div class="execution-detail-tab">
              @if (artifactFeedback()) {
                <p role="status">{{ artifactFeedback() }}</p>
              }
              @if (artifactRows().length) {
                <h3>Fichiers produits</h3>
                <app-data-table
                  [columns]="artifactColumns"
                  [rows]="artifactRows()"
                  [rowActions]="artifactActions"
                  (rowAction)="artifactDownload.emit($event.row)"
                  emptyMessage="Aucun fichier produit."
                />
              }
              @if (videoEncodingRows().length) {
                <h3>Configuration vidéo retenue</h3>
                <app-data-table
                  [columns]="videoEncodingColumns"
                  [rows]="videoEncodingRows()"
                  emptyMessage="Aucun encodeur vidéo résolu pour cette exécution."
                />
              }
              @if (!artifactRows().length && !artifactFeedback() && !videoEncodingRows().length) {
                <p class="execution-detail-empty">Aucun fichier ou réglage vidéo enregistré.</p>
              }
            </div>
          </mat-tab>
        </mat-tab-group>
      </ng-template>
    </app-detail-drawer>
  `,
  styles: `
    .monitor-panel {
      overflow: hidden;
      border: 1px solid var(--border-subtle);
      border-radius: 12px;
      background: var(--surface);
      box-shadow: 0 2px 8px rgb(24 40 33 / 3%);
    }

    .monitor-panel-heading {
      min-height: 76px;
      padding: 18px 22px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 18px;
      border-bottom: 1px solid var(--border-subtle);
    }

    .monitor-panel-heading h2 {
      margin: 0;
      color: var(--text-primary);
      font-size: 14px;
      font-weight: 700;
    }

    .monitor-panel-heading p {
      margin: 5px 0 0;
      color: var(--text-secondary);
      font-size: 14px;
    }

    .execution-count,
    .tab-count {
      color: var(--text-muted);
      font-size: 14px;
      font-variant-numeric: tabular-nums;
    }

    .execution-count {
      flex: 0 0 auto;
    }

    .tab-count {
      margin-left: 6px;
    }

    .execution-feedback {
      margin: 14px 20px 0;
      padding: 10px 12px;
      border: 1px solid var(--accent-soft);
      border-radius: 8px;
      background: var(--accent-soft);
      color: var(--accent-strong);
      font-size: 14px;
      line-height: 1.5;
    }

    .tab-content {
      padding-top: 14px;
    }

    .execution-detail-tabs { display: block; }
    .execution-detail-tab { display: grid; gap: 18px; padding: 18px 2px 4px; }
    .execution-detail-tab h3 { margin: 0; color: var(--text-primary); font-size: 14px; }
    .execution-detail-empty { margin: 0; color: var(--text-secondary); font-size: 14px; }

    .monitor-note {
      margin: 14px 2px 0;
      color: var(--text-muted);
      font-size: 14px;
      line-height: 1.5;
    }

    @media (max-width: 700px) {
      :host ::ng-deep .execution-tabs .mat-mdc-tab-labels {
        min-width: max-content;
      }

      :host ::ng-deep .execution-tabs .mat-mdc-tab {
        min-width: 112px;
      }

      .monitor-panel-heading {
        align-items: flex-start;
        padding: 16px;
      }
    }
  `,
})
export class ExecutionMonitorTemplateComponent {
  readonly title = input.required<string>();
  readonly sectionDescription = input('');
  readonly countText = input.required<string>();
  readonly queueCount = input.required<number>();
  readonly activeCount = input.required<number>();
  readonly errorCount = input.required<number>();
  readonly cancelledCount = input.required<number>();
  readonly historyCount = input.required<number>();
  readonly selectedView = input.required<ExecutionMonitorView>();
  readonly queueColumns = input.required<readonly DataTableColumn[]>();
  readonly queueRows = input.required<readonly DataTableRow[]>();
  readonly activeColumns = input.required<readonly DataTableColumn[]>();
  readonly activeRows = input.required<readonly DataTableRow[]>();
  readonly errorColumns = input.required<readonly DataTableColumn[]>();
  readonly errorRows = input.required<readonly DataTableRow[]>();
  readonly errorActions = input<readonly DataTableRowAction[]>([]);
  readonly selectionActions = input<readonly DataTableSelectionAction[]>([]);
  readonly cancelledColumns = input.required<readonly DataTableColumn[]>();
  readonly cancelledRows = input.required<readonly DataTableRow[]>();
  readonly historyColumns = input.required<readonly DataTableColumn[]>();
  readonly historyRows = input.required<readonly DataTableRow[]>();
  readonly feedback = input('');
  readonly artifactRows = input<readonly DataTableRow[]>([]);
  readonly artifactFeedback = input('');
  readonly artifactDownload = output<DataTableRow>();
  protected readonly artifactColumns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Fichier' },
    { key: 'size', label: 'Taille' },
  ];
  protected readonly artifactActions: readonly DataTableRowAction[] = [
    { id: 'download', label: 'T�l�charger' },
  ];
  readonly note = input('');
  protected readonly pageActions = contentChild('pageActions', { read: TemplateRef });
  readonly viewChange = output<ExecutionMonitorView>();
  readonly errorAction = output<DataTableActionEvent>();
  readonly selectionAction = output<DataTableSelectionActionEvent>();

  protected readonly selectedIndex = computed(() =>
    EXECUTION_MONITOR_VIEWS.indexOf(this.selectedView()),
  );
  private readonly selectedId = signal<string | null>(null);
  protected readonly selectedExecution = computed(
    () =>
      [
        ...this.queueRows(),
        ...this.activeRows(),
        ...this.errorRows(),
        ...this.cancelledRows(),
        ...this.historyRows(),
      ].find((row) => row['id'] === this.selectedId()) ?? null,
  );
  readonly executionOpened = output<string | null>();
  readonly eventRows = input<readonly DataTableRow[]>([]);
  readonly eventFeedback = input('');
  readonly pipelineSteps = input<readonly ExecutionPipelineStep[]>([]);
  readonly pipelineFeedback = input('');
  readonly pipelineExecutionId = input('execution');
  protected readonly videoEncodingRows = computed(() => {
    const serialized = this.selectedExecution()?.['videoEncodingsJson'];
    if (typeof serialized !== 'string') return [];
    try {
      const rows: unknown = JSON.parse(serialized);
      return Array.isArray(rows) ? rows as DataTableRow[] : [];
    } catch {
      return [];
    }
  });
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
  protected readonly eventColumns: readonly DataTableColumn[] = [
    { key: 'revision', label: 'Ordre' },
    { key: 'createdAt', label: 'Date' },
    { key: 'state', label: 'État' },
    { key: 'detail', label: 'Détail' },
  ];
  protected readonly executionDetailTemplate = viewChild<TemplateRef<unknown>>('executionDetail');
  protected readonly detailTitle = computed(() =>
    String(this.selectedExecution()?.['media'] ?? 'Exécution'),
  );
  protected readonly detailDescription = computed(() => {
    const execution = this.selectedExecution();
    return (
      [execution?.['state'], execution?.['flow']].filter(Boolean).join(' · ') ||
      'Détails de l’exécution'
    );
  });
  protected readonly executionDetailFields = computed<readonly DetailField[]>(() => {
    const execution = this.selectedExecution();
    return execution
      ? [
          { section: 'Exécution', label: 'Identifiant', value: execution['id'] ?? '—' },
          { section: 'Exécution', label: 'État', value: execution['state'] ?? '—' },
          { section: 'Exécution', label: 'Progression', value: execution['progress'] ?? '—' },
          { section: 'Média', label: 'Taille avant', value: execution['beforeSize'] ?? '—' },
          { section: 'Média', label: 'Taille après', value: execution['afterSize'] ?? '—' },
          { section: 'Média', label: 'Résultat publié', value: execution['publishedPath'] ?? '—' },
          { section: 'Exécution', label: 'Étape ou résultat', value: execution['detail'] ?? '—' },
          {
            section: 'Source et traitement',
            label: 'Bibliothèque',
            value: execution['library'] ?? '—',
          },
          { section: 'Source et traitement', label: 'Média', value: execution['media'] ?? '—' },
          { section: 'Source et traitement', label: 'Flow', value: execution['flow'] ?? '—' },
          { section: 'Source et traitement', label: 'Worker', value: execution['node'] ?? '—' },
          {
            section: 'Chronologie',
            label: 'Ajoutée à la file',
            value: execution['queuedAt'] ?? '—',
          },
          { section: 'Chronologie', label: 'Démarrée', value: execution['startedAt'] ?? '—' },
          { section: 'Chronologie', label: 'Terminée', value: execution['finishedAt'] ?? '—' },
          { section: 'Chronologie', label: 'Durée', value: execution['duration'] ?? '—' },
          { section: 'Chronologie', label: 'Tentative', value: execution['attempt'] ?? '—' },
        ]
      : [];
  });

  protected openExecution(execution: DataTableRow): void {
    this.selectedId.set(String(execution['id']));
    this.executionOpened.emit(String(execution['id']));
  }

  protected onSelectedIndexChange(index: number): void {
    const view = EXECUTION_MONITOR_VIEWS[index];
    if (view) {
      this.viewChange.emit(view);
    }
  }
}
