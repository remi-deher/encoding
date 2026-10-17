import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  computed,
  contentChild,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
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
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DataTableComponent,
    DetailDrawerComponent,
    DetailFieldListComponent,
    MatFormFieldModule,
    MatInputModule,
    MatTabsModule,
    PageHeaderComponent,
    ExecutionPipelineLogsComponent,
  ],
  template: `
    <app-page-header
      [title]="title()"
      [description]="sectionDescription()"
      [actions]="pageActions()"
    >
      <span page-metadata class="execution-count-pill">{{ countText() }}</span>
    </app-page-header>

    <section class="monitor-panel" [attr.aria-label]="title()">
      @if (feedback()) {
        <p class="execution-feedback" role="status">{{ feedback() }}</p>
      }

      <div class="monitor-toolbar">
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="monitor-search-field">
          <mat-label>Rechercher un média, une bibliothèque, un flow ou un worker</mat-label>
          <input matInput type="search" [value]="searchQuery()" (input)="onSearchInput($event)" />
        </mat-form-field>
      </div>

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
              [rows]="filteredQueueRows()"
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
              [rows]="filteredActiveRows()"
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
              [rows]="filteredErrorRows()"
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
              [rows]="filteredCancelledRows()"
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
              [rows]="filteredHistoryRows()"
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
      [modal]="true"
      [title]="detailTitle()"
      [description]="detailDescription()"
      [content]="executionDetailTemplate()"
      (closed)="selectedId.set(null); executionOpened.emit(null)"
    >
      <ng-template #executionDetail>
        @if (selectedExecution(); as exec) {
          <div class="execution-hero-comparison" aria-label="Comparaison média et worker">
            <div class="comparison-card">
              <span class="comparison-label">Taille source</span>
              <strong class="comparison-value">{{ exec['beforeSize'] || '—' }}</strong>
            </div>
            <div class="comparison-arrow" aria-hidden="true">→</div>
            <div class="comparison-card">
              <span class="comparison-label">Taille finale</span>
              <strong class="comparison-value">{{ exec['afterSize'] || '—' }}</strong>
            </div>
            <div class="comparison-card">
              <span class="comparison-label">Worker & Progression</span>
              <strong class="comparison-value">
                {{ exec['node'] || '—' }} · {{ exec['progress'] || '—' }}
              </strong>
            </div>
          </div>
        }

        <mat-tab-group
          class="execution-detail-tabs"
          aria-label="Informations de l’exécution"
          [selectedIndex]="detailTabIndex()"
          (selectedIndexChange)="detailTabChange.emit($event)"
        >
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
    .execution-count-pill {
      display: inline-flex;
      align-items: center;
      margin-top: var(--space-2);
      padding: 4px 12px;
      border-radius: var(--radius-pill);
      background: var(--surface-container);
      color: var(--text-secondary);
      font-size: var(--font-label);
      font-weight: 700;
      font-variant-numeric: tabular-nums;
    }

    .monitor-panel {
      overflow: hidden;
      border-radius: var(--radius-2xl);
      background: var(--surface-container-low);
    }

    .monitor-toolbar {
      padding: var(--space-4) var(--space-4) var(--space-2);
      display: flex;
      justify-content: flex-end;
    }

    .monitor-search-field {
      width: min(380px, 100%);
    }

    .tab-count {
      margin-left: 6px;
      padding: 2px 7px;
      border-radius: var(--radius-pill);
      background: var(--surface-container);
      color: var(--text-secondary);
      font-size: var(--font-label);
      font-weight: 700;
      font-variant-numeric: tabular-nums;
    }

    .execution-feedback {
      margin: var(--space-3) var(--space-4) 0;
      padding: 10px var(--space-3);
      border-radius: var(--radius-md);
      background: var(--accent-soft);
      color: var(--accent-strong);
      font-size: var(--font-label);
      line-height: 1.5;
    }

    .tab-content {
      padding-top: 12px;
    }

    .execution-hero-comparison {
      margin-bottom: var(--space-4);
      padding: var(--space-3);
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-2);
      border-radius: var(--radius-xl);
      background: var(--surface-container);
    }

    .comparison-card {
      flex: 1 1 130px;
      min-width: 0;
      padding: var(--space-3);
      border-radius: var(--radius-lg);
      background: var(--surface-container-lowest);
      display: flex;
      flex-direction: column;
      gap: 4px;
    }

    .comparison-label {
      color: var(--text-muted);
      font-size: var(--font-label);
      font-weight: 650;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }

    .comparison-value {
      color: var(--text-primary);
      font-size: var(--font-title-sm);
      font-weight: 760;
      font-variant-numeric: tabular-nums;
      overflow-wrap: anywhere;
    }

    .comparison-arrow {
      color: var(--accent-strong);
      font-weight: 800;
      padding-inline: 2px;
    }

    .execution-detail-tabs {
      display: block;
    }
    .execution-detail-tab {
      display: grid;
      gap: 18px;
      padding: 18px 2px var(--space-1);
    }
    .execution-detail-tab h3 {
      margin: 0;
      color: var(--text-primary);
      font-size: var(--font-body);
    }
    .execution-detail-empty {
      margin: 0;
      color: var(--text-secondary);
      font-size: var(--font-body);
    }

    .monitor-note {
      margin: 14px 2px 0;
      color: var(--text-muted);
      font-size: var(--font-body);
      line-height: 1.5;
    }

    @media (max-width: 700px) {
      :host ::ng-deep .execution-tabs .mat-mdc-tab-labels {
        min-width: max-content;
      }

      :host ::ng-deep .execution-tabs .mat-mdc-tab {
        min-width: 112px;
      }

      .comparison-arrow {
        display: none;
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
    { id: 'download', label: 'Télécharger' },
  ];
  readonly note = input('');
  protected readonly pageActions = contentChild('pageActions', { read: TemplateRef });
  readonly viewChange = output<ExecutionMonitorView>();
  readonly errorAction = output<DataTableActionEvent>();
  readonly selectionAction = output<DataTableSelectionActionEvent>();

  protected readonly searchQuery = signal('');
  protected readonly filteredQueueRows = computed(() =>
    this.filterRows(this.queueRows(), this.searchQuery()),
  );
  protected readonly filteredActiveRows = computed(() =>
    this.filterRows(this.activeRows(), this.searchQuery()),
  );
  protected readonly filteredErrorRows = computed(() =>
    this.filterRows(this.errorRows(), this.searchQuery()),
  );
  protected readonly filteredCancelledRows = computed(() =>
    this.filterRows(this.cancelledRows(), this.searchQuery()),
  );
  protected readonly filteredHistoryRows = computed(() =>
    this.filterRows(this.historyRows(), this.searchQuery()),
  );

  protected onSearchInput(event: Event): void {
    this.searchQuery.set((event.target as HTMLInputElement).value);
  }

  private filterRows(rows: readonly DataTableRow[], rawQuery: string): readonly DataTableRow[] {
    const query = rawQuery.trim().toLocaleLowerCase('fr');
    if (!query) return rows;
    return rows.filter((row) =>
      [row['media'], row['library'], row['flow'], row['node'], row['detail'], row['state']]
        .filter(Boolean)
        .some((val) => String(val).toLocaleLowerCase('fr').includes(query)),
    );
  }

  protected readonly selectedIndex = computed(() =>
    EXECUTION_MONITOR_VIEWS.indexOf(this.selectedView()),
  );
  protected readonly selectedId = signal<string | null>(null);
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
  readonly detailTabIndex = input(0);
  readonly detailTabChange = output<number>();
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
      return Array.isArray(rows) ? (rows as DataTableRow[]) : [];
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
    this.detailTabChange.emit(0);
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
