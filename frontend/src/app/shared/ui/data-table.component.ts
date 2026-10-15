import { ResourceStripComponent, ResourceStripData } from './resource-strip.component';
import { SelectionModel } from '@angular/cdk/collections';
import {
  Component,
  contentChild,
  Directive,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  TemplateRef,
  ViewChild,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatSort, MatSortModule } from '@angular/material/sort';
import { MatTableDataSource, MatTableModule } from '@angular/material/table';
import { BadgeListComponent } from './badge-list.component';
import { splitBadges } from '../models/media-badges';
import {
  ConfirmationDialogComponent,
  ConfirmationDialogData,
} from './confirmation-dialog.component';

export type DataTableValue = string | number | boolean;
export type DataTableRow = Readonly<Record<string, DataTableValue>>;

export interface DataTableToggle {
  readonly showStatusLabel?: boolean;
  readonly ariaLabel: (row: DataTableRow, checked: boolean) => string;
  readonly disabledWhen?: (row: DataTableRow) => boolean;
  readonly confirmation?: (
    row: DataTableRow,
    checked: boolean,
  ) => ConfirmationDialogData | undefined;
}

export interface DataTableColumn {
  readonly key: string;
  readonly label: string;
  readonly sortable?: boolean;
  readonly sortValueKey?: string;
  readonly toggle?: DataTableToggle;
  /** La valeur est une liste de pastilles sérialisée avec `joinBadges`. */
  readonly badges?: boolean;
}

export interface DataTableRowAction {
  readonly id: string;
  readonly label: string;
  readonly expandedLabel?: string;
  readonly tone?: 'default' | 'danger';
  readonly visibleWhen?: (row: DataTableRow) => boolean;
  readonly confirmation?: (row: DataTableRow) => ConfirmationDialogData;
}

export interface DataTableRowDetailContext {
  readonly $implicit: DataTableRow;
}

@Directive({ selector: 'ng-template[dataTableRowDetail]', standalone: true })
export class DataTableRowDetailDirective {
  readonly template = inject(TemplateRef<DataTableRowDetailContext>);

  static ngTemplateContextGuard(
    _: DataTableRowDetailDirective,
    _context: unknown,
  ): _context is DataTableRowDetailContext {
    return true;
  }
}

export interface DataTableActionEvent {
  readonly actionId: string;
  readonly row: DataTableRow;
}

export interface DataTableToggleEvent {
  readonly key: string;
  readonly row: DataTableRow;
  readonly checked: boolean;
}

export interface DataTableSelectionAction {
  readonly id: string;
  readonly label: string;
  readonly visibleWhen?: (rows: readonly DataTableRow[]) => boolean;
  readonly confirmation?: (rows: readonly DataTableRow[]) => ConfirmationDialogData;
}

export interface DataTableSelectionActionEvent {
  readonly actionId: string;
  readonly rows: readonly DataTableRow[];
}
export interface DataTableListAction {
  readonly id: string;
  readonly label: string;
  readonly fileAccept?: string;
}
export interface DataTableListActionEvent {
  readonly actionId: string;
  readonly file?: File;
}

@Component({
  selector: 'app-data-table',
  imports: [
    BadgeListComponent,
    ResourceStripComponent,
    MatButtonModule,
    MatCheckboxModule,
    MatDialogModule,
    MatFormFieldModule,
    MatSelectModule,
    MatSortModule,
    MatTableModule,
    NgTemplateOutlet,
  ],
  template: `
    <div class="table-scroll-region">
      @if (listActions().length) {
        <div class="selection-toolbar" role="toolbar" aria-label="Actions du tableau">
          @for (action of listActions(); track action.id) {
            @if (action.fileAccept) {
              <input
                #filePicker
                type="file"
                hidden
                [accept]="action.fileAccept"
                (change)="pickFile(action.id, $event)"
              />
              <button class="selection-action" type="button" (click)="filePicker.click()">
                {{ action.label }}
              </button>
            } @else {
              <button
                class="selection-action"
                type="button"
                (click)="listAction.emit({ actionId: action.id })"
              >
                {{ action.label }}
              </button>
            }
          }
        </div>
      }
      @if (selectable() && layout() === 'table') {
        <button
          class="mobile-select-all"
          type="button"
          [attr.aria-label]="
            isAllSelected()
              ? 'Désélectionner les lignes affichées'
              : 'Sélectionner toutes les lignes affichées'
          "
          (click)="toggleAll()"
        >
          {{ isAllSelected() ? 'Désélectionner les lignes affichées' : 'Tout sélectionner' }}
        </button>
      }

      @if (selectable() && selectedRows().length) {
        <div class="selection-toolbar" role="toolbar" aria-label="Actions sur la sélection">
          <span class="selection-count" aria-live="polite">
            {{ selectedRows().length }}
            {{ selectedRows().length === 1 ? 'élément sélectionné' : 'éléments sélectionnés' }}
          </span>
          <div class="selection-actions">
            @if (rowOpenable() && selectedRows().length === 1) {
              <button class="selection-action" type="button" (click)="openSelectedRow()">
                {{ rowOpenLabel() }}
              </button>
            }
            @for (action of selectionActions(); track action.id) {
              @if (!action.visibleWhen || action.visibleWhen(selectedRows())) {
                <button
                  class="selection-action"
                  type="button"
                  (click)="requestSelectionAction(action)"
                >
                  {{ action.label }}
                </button>
              }
            }
            <button class="selection-clear" type="button" (click)="clearSelection()">
              Tout désélectionner
            </button>
          </div>
        </div>
      }

      <ng-template #cellTpl let-column="column" let-row="row" let-isFirst="isFirst">
        @if (column.toggle) {
          <div>
            <button
              class="row-toggle"
              [class.row-toggle_checked]="!!row[column.key]"
              type="button"
              role="switch"
              [attr.aria-checked]="!!row[column.key]"
              [attr.aria-label]="column.toggle.ariaLabel(row, !!row[column.key])"
              [disabled]="column.toggle.disabledWhen?.(row) ?? false"
              (click)="requestToggle(column, row)"
            >
              <span></span>
            </button>
            @if (column.toggle.showStatusLabel !== false) {
              <span class="row-toggle-status">{{
                row[column.key] ? 'Activée' : 'Désactivée'
              }}</span>
            }
          </div>
        } @else if (column.badges) {
          <app-badge-list [badges]="badgesOf(row[column.key])" />
        } @else if (rowOpenable() && isFirst) {
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
      </ng-template>

      @if (layout() === 'cards') {
        @if (selectable() || sortableColumns().length) {
          <div class="card-toolbar">
            @if (selectable()) {
              <mat-checkbox
                [checked]="isAllSelected()"
                [indeterminate]="selection.hasValue() && !isAllSelected()"
                (change)="toggleAll()"
                >Tout sélectionner</mat-checkbox
              >
            }
            @if (sortableColumns().length) {
              <div class="card-sort">
                <mat-form-field appearance="outline" subscriptSizing="dynamic">
                  <mat-label>Trier par</mat-label>
                  <mat-select
                    [value]="cardSortKey()"
                    (selectionChange)="cardSortKey.set($event.value)"
                  >
                    @for (column of sortableColumns(); track column.key) {
                      <mat-option [value]="column.key">{{ column.label }}</mat-option>
                    }
                  </mat-select>
                </mat-form-field>
                <button
                  matButton="outlined"
                  type="button"
                  [attr.aria-label]="cardSortDescending() ? 'Tri décroissant' : 'Tri croissant'"
                  (click)="cardSortDescending.set(!cardSortDescending())"
                >
                  {{ cardSortDescending() ? 'Z → A' : 'A → Z' }}
                </button>
              </div>
            }
          </div>
        }
        @if (cardRows().length === 0) {
          <p class="card-empty">{{ emptyMessage() }}</p>
        } @else {
          <ul class="card-grid" [class.resource-list]="resourceCards() !== null">
            @for (row of cardRows(); track row) {
              @if (resourceCards()?.[row['id'].toString()]; as resource) {
                <li>
                  <app-resource-strip
                    [data]="resource"
                    [selectable]="selectable()"
                    [selected]="selection.isSelected(row)"
                    [openable]="rowOpenable()"
                    [actions]="visibleActions(row)"
                    (selectionChanged)="toggleRow(row)"
                    (opened)="rowOpen.emit(row)"
                    (actionSelected)="resourceAction($event, row)"
                  />
                </li>
              } @else {
                <li
                  class="data-card"
                  [class.data-card_selected]="selection.isSelected(row)"
                  [class.data-card_expanded]="isExpandedRow(row)"
                >
                  <header class="data-card-header">
                    @if (selectable()) {
                      <mat-checkbox
                        [checked]="selection.isSelected(row)"
                        aria-label="Sélectionner cette ligne"
                        (click)="captureCheckboxClick($event)"
                        (change)="toggleRow(row)"
                      />
                    }
                    <h3 class="data-card-title">
                      <ng-container
                        [ngTemplateOutlet]="cellTpl"
                        [ngTemplateOutletContext]="{
                          column: columns()[0],
                          row: row,
                          isFirst: true,
                        }"
                      />
                    </h3>
                  </header>
                  <dl class="data-card-fields">
                    @for (column of columns().slice(1); track column.key) {
                      <div class="data-card-field">
                        <dt>{{ column.label }}</dt>
                        <dd>
                          <ng-container
                            [ngTemplateOutlet]="cellTpl"
                            [ngTemplateOutletContext]="{ column: column, row: row, isFirst: false }"
                          />
                        </dd>
                      </div>
                    }
                  </dl>
                  @if (rowActions().length > 0) {
                    <footer class="data-card-actions">
                      @for (action of rowActions(); track action.id) {
                        @if (!action.visibleWhen || action.visibleWhen(row)) {
                          <button
                            [matButton]="action.tone === 'danger' ? 'filled' : 'outlined'"
                            [class.data-card-action_danger]="action.tone === 'danger'"
                            type="button"
                            [attr.aria-label]="
                              rowActionLabel(action, row) + ' ' + rowActionSubject(row)
                            "
                            [attr.aria-expanded]="rowDetail() ? isExpandedRow(row) : null"
                            [attr.aria-controls]="rowDetail() ? rowDetailId(row) : null"
                            (click)="requestRowAction(action, row)"
                          >
                            {{ rowActionLabel(action, row) }}
                          </button>
                        }
                      }
                    </footer>
                  }
                  @if (rowDetail() && isExpandedRow(row)) {
                    <div class="data-card-detail" [id]="rowDetailId(row)">
                      <ng-container
                        [ngTemplateOutlet]="rowDetail()!.template"
                        [ngTemplateOutletContext]="{ $implicit: row }"
                      />
                    </div>
                  }
                </li>
              }
            }
          </ul>
        }
      } @else {
        <table mat-table matSort [dataSource]="dataSource" class="data-table" multiTemplateDataRows>
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
                <ng-container
                  [ngTemplateOutlet]="cellTpl"
                  [ngTemplateOutletContext]="{ column: column, row: row, isFirst: isFirst }"
                />
              </td>
            </ng-container>
          }

          @if (rowActions().length > 0 && separateRowActionColumns()) {
            @for (action of rowActions(); track action.id) {
              <ng-container [matColumnDef]="'row-action-' + action.id">
                <th mat-header-cell *matHeaderCellDef>{{ action.label }}</th>
                <td
                  mat-cell
                  *matCellDef="let row"
                  class="row-actions-cell"
                  [attr.data-label]="action.label"
                >
                  @if (!action.visibleWhen || action.visibleWhen(row)) {
                    <button
                      class="row-action"
                      [class.row-action_danger]="action.tone === 'danger'"
                      type="button"
                      [attr.aria-label]="rowActionLabel(action, row) + ' ' + rowActionSubject(row)"
                      [attr.aria-expanded]="rowDetail() ? isExpandedRow(row) : null"
                      [attr.aria-controls]="rowDetail() ? rowDetailId(row) : null"
                      (click)="requestRowAction(action, row)"
                    >
                      {{ rowActionLabel(action, row) }}
                    </button>
                  }
                </td>
              </ng-container>
            }
          }

          @if (rowActions().length > 0 && !separateRowActionColumns()) {
            <ng-container matColumnDef="row-actions">
              <th mat-header-cell *matHeaderCellDef>Actions</th>
              <td mat-cell *matCellDef="let row" class="row-actions-cell" data-label="Actions">
                @for (action of rowActions(); track action.id) {
                  @if (!action.visibleWhen || action.visibleWhen(row)) {
                    <button
                      class="row-action"
                      [class.row-action_danger]="action.tone === 'danger'"
                      type="button"
                      [attr.aria-label]="rowActionLabel(action, row) + ' ' + rowActionSubject(row)"
                      [attr.aria-expanded]="rowDetail() ? isExpandedRow(row) : null"
                      [attr.aria-controls]="rowDetail() ? rowDetailId(row) : null"
                      (click)="requestRowAction(action, row)"
                    >
                      {{ rowActionLabel(action, row) }}
                    </button>
                  }
                }
              </td>
            </ng-container>
          }

          @if (rowDetail()) {
            <ng-container matColumnDef="expandedDetail">
              <td mat-cell *matCellDef="let row" [attr.colspan]="displayedColumnKeys().length">
                @if (isExpandedRow(row)) {
                  <ng-container
                    [ngTemplateOutlet]="rowDetail()!.template"
                    [ngTemplateOutletContext]="{ $implicit: row }"
                  />
                }
              </td>
            </ng-container>
          }

          <tr mat-header-row *matHeaderRowDef="displayedColumnKeys()"></tr>
          <tr
            mat-row
            *matRowDef="let row; columns: displayedColumnKeys()"
            [attr.aria-selected]="selectable() ? selection.isSelected(row) : null"
          ></tr>
          @if (rowDetail()) {
            <tr
              mat-row
              class="detail-row"
              *matRowDef="let row; columns: ['expandedDetail']; when: isExpandedRowPredicate"
            ></tr>
          }
          <tr class="mat-row empty-row" *matNoDataRow>
            <td class="mat-cell" [attr.colspan]="displayedColumnKeys().length">
              {{ emptyMessage() }}
            </td>
          </tr>
        </table>
      }
    </div>
  `,
  styleUrl: './data-table.component.scss',
})
export class DataTableComponent {
  private readonly dialog = inject(MatDialog);
  readonly columns = input.required<readonly DataTableColumn[]>();
  readonly rows = input.required<readonly DataTableRow[]>();
  /** `cards` présente chaque ligne comme une carte : plus d'informations lisibles que le tableau dense. */
  readonly layout = input<'table' | 'cards'>('table');
  /** Présentation riche facultative, toujours gérée par la sélection et les actions communes. */
  readonly resourceCards = input<Readonly<Record<string, ResourceStripData>> | null>(null);
  readonly emptyMessage = input('Aucun résultat.');
  readonly selectable = input(false);
  readonly rowOpenable = input(false);
  readonly rowOpenLabel = input('Ouvrir');
  readonly rowActions = input<readonly DataTableRowAction[]>([]);
  readonly expandedRowId = input<string | null>(null);
  readonly rowDetail = contentChild(DataTableRowDetailDirective);
  readonly listActions = input<readonly DataTableListAction[]>([]);
  readonly listAction = output<DataTableListActionEvent>();
  protected pickFile(actionId: string, event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) this.listAction.emit({ actionId, file });
    input.value = '';
  }
  readonly separateRowActionColumns = input(false);
  readonly selectionActions = input<readonly DataTableSelectionAction[]>([]);
  readonly selectionChange = output<readonly DataTableRow[]>();
  readonly rowAction = output<DataTableActionEvent>();
  readonly toggleChange = output<DataTableToggleEvent>();
  readonly rowOpen = output<DataTableRow>();
  readonly selectionAction = output<DataTableSelectionActionEvent>();

  protected readonly cardSortKey = signal<string | null>(null);
  protected readonly cardSortDescending = signal(false);
  protected readonly sortableColumns = computed(() =>
    this.columns().filter((column) => column.sortable),
  );
  protected readonly cardRows = computed(() => {
    const rows = [...this.rows()];
    const key = this.cardSortKey();
    if (!key) return rows;
    const direction = this.cardSortDescending() ? -1 : 1;
    return rows.sort(
      (a, b) =>
        direction *
        String(this.sortValue(a, key)).localeCompare(String(this.sortValue(b, key)), 'fr', {
          numeric: true,
        }),
    );
  });

  protected readonly dataSource = new MatTableDataSource<DataTableRow>();
  protected readonly selection = new SelectionModel<DataTableRow>(true, []);
  protected readonly selectedRows = signal<readonly DataTableRow[]>([]);
  protected readonly displayedColumnKeys = computed(() => [
    ...(this.selectable() ? ['select'] : []),
    ...this.columns().map((column) => column.key),
    ...(this.separateRowActionColumns()
      ? this.rowActions().map((action) => `row-action-${action.id}`)
      : this.rowActions().length > 0
        ? ['row-actions']
        : []),
  ]);

  @ViewChild(MatSort)
  protected set sort(sort: MatSort | undefined) {
    if (sort) {
      this.dataSource.sort = sort;
    }
  }

  constructor() {
    effect(() => {
      this.expandedRowId();
      const rows = this.rows();
      this.dataSource.data = [...rows];
      if (this.resourceCards()) {
        const selectedIds = new Set(this.selection.selected.map((row) => String(row['id'])));
        this.selection.clear();
        this.selection.select(...rows.filter((row) => selectedIds.has(String(row['id']))));
        this.selectedRows.set([...this.selection.selected]);
      }
    });

    this.dataSource.sortingDataAccessor = (row, property) => this.sortValue(row, property);
  }

  private sortValue(row: DataTableRow, property: string): string | number {
    const sortValueKey = this.columns().find((column) => column.key === property)?.sortValueKey;
    const value = row[sortValueKey ?? property];
    return typeof value === 'boolean' ? Number(value) : (value ?? '');
  }

  protected badgesOf(value: DataTableValue | undefined): string[] {
    return splitBadges(value);
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

  protected requestToggle(column: DataTableColumn, row: DataTableRow): void {
    if (!column.toggle || column.toggle.disabledWhen?.(row)) return;
    const checked = !Boolean(row[column.key]);
    const confirmation = column.toggle.confirmation?.(row, checked);
    if (!confirmation) {
      this.toggleChange.emit({ key: column.key, row, checked });
      return;
    }

    this.dialog
      .open(ConfirmationDialogComponent, { data: confirmation })
      .afterClosed()
      .subscribe((confirmed: boolean | undefined) => {
        if (confirmed) this.toggleChange.emit({ key: column.key, row, checked });
      });
  }

  protected visibleActions(row: DataTableRow): readonly DataTableRowAction[] {
    return this.rowActions().filter((action) => !action.visibleWhen || action.visibleWhen(row));
  }
  protected resourceAction(id: string, row: DataTableRow): void {
    const action = this.visibleActions(row).find((action) => action.id === id);
    if (action) this.requestRowAction(action, row);
  }

  protected rowActionSubject(row: DataTableRow): string {
    return String(row['name'] ?? row['nodeName'] ?? row['id'] ?? 'cet élément');
  }

  protected rowActionLabel(action: DataTableRowAction, row: DataTableRow): string {
    return this.isExpandedRow(row) && action.expandedLabel ? action.expandedLabel : action.label;
  }

  protected isExpandedRow(row: DataTableRow): boolean {
    const expandedRowId = this.expandedRowId();
    return expandedRowId !== null && String(row['id']) === expandedRowId;
  }

  protected rowDetailId(row: DataTableRow): string {
    return 'data-table-row-detail-' + String(row['id']);
  }

  protected readonly isExpandedRowPredicate = (_: number, row: DataTableRow): boolean =>
    this.isExpandedRow(row);

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
        this.selection.select(
          ...rows.slice(Math.min(anchorIndex, targetIndex), Math.max(anchorIndex, targetIndex) + 1),
        );
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
    return (
      this.selectable() &&
      this.selectedRows().length > 0 &&
      (event.ctrlKey || event.metaKey || event.shiftKey)
    );
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
