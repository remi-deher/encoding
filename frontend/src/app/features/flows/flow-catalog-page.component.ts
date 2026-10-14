import { compareFlowVersions } from '../../shared/models/flow-version-comparison';
import { FlowDocument } from '../../shared/models/flow-validation';
import { FlowComponentDefinition } from '../../shared/models/flow-component';
import { RecordFormData } from '../../shared/ui/record-form.component';
import { MatDialog } from '@angular/material/dialog';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import { FileDownload } from '../../shared/services/file-download';
import { NotificationService } from '../../shared/services/notification.service';
import { HttpErrorResponse } from '@angular/common/http';
import { FlowValidation } from '../../shared/services/flow-validation';
import { map, catchError, of, timer, switchMap } from 'rxjs';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import {
  DataTableColumn,
  DataTableListActionEvent,
  DataTableActionEvent,
  DataTableSelectionActionEvent,
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
import { FlowTemplates } from '../../shared/services/flow-templates';
import { FlowComponentPacks } from '../../shared/services/flow-component-packs';
import { FlowPreviewComponent } from '../../shared/ui/flow-preview/flow-preview';
import { firstValueFrom } from 'rxjs';

interface FlowRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly category: MediaType;
  readonly mediaType: string;
  readonly publishedVersion: number;
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
    FlowPreviewComponent,
  ],
  templateUrl: './flow-catalog-page.component.html',
})
export class FlowCatalogPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  private readonly comparisonVersions = signal<readonly FlowDocument[] | null>(null);
  private readonly comparisonForm = signal<RecordFormData | null>(null);
  protected readonly actionForm = computed(() => this.comparisonForm() ?? this.flowCreationForm);
  private readonly templateSource = signal<{
    readonly id: string;
    readonly version: number;
  } | null>(null);
  private readonly packSource = signal<{ readonly id: string; readonly version: number } | null>(
    null,
  );
  protected readonly sourcePreview = signal<FlowDocument | null>(null);
  protected readonly sourcePreviewComponents = signal<readonly FlowComponentDefinition[]>([]);
  protected readonly sourcePreviewStatus = signal<'idle' | 'loading' | 'ready' | 'error'>('idle');
  private previewRequestId = 0;

  private async loadSourcePreview(id: string, version: number): Promise<void> {
    const requestId = ++this.previewRequestId;
    this.sourcePreview.set(null);
    this.sourcePreviewComponents.set([]);
    this.sourcePreviewStatus.set('loading');
    try {
      const [saved, plugins] = await Promise.all([
        firstValueFrom(this.api.versionStatus(id, version)),
        this.api.catalog(),
      ]);
      if (requestId !== this.previewRequestId) return;
      this.sourcePreview.set(saved.definition);
      this.sourcePreviewComponents.set(
        plugins.flatMap((plugin) =>
          plugin.components.map((component) => ({
            ...component,
            pluginId: plugin.id,
            pluginName: plugin.name,
          })),
        ),
      );
      this.sourcePreviewStatus.set('ready');
    } catch {
      if (requestId === this.previewRequestId) this.sourcePreviewStatus.set('error');
    }
  }

  private clearSourcePreview(): void {
    this.previewRequestId++;
    this.sourcePreview.set(null);
    this.sourcePreviewComponents.set([]);
    this.sourcePreviewStatus.set('idle');
  }
  protected async submitAction(values: Record<string, string>): Promise<void> {
    const packSource = this.packSource();
    if (packSource) {
      try {
        await this.componentPacks.createFromFlow({
          sourceFlowId: packSource.id,
          sourceVersion: packSource.version,
          name: values['name'],
          description: values['description'] ?? '',
        });
        this.packSource.set(null);
        this.comparisonForm.set(null);
        this.clearSourcePreview();
        this.actionOpen.set(false);
        this.notifications.show('Pack créé depuis la version publiée du flow.');
      } catch (error) {
        this.notifications.show(
          error instanceof HttpErrorResponse
            ? (error.error?.title ?? 'Création du pack impossible.')
            : 'Création du pack impossible.',
        );
      }
      return;
    }
    const templateSource = this.templateSource();
    if (templateSource) {
      try {
        await this.templates.createFromFlow({
          sourceFlowId: templateSource.id,
          sourceVersion: templateSource.version,
          name: values['name'],
          description: values['description'] ?? '',
        });
        this.templateSource.set(null);
        this.comparisonForm.set(null);
        this.clearSourcePreview();
        this.actionOpen.set(false);
        this.notifications.show('Modèle créé depuis la version publiée du flow.');
      } catch (error) {
        this.notifications.show(
          error instanceof HttpErrorResponse
            ? (error.error?.title ?? 'Création du modèle impossible.')
            : 'Création du modèle impossible.',
        );
      }
      return;
    }
    const versions = this.comparisonVersions();
    if (versions) {
      const before = versions.find((version) => version.version === Number(values['before']));
      const after = versions.find((version) => version.version === Number(values['after']));
      if (before && after) {
        const differences = compareFlowVersions(before, after);
        this.actionOpen.set(false);
        this.dialog.open(ConfirmationDialogComponent, {
          data: {
            title: 'Comparaison v' + before.version + ' → v' + after.version,
            mode: 'information',
            items: differences.slice(0, 100),
            emptyMessage: 'Aucune différence de définition.',
            message:
              differences.length > 100
                ? 'Les 100 premières différences sont affichées.'
                : undefined,
          },
        });
      }
      return;
    }
    this.startFlow(values);
  }

  private readonly dialog = inject(MatDialog);
  private readonly downloads = inject(FileDownload);
  private readonly notifications = inject(NotificationService);
  protected readonly listActions = [{ id: 'import', label: 'Importer', fileAccept: '.json,.zip' }];
  protected readonly rowActions = [
    { id: 'export', label: 'Exporter' },
    { id: 'versions', label: 'Versions / Comparer' },
    {
      id: 'template',
      label: 'Créer un modèle',
      visibleWhen: (row: DataTableRow) => Number(row['publishedVersion']) > 0,
    },
    {
      id: 'component-pack',
      label: 'Créer un pack de composants',
      visibleWhen: (row: DataTableRow) => Number(row['publishedVersion']) > 0,
    },
  ];
  protected readonly selectionActions = [{ id: 'export', label: 'Exporter la sélection (ZIP)' }];
  protected async onListAction(event: DataTableListActionEvent): Promise<void> {
    if (!event.file) return;
    try {
      const result = await this.api.import(event.file);
      this.notifications.show(result.imported + ' flow(s) importé(s).');
    } catch (error) {
      this.notifications.show(
        error instanceof HttpErrorResponse
          ? (error.error?.title ??
              error.error?.issues?.map((issue: { message: string }) => issue.message).join(' ') ??
              'Import impossible.')
          : 'Import impossible.',
      );
    }
  }
  protected async onRowAction(event: DataTableActionEvent): Promise<void> {
    if (event.actionId === 'component-pack') {
      const version = Number(event.row['publishedVersion']);
      this.packSource.set({ id: String(event.row['id']), version });
      this.templateSource.set(null);
      this.comparisonVersions.set(null);
      void this.loadSourcePreview(String(event.row['id']), version);
      this.comparisonForm.set({
        title: 'Créer un pack de composants depuis un flow',
        description: `La version publiée v${version} sera copiée avec une entrée et ses sorties de flow.`,
        submitLabel: 'Créer le pack',
        fields: [
          { id: 'name', label: 'Nom du pack', type: 'text', required: true, maxLength: 128 },
          { id: 'description', label: 'Description', type: 'text', maxLength: 1000 },
        ],
        initialValues: { name: `${String(event.row['name'])} — Pack`, description: '' },
      });
      this.actionOpen.set(true);
      return;
    }
    if (event.actionId === 'template') {
      const version = Number(event.row['publishedVersion']);
      this.templateSource.set({ id: String(event.row['id']), version });
      this.packSource.set(null);
      this.comparisonVersions.set(null);
      void this.loadSourcePreview(String(event.row['id']), version);
      this.comparisonForm.set({
        title: 'Créer un modèle depuis un flow',
        description: `La version publiée v${version} sera copiée. Le flow d’origine ne sera pas modifié.`,
        submitLabel: 'Créer le modèle',
        fields: [
          {
            id: 'name',
            label: 'Nom du modèle',
            type: 'text',
            required: true,
            maxLength: 128,
          },
          {
            id: 'description',
            label: 'Description',
            type: 'text',
            maxLength: 1000,
          },
        ],
        initialValues: {
          name: `${String(event.row['name'])} — Modèle`,
          description: '',
        },
      });
      this.actionOpen.set(true);
      return;
    }
    if (event.actionId === 'versions') {
      try {
        const versions = await this.api.versions(String(event.row['id']));
        if (versions.length < 2) {
          this.dialog.open(ConfirmationDialogComponent, {
            data: {
              title: 'Versions du flow',
              mode: 'information',
              message: 'Une seconde version publiée est nécessaire pour comparer.',
              items: versions.map((v) => ({ label: 'Version ' + v.version, detail: v.name })),
            },
          });
          return;
        }
        this.comparisonVersions.set(versions);
        this.comparisonForm.set({
          title: 'Comparer les versions',
          description: 'Choisissez deux versions publiées du flow.',
          submitLabel: 'Comparer',
          initialValues: {
            before: String(versions.at(-2)!.version),
            after: String(versions.at(-1)!.version),
          },
          fields: [
            {
              id: 'before',
              label: 'Version de référence',
              type: 'select',
              required: true,
              options: versions.map((v) => ({ value: String(v.version), label: 'v' + v.version })),
            },
            {
              id: 'after',
              label: 'Version à comparer',
              type: 'select',
              required: true,
              options: versions.map((v) => ({ value: String(v.version), label: 'v' + v.version })),
            },
          ],
        });
        this.actionOpen.set(true);
      } catch {
        this.notifications.show('Historique indisponible.');
      }
      return;
    }
    try {
      this.downloads.save(
        await this.api.export(String(event.row['id'])),
        String(event.row['id']) + '.json',
      );
    } catch {
      this.notifications.show('Export impossible.');
    }
  }
  protected async onSelectionAction(event: DataTableSelectionActionEvent): Promise<void> {
    try {
      this.downloads.save(
        await this.api.exportMany(event.rows.map((row) => String(row['id']))),
        'flows.zip',
      );
    } catch {
      this.notifications.show('Export de la sélection impossible.');
    }
  }
  private readonly api = inject(FlowValidation);
  private readonly templates = inject(FlowTemplates);
  private readonly componentPacks = inject(FlowComponentPacks);
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
          publishedVersion: flow.version,
          version: 'v' + flow.version,
          summary: flow.nodeCount + ' étapes',
          components: flow.nodeCount + ' composants',
          state: flow.isDraft ? 'Brouillon' : 'Publié',
          backup:
            flow.backupStatus === 'draft'
              ? 'Brouillon en base'
              : flow.backupStatus === 'ready'
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
    this.clearSourcePreview();
    this.comparisonVersions.set(null);
    this.comparisonForm.set(null);
    this.templateSource.set(null);
    this.packSource.set(null);
    this.actionOpen.set(true);
  }

  protected closeFlowCreation(): void {
    this.clearSourcePreview();
    this.actionOpen.set(false);
    this.templateSource.set(null);
    this.packSource.set(null);
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
