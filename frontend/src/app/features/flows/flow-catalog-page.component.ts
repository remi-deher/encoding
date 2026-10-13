import { FlowValidation } from '../../shared/services/flow-validation';
import { map, catchError, of, timer, switchMap } from 'rxjs';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import {
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
} from '../../shared/ui/data-table.component';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import { DetailField, DetailFieldListComponent } from '../../shared/ui/detail-field-list.component';
import { MEDIA_TYPE_OPTIONS, MediaType } from '../../shared/models/media-type';
import { FLOW_CREATION_FORM } from '../../shared/models/flow-creation';
import { RecordFormComponent } from '../../shared/ui/record-form.component';
import { DrawerActionButtonComponent } from '../../shared/ui/drawer-action-button/drawer-action-button.component';

interface FlowRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly category: MediaType;
  readonly mediaType: string;
  readonly version: string;
  readonly summary: string;
  readonly components: string;
  readonly state: string;
  readonly backup: string;
  readonly modifiedAt: string;
  readonly modifiedOrder: number;
}

@Component({
  selector: 'app-flow-catalog-page',
  imports: [
    DataTableComponent,
    DetailFieldListComponent,
    ManagementCatalogTemplateComponent,
    CategoryTabsComponent,
    MatButtonModule,
    RecordFormComponent,
    DrawerActionButtonComponent,
  ],
  templateUrl: './flow-catalog-page.component.html',
})
export class FlowCatalogPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  private readonly api = inject(FlowValidation);
  private readonly catalog = toSignal(
    timer(0, 5000).pipe(
      switchMap(() => this.api.list()),
      map((flows): { state: 'ready' | 'error' | 'loading'; records: readonly FlowRecord[] } => ({
        state: 'ready' as const,
        records: flows.map((flow) => ({
          id: flow.id,
          name: flow.name,
          category: flow.mediaType,
          mediaType:
            MEDIA_TYPE_OPTIONS.find((type) => type.value === flow.mediaType)?.label ??
            flow.mediaType,
          version: 'v' + flow.version,
          summary: flow.nodeCount + ' étapes',
          components: flow.nodeCount + ' composants',
          state: 'Enregistré',
          backup:
            flow.backupStatus === 'ready'
              ? 'Disponible'
              : flow.backupStatus === 'failed'
                ? 'En échec'
                : 'En attente',
          modifiedAt: new Date(flow.updatedAt).toLocaleString('fr-FR'),
          modifiedOrder: Date.parse(flow.updatedAt),
        })),
      })),
      catchError(() => of({ state: 'error' as const, records: [] as readonly FlowRecord[] })),
    ),
    { initialValue: { state: 'loading' as const, records: [] as readonly FlowRecord[] } },
  );
  protected readonly catalogState = computed(() => this.catalog().state);
  private readonly records = computed(() => this.catalog().records);

  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Flows');
  protected readonly selectedCategory = signal('all');
  protected readonly actionOpen = signal(false);
  protected readonly flowCreationForm = FLOW_CREATION_FORM;
  protected readonly selectedFlow = signal<FlowRecord | null>(null);
  protected readonly detailTitle = computed(() => this.selectedFlow()?.name ?? 'Flow');
  protected readonly detailDescription = computed(() => {
    const flow = this.selectedFlow();
    return flow ? `${flow.mediaType} · ${flow.version} · ${flow.state}` : 'Détails du flow';
  });
  protected readonly detailFields = computed<readonly DetailField[]>(() => {
    const flow = this.selectedFlow();
    return flow
      ? [
          { section: 'Flow', label: 'Type de média', value: flow.mediaType },
          { section: 'Flow', label: 'État', value: flow.state },
          { section: 'Flow', label: 'Version', value: flow.version },
          { section: 'Flow', label: 'Sauvegarde JSON', value: flow.backup },
          { section: 'Traitement', label: 'Résumé', value: flow.summary },
          { section: 'Traitement', label: 'Composants', value: flow.components },
          { section: 'Historique', label: 'Dernière modification', value: flow.modifiedAt },
        ]
      : [];
  });

  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom du flow', sortable: true },
    { key: 'mediaType', label: 'Type', sortable: true },
    { key: 'version', label: 'Version', sortable: true },
    { key: 'state', label: 'État', sortable: true },
    { key: 'backup', label: 'Sauvegarde JSON', sortable: true },
    { key: 'modifiedAt', label: 'Modifié le', sortable: true, sortValueKey: 'modifiedOrder' },
  ];

  protected readonly categoryOptions = computed<readonly CategoryTabOption[]>(() => [
    { value: 'all', label: 'Tous', count: this.records().length },
    ...MEDIA_TYPE_OPTIONS.map((category) => ({
      ...category,
      count: this.records().filter((flow) => flow.category === category.value).length,
    })),
  ]);

  protected readonly filteredFlows = computed(() => {
    const category = this.selectedCategory();
    return this.records().filter((flow) => category === 'all' || flow.category === category);
  });

  protected onCategoryChange(category: string): void {
    this.selectedCategory.set(category);
  }

  protected onFlowOpen(row: DataTableRow): void {
    this.selectedFlow.set(this.records().find((flow) => flow.id === row['id']) ?? null);
  }

  protected onDetailClosed(): void {
    this.selectedFlow.set(null);
  }

  protected openFlowCreation(): void {
    this.actionOpen.set(true);
  }

  protected closeFlowCreation(): void {
    this.actionOpen.set(false);
  }

  protected startFlow(values: Record<string, string>): void {
    void this.router.navigate(['/flows/new'], {
      queryParams: {
        name: values['name'],
        mediaType: values['mediaType'],
        starter: values['starter'],
      },
      info: { flowEditorDirection: 'forward' },
    });
    this.actionOpen.set(false);
  }
}
