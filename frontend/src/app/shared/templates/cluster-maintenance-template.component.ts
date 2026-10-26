import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  contentChild,
  input,
  output,
  viewChild,
} from '@angular/core';
import { CategoryTabOption } from '../ui/category-tabs.component';
import { SubnavComponent } from '../ui/subnav.component';
import {
  CatalogEmptyState,
  ManagementCatalogTemplateComponent,
} from './management-catalog-template.component';

/** Shell commun des vues qui maintiennent les nœuds, capacités et extensions du cluster. */
@Component({
  selector: 'app-cluster-maintenance-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ManagementCatalogTemplateComponent, SubnavComponent],
  template: `
    <app-management-catalog-template
      [title]="title()"
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
      [pageActionsInput]="pageActionsContent()"
      [toolbarActionsInput]="toolbarActionsContent()"
      [detailContentInput]="detailContentContent()"
      [detailFooterActionsInput]="detailFooterActionsContent()"
      [detailHeaderActionsInput]="detailHeaderActionsContent()"
      [actionFooterActionsInput]="actionFooterActionsContent()"
      [actionContentInput]="actionContentContent()"
      [catalogueSummaryInput]="catalogueSummaryProjection()"
      [catalogueFiltersInput]="catalogueFiltersProjection()"
      [catalogueActiveFiltersInput]="catalogueActiveFiltersProjection()"
      [catalogueTableInput]="catalogueTableProjection()"
      (searchQueryChange)="searchQueryChange.emit($event)"
      (emptyStateAction)="emptyStateAction.emit()"
      (detailClosed)="detailClosed.emit()"
      (actionClosed)="actionClosed.emit()"
    >
      <app-subnav
        catalogue-navigation
        [label]="navigationLabel()"
        [options]="navigationOptions()"
        [selected]="navigationSelected()"
        (selectedChange)="navigationSelectedChange.emit($event)"
      />
      <ng-template #catalogueSummarySlot>
        <ng-content select="[catalogue-summary]" />
      </ng-template>
      <ng-template #catalogueFiltersSlot>
        <ng-content select="[catalogue-filters]" />
      </ng-template>
      <ng-template #catalogueActiveFiltersSlot>
        <ng-content select="[catalogue-active-filters]" />
      </ng-template>
      <ng-template #catalogueTableSlot>
        <ng-content select="[catalogue-table]" />
      </ng-template>
    </app-management-catalog-template>
  `,
})
export class ClusterMaintenanceTemplateComponent {
  /** Capture et transmet les fragments métier au gabarit catalogue comme templates explicites. */
  private readonly pageActionsContent = contentChild('pageActions', { read: TemplateRef });
  private readonly toolbarActionsContent = contentChild('catalogueToolbarActions', {
    read: TemplateRef,
  });
  private readonly detailContentContent = contentChild('detailContent', { read: TemplateRef });
  private readonly detailFooterActionsContent = contentChild('detailFooterActions', {
    read: TemplateRef,
  });
  private readonly detailHeaderActionsContent = contentChild('detailHeaderActions', {
    read: TemplateRef,
  });
  private readonly actionFooterActionsContent = contentChild('actionFooterActions', {
    read: TemplateRef,
  });
  private readonly actionContentContent = contentChild('actionContent', { read: TemplateRef });
  protected readonly catalogueSummaryProjection = viewChild('catalogueSummarySlot', {
    read: TemplateRef,
  });
  protected readonly catalogueFiltersProjection = viewChild('catalogueFiltersSlot', {
    read: TemplateRef,
  });
  protected readonly catalogueActiveFiltersProjection = viewChild('catalogueActiveFiltersSlot', {
    read: TemplateRef,
  });
  protected readonly catalogueTableProjection = viewChild('catalogueTableSlot', {
    read: TemplateRef,
  });

  readonly title = input.required<string>();
  readonly navigationLabel = input.required<string>();
  readonly navigationOptions = input.required<readonly CategoryTabOption[]>();
  readonly navigationSelected = input.required<string>();
  readonly navigationSelectedChange = output<string>();

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
}
