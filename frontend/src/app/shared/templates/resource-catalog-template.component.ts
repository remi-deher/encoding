import { ChangeDetectionStrategy, Component, input, model, output } from '@angular/core';
import {
  DataTableActionEvent,
  DataTableRowAction,
  DataTableSelectionActionEvent,
} from '../ui/data-table.component';
import { IconName } from '../ui/icon.component';
import {
  ResourceListComponent,
  ResourceListItem,
  ResourceListSelectionAction,
  ResourceListToggleEvent,
} from '../ui/resource-list.component';
import {
  CatalogEmptyState,
  ManagementCatalogTemplateComponent,
} from './management-catalog-template.component';

/** Shell partagé des catalogues : la feature fournit les lignes et les interactions métier. */
@Component({
  selector: 'app-resource-catalog-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ManagementCatalogTemplateComponent, ResourceListComponent],
  template: `
    <app-management-catalog-template
      [title]="title()"
      [showPageHeader]="showPageHeader()"
      [filterLabel]="filterLabel()"
      [searchLabel]="searchLabel()"
      [searchQuery]="searchQuery()"
      [searchLayout]="searchLayout()"
      [filterCount]="filterCount()"
      [note]="note()"
      [showToolbar]="showToolbar()"
      [emptyState]="emptyState()"
      [catalogState]="catalogState()"
      [catalogMessage]="catalogMessage()"
      [detailLayout]="detailLayout()"
      [detailOpen]="detailOpen()"
      [detailTitle]="detailTitle()"
      [detailDescription]="detailDescription()"
      [actionOpen]="actionOpen()"
      [actionTitle]="actionTitle()"
      [actionDescription]="actionDescription()"
      [actionStep]="actionStep()"
      [drawerSize]="drawerSize()"
      [drawerHeaderDensity]="drawerHeaderDensity()"
      [drawerModal]="drawerModal()"
      (searchQueryChange)="searchQueryChange.emit($event)"
      (emptyStateAction)="emptyStateAction.emit()"
      (detailClosed)="detailClosed.emit()"
      (actionClosed)="actionClosed.emit()"
    >
      <ng-content select="[catalogue-navigation]" />
      <ng-content select="[catalogue-summary]" />
      <div catalogue-table>
        <app-resource-list
          [items]="items()"
          [actions]="rowActions()"
          [icon]="resourceIcon()"
          [showIcon]="showResourceIcon()"
          [openLabel]="openLabel()"
          [selectionUnit]="selectionUnit()"
          [emptyMessage]="listEmptyMessage()"
          [selectable]="selectable()"
          [disabled]="disabled()"
          [selectionActions]="selectionActions()"
          [(selectedIds)]="selectedIds"
          (open)="open.emit($event)"
          (action)="action.emit($event)"
          (toggleChange)="toggleChange.emit($event)"
          (selectionAction)="selectionAction.emit($event)"
        />
      </div>
      <ng-content select="ng-template" />
    </app-management-catalog-template>
  `,
})
export class ResourceCatalogTemplateComponent {
  readonly title = input.required<string>();
  readonly items = input.required<readonly ResourceListItem[]>();
  readonly rowActions = input<readonly DataTableRowAction[]>([]);
  readonly selectionActions = input<readonly ResourceListSelectionAction[]>([]);
  readonly resourceIcon = input<IconName>('flow');
  readonly showResourceIcon = input(true);
  readonly openLabel = input('Ouvrir');
  readonly selectionUnit = input('élément(s)');
  readonly listEmptyMessage = input('Aucun résultat.');
  readonly selectable = input(false);
  readonly disabled = input(false);
  readonly selectedIds = model<ReadonlySet<string>>(new Set());

  readonly showPageHeader = input(true);
  readonly filterLabel = input('Filtres');
  readonly searchLabel = input('');
  readonly searchQuery = input('');
  readonly searchLayout = input<'end' | 'centered' | 'centered-sheet'>('end');
  readonly filterCount = input(0);
  readonly note = input('');
  readonly showToolbar = input(true);
  readonly emptyState = input<CatalogEmptyState | null>(null);
  readonly catalogState = input<'ready' | 'loading' | 'error'>('ready');
  readonly catalogMessage = input('');
  readonly detailLayout = input<'drawer' | 'page'>('drawer');
  readonly detailOpen = input(false);
  readonly detailTitle = input('Détails');
  readonly detailDescription = input('');
  readonly actionOpen = input(false);
  readonly actionTitle = input('Ajouter');
  readonly actionDescription = input('');
  readonly actionStep = input<{ readonly current: number; readonly total: number } | null>(null);
  readonly drawerSize = input<'standard' | 'wide'>('wide');
  readonly drawerHeaderDensity = input<'standard' | 'compact'>('standard');
  readonly drawerModal = input(true);

  readonly searchQueryChange = output<string>();
  readonly emptyStateAction = output<void>();
  readonly detailClosed = output<void>();
  readonly actionClosed = output<void>();
  readonly open = output<string>();
  readonly action = output<DataTableActionEvent>();
  readonly toggleChange = output<ResourceListToggleEvent>();
  readonly selectionAction = output<DataTableSelectionActionEvent>();
}
