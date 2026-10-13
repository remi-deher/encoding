import { SelectionModel } from '@angular/cdk/collections';
import { Component, computed, effect, inject, input, output, ViewChild } from '@angular/core';
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

@Component({
  selector: 'app-data-table',
  imports: [MatCheckboxModule, MatDialogModule, MatSortModule, MatTableModule],
  template: `
    <div class="table-scroll-region">
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
                (change)="toggleRow(row)"
              />
            </td>
          </ng-container>
        }

        @for (column of columns(); track column.key) {
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
              {{ row[column.key] }}
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
        <tr mat-row *matRowDef="let row; columns: displayedColumnKeys()"></tr>
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
  readonly rowActions = input<readonly DataTableRowAction[]>([]);
  readonly selectionChange = output<readonly DataTableRow[]>();
  readonly rowAction = output<DataTableActionEvent>();

  protected readonly dataSource = new MatTableDataSource<DataTableRow>();
  protected readonly selection = new SelectionModel<DataTableRow>(true, []);
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
    this.selectionChange.emit(this.selection.selected);
  }

  protected toggleRow(row: DataTableRow): void {
    this.selection.toggle(row);
    this.selectionChange.emit(this.selection.selected);
  }
}
