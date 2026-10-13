import { SelectionModel } from '@angular/cdk/collections';
import { Component, computed, effect, inject, input, output, signal, ViewChild } from '@angular/core';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatSort, MatSortModule } from '@angular/material/sort';
import { MatTableDataSource, MatTableModule } from '@angular/material/table';
import { ConfirmationDialogComponent } from './confirmation-dialog.component';

export type DataTableValue = string | number;
export type DataTableRow = Readonly<Record<string, DataTableValue>>;

export interface DataTableColumn {
  readonly key: string;
  readonly label: string;
  readonly sortable?: boolean;
  readonly sortValueKey?: string;
}

export interface DataTableRowAction {
  readonly id: string;
  readonly label: string;
  readonly visibleWhen?: (row: DataTableRow) => boolean;
  readonly confirmation?: (row: DataTableRow) => {
    readonly title: string;
    readonly message: string;
    readonly confirmLabel?: string;
  };
}

export interface DataTableActionEvent {
  readonly actionId: string;
  readonly row: DataTableRow;
}

export interface DataTableSelectionAction {
  readonly id: string;
  readonly label: string;
  readonly visibleWhen?: (rows: readonly DataTableRow[]) => boolean;
  readonly confirmation?: (rows: readonly DataTableRow[]) => {
    readonly title: string;
    readonly message: string;
    readonly confirmLabel?: string;
  };
}

export interface DataTableSelectionActionEvent {
  readonly actionId: string;
  readonly rows: readonly DataTableRow[];
}

@Component({
  selector: 'app-data-table',
  imports: [MatCheckboxModule, MatDialogModule, MatSortModule, MatTableModule],
  template: `
    <div class="table-scroll-region">
      @if (selectable()) {
        <button class="mobile-select-all" type="button" [attr.aria-label]="isAllSelected() ? 'Désélectionner les lignes affichées' : 'Sélectionner toutes les lignes affichées'" (click)="toggleAll()">
          {{ isAllSelected() ? 'Désélectionner les lignes affichées' : 'Tout sélectionner' }}
        </button>
      }

      @if (selectable() && selectedRows().length) {
        <div class="selection-toolbar" role="toolbar" aria-label="Actions sur la sélection">
          <span class="selection-count" aria-live="polite">
            {{ selectedRows().length }} {{ selectedRows().length === 1 ? 'élément sélectionné' : 'éléments sélectionnés' }}
          </span>
          <div class="selection-actions">
            @if (rowOpenable() && selectedRows().length === 1) {
              <button class="selection-action" type="button" (click)="openSelectedRow()">
                {{ rowOpenLabel() }}
              </button>
            }
            @for (action of selectionActions(); track action.id) {
              @if (!action.visibleWhen || action.visibleWhen(selectedRows())) {
                <button class="selection-action" type="button" (click)="requestSelectionAction(action)">
                  {{ action.label }}
                </button>
              }
            }
            <button class="selection-clear" type="button" (click)="clearSelection()">Tout désélectionner</button>
          </div>
        </div>
      }

      <table mat-table matSort [dataSource]="dataSource" class="data-table">
        @if (selectable()) {
          <ng-container matColumnDef="select">
            <th mat-header-cell *matHeaderCellDef class="selection-cell">
              <mat-checkbox
                [checked]="isAllSelected()"
                [indeterminate]="selection.hasValue() && !isAllSelected()"
                aria-label="Sélectionner toutes les lignes"
                (change)="toggleAll()"
              />
            </th>
            <td mat-cell *matCellDef="let row" class="selection-cell" data-label="Sélection">
              <mat-checkbox
                [checked]="selection.isSelected(row)"
                aria-label="Sélectionner cette ligne"
                (click)="captureCheckboxClick($event)"
                (change)="toggleRow(row)"
              />
            </td>
          </ng-container>
        }

        @for (column of columns(); track column.key; let isFirst = $first) {
          <ng-container [matColumnDef]="column.key">
            <th
              mat-header-cell
              *matHeaderCellDef
              [mat-sort-header]="column.key"
              [disabled]="!column.sortable"
            >
              {{ column.label }}
            </th>
            <td mat-cell *matCellDef="let row" [attr.data-label]="column.label">
              @if (rowOpenable() && isFirst) {
                <button
                  class="row-open"
                  type="button"
                  [attr.aria-label]="rowOpenLabel() + ' ' + row[column.key]"
                  (click)="openRow(row, $event)"
                >
                  {{ row[column.key] }}
                </button>
              } @else {
                {{ row[column.key] }}
              }
            </td>
          </ng-container>
        }

        @if (rowActions().length > 0) {
          <ng-container matColumnDef="row-actions">
            <th mat-header-cell *matHeaderCellDef>Actions</th>
            <td mat-cell *matCellDef="let row" class="row-actions-cell" data-label="Actions">
              @for (action of rowActions(); track action.id) {
                @if (!action.visibleWhen || action.visibleWhen(row)) {
                  <button
                    class="row-action"
                    type="button"
                    [attr.aria-label]="action.label + ' : ' + row['media']"
                    (click)="requestRowAction(action, row)"
                  >
                    {{ action.label }}
                  </button>
                }
              }
            </td>
          </ng-container>
        }

        <tr mat-header-row *matHeaderRowDef="displayedColumnKeys()"></tr>
        <tr mat-row *matRowDef="let row; columns: displayedColumnKeys()" [attr.aria-selected]="selectable() ? selection.isSelected(row) : null"></tr>
        <tr class="mat-row empty-row" *matNoDataRow>
          <td class="mat-cell" [attr.colspan]="displayedColumnKeys().length">
            {{ emptyMessage() }}
          </td>
        </tr>
      </table>
    </div>
  `,
  styleUrl: './data-table.component.scss',
})
export class DataTableComponent {
  private readonly dialog = inject(MatDialog);
  readonly columns = input.required<readonly DataTableColumn[]>();
  readonly rows = input.required<readonly DataTableRow[]>();
  readonly emptyMessage = input('Aucun résultat.');
  readonly selectable = input(false);
  readonly rowOpenable = input(false);
  readonly rowOpenLabel = input('Ouvrir');
  readonly rowActions = input<readonly DataTableRowAction[]>([]);
  readonly selectionActions = input<readonly DataTableSelectionAction[]>([]);
  readonly selectionChange = output<readonly DataTableRow[]>();
  readonly rowAction = output<DataTableActionEvent>();
  readonly rowOpen = output<DataTableRow>();
  readonly selectionAction = output<DataTableSelectionActionEvent>();

  protected readonly dataSource = new MatTableDataSource<DataTableRow>();
  protected readonly selection = new SelectionModel<DataTableRow>(true, []);
  protected readonly selectedRows = signal<readonly DataTableRow[]>([]);
  protected readonly displayedColumnKeys = computed(() => [
    ...(this.selectable() ? ['select'] : []),
    ...this.columns().map((column) => column.key),
    ...(this.rowActions().length > 0 ? ['row-actions'] : []),
  ]);

  @ViewChild(MatSort)
  private set sort(sort: MatSort | undefined) {
    if (sort) {
      this.dataSource.sort = sort;
    }
  }

  constructor() {
    effect(() => {
      this.dataSource.data = [...this.rows()];
    });

    this.dataSource.sortingDataAccessor = (row, property) => {
      const sortValueKey = this.columns().find((column) => column.key === property)?.sortValueKey;
      return row[sortValueKey ?? property] ?? '';
    };
  }

  protected requestRowAction(action: DataTableRowAction, row: DataTableRow): void {
    const confirmation = action.confirmation?.(row);
    if (!confirmation) {
      this.rowAction.emit({ actionId: action.id, row });
      return;
    }

    this.dialog
      .open(ConfirmationDialogComponent, { data: confirmation })
      .afterClosed()
      .subscribe((confirmed: boolean | undefined) => {
        if (confirmed) {
          this.rowAction.emit({ actionId: action.id, row });
        }
      });
  }

  protected openRow(row: DataTableRow, event: MouseEvent): void {
    if (this.hasSelectionModifier(event)) {
      event.preventDefault();
      event.stopPropagation();
      this.selectFromRow(row, event);
      return;
    }
    this.rowOpen.emit(row);
  }

  protected clearSelection(): void {
    this.selection.clear();
    this.selectionAnchor = undefined;
    this.emitSelectionChange();
  }

  protected openSelectedRow(): void {
    const [row] = this.selection.selected;
    if (!row) return;
    this.rowOpen.emit(row);
    this.clearSelection();
  }

  protected requestSelectionAction(action: DataTableSelectionAction): void {
    const rows = [...this.selection.selected];
    if (!rows.length) return;
    const confirmation = action.confirmation?.(rows);
    if (!confirmation) {
      this.emitSelectionAction(action.id, rows);
      return;
    }

    this.dialog
      .open(ConfirmationDialogComponent, { data: confirmation })
      .afterClosed()
      .subscribe((confirmed: boolean | undefined) => {
        if (confirmed) this.emitSelectionAction(action.id, rows);
      });
  }

  protected isAllSelected(): boolean {
    const rows = this.dataSource.filteredData;
    return rows.length > 0 && rows.every((row) => this.selection.isSelected(row));
  }

  protected toggleAll(): void {
    const rows = this.dataSource.filteredData;
    if (this.isAllSelected()) {
      rows.forEach((row) => this.selection.deselect(row));
    } else {
      rows.forEach((row) => this.selection.select(row));
    }
    this.emitSelectionChange();
  }

  protected toggleRow(row: DataTableRow): void {
    const clickEvent = this.pendingCheckboxClick;
    this.pendingCheckboxClick = undefined;
    if (clickEvent && (clickEvent.shiftKey || clickEvent.ctrlKey || clickEvent.metaKey)) {
      this.selectFromRow(row, clickEvent);
      return;
    }
    this.selection.toggle(row);
    this.selectionAnchor = row;
    this.emitSelectionChange();
  }

  private selectionAnchor: DataTableRow | undefined;
  private pendingCheckboxClick: MouseEvent | undefined;

  protected captureCheckboxClick(event: MouseEvent): void {
    event.stopPropagation();
    this.pendingCheckboxClick = event;
  }

  private selectFromRow(row: DataTableRow, event: MouseEvent): void {
    if (event.shiftKey && this.selectionAnchor) {
      const rows = this.dataSource.sort
        ? this.dataSource.sortData(this.dataSource.filteredData, this.dataSource.sort)
        : this.dataSource.filteredData;
      const anchorIndex = rows.indexOf(this.selectionAnchor);
      const targetIndex = rows.indexOf(row);
      if (anchorIndex >= 0 && targetIndex >= 0) {
        if (!event.ctrlKey && !event.metaKey) this.selection.clear();
        this.selection.select(...rows.slice(Math.min(anchorIndex, targetIndex), Math.max(anchorIndex, targetIndex) + 1));
        this.emitSelectionChange();
        return;
      }
    }

    if (event.ctrlKey || event.metaKey) {
      this.selection.toggle(row);
    } else {
      this.selection.clear();
      this.selection.select(row);
    }
    this.selectionAnchor = row;
    this.emitSelectionChange();
  }

  private hasSelectionModifier(event: MouseEvent): boolean {
    return this.selectable() && this.selectedRows().length > 0 && (event.ctrlKey || event.metaKey || event.shiftKey);
  }

  private emitSelectionChange(): void {
    const selected = [...this.selection.selected];
    this.selectedRows.set(selected);
    if (!selected.length) this.selectionAnchor = undefined;
    this.selectionChange.emit(selected);
  }

  private emitSelectionAction(actionId: string, rows: readonly DataTableRow[]): void {
    this.selectionAction.emit({ actionId, rows });
    this.clearSelection();
  }
}
