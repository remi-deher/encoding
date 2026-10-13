import { Component, computed, input, output } from '@angular/core';
import { MatTabsModule } from '@angular/material/tabs';
import {
  DataTableActionEvent,
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
  DataTableRowAction,
} from '../ui/data-table.component';
import { PageHeaderComponent } from '../ui/page-header.component';

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
  imports: [DataTableComponent, MatTabsModule, PageHeaderComponent],
  template: `
    <app-page-header [title]="title()" [description]="description()" />

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
              emptyMessage="Aucune exécution en erreur."
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
              emptyMessage="Aucune exécution réussie."
            />
          </div>
        </mat-tab>
      </mat-tab-group>
    </section>

    @if (note()) {
      <p class="monitor-note" role="note">{{ note() }}</p>
    }
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
      font-size: 11px;
    }

    .execution-count,
    .tab-count {
      color: var(--text-muted);
      font-size: 11px;
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
      font-size: 11px;
      line-height: 1.5;
    }

    .tab-content {
      padding-top: 14px;
    }

    .monitor-note {
      margin: 14px 2px 0;
      color: var(--text-muted);
      font-size: 11px;
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
  readonly description = input('');
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
  readonly cancelledColumns = input.required<readonly DataTableColumn[]>();
  readonly cancelledRows = input.required<readonly DataTableRow[]>();
  readonly historyColumns = input.required<readonly DataTableColumn[]>();
  readonly historyRows = input.required<readonly DataTableRow[]>();
  readonly feedback = input('');
  readonly note = input('');
  readonly viewChange = output<ExecutionMonitorView>();
  readonly errorAction = output<DataTableActionEvent>();

  protected readonly selectedIndex = computed(() =>
    EXECUTION_MONITOR_VIEWS.indexOf(this.selectedView()),
  );

  protected onSelectedIndexChange(index: number): void {
    const view = EXECUTION_MONITOR_VIEWS[index];
    if (view) {
      this.viewChange.emit(view);
    }
  }
}
