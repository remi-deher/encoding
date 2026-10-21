import { FlowDetailsComponent } from './editor/flow-details.component';
import { FlowDocument } from '../../shared/models/flow-validation';
import { FlowComponentDefinition } from '../../shared/models/flow-component';
import { RecordFormData } from '../../shared/ui/record-form.component';
import { MatDialog } from '@angular/material/dialog';
import { FlowDeleteDialogComponent } from './flow-delete-dialog.component';
import { FileDownload } from '../../shared/services/file-download';
import { NotificationService } from '../../shared/services/notification.service';
import { HttpErrorResponse } from '@angular/common/http';
import { FlowValidation } from '../../shared/services/flow-validation';
import { map, catchError, of, timer, switchMap, merge, Subject } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import {
  DataTableColumn,
  DataTableListActionEvent,
  DataTableActionEvent,
  DataTableSelectionActionEvent,
  DataTableComponent,
  DataTableRow,
} from '../../shared/ui/data-table.component';
import { IconComponent } from '../../shared/ui/icon.component';
import {
  CatalogEmptyState,
  ManagementCatalogTemplateComponent,
} from '../../shared/templates/management-catalog-template.component';
import { MEDIA_TYPE_ICONS, MEDIA_TYPE_OPTIONS, MediaType } from '../../shared/models/media-type';
import { FLOW_CREATION_FORM } from '../../shared/models/flow-creation';
import { RecordFormComponent } from '../../shared/ui/record-form.component';
import { DrawerActionButtonComponent } from '../../shared/ui/drawer-action-button/drawer-action-button.component';
import { FlowTemplates } from '../../shared/services/flow-templates';
import { FlowComponentPacks } from '../../shared/services/flow-component-packs';
import { FlowPreviewComponent } from '../../shared/ui/flow-preview/flow-preview';
import { firstValueFrom } from 'rxjs';
import { FileImportComponent } from '../../shared/ui/file-import.component';

interface FlowRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly category: MediaType;
  readonly mediaType: string;
  readonly steps: string;
  readonly nodeCount: number;
  readonly publishedVersion: number;
  readonly version: string;
  readonly summary: string;
  readonly components: string;
  readonly state: string;
  readonly status: string;
  readonly backup: string;
  readonly modifiedAt: string;
  readonly modifiedOrder: number;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-catalog-page',
  imports: [
    DataTableComponent,
    FlowDetailsComponent,
    ManagementCatalogTemplateComponent,
    IconComponent,
    MatButtonModule,
    MatMenuModule,
    RecordFormComponent,
    DrawerActionButtonComponent,
    FlowPreviewComponent,
    FileImportComponent,
  ],
  templateUrl: './flow-catalog-page.component.html',
  styleUrl: './flow-catalog-page.component.scss',
})
export class FlowCatalogPageComponent {
  protected readonly auth = inject(AuthService);
  protected readonly deletingFlow = signal<string | null>(null);
  private readonly refreshCatalog = new Subject<void>();
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  private readonly actionDefinition = signal<RecordFormData | null>(null);
  protected readonly actionForm = computed(() => this.actionDefinition() ?? this.flowCreationForm);
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
        this.actionDefinition.set(null);
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
        this.actionDefinition.set(null);
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
    this.startFlow(values);
  }

  private readonly dialog = inject(MatDialog);
  private readonly downloads = inject(FileDownload);
  private readonly notifications = inject(NotificationService);
  protected readonly listActions = [{ id: 'import', label: 'Importer', fileAccept: '.json,.zip' }];
  protected readonly importing = signal(false);
  protected async onImportFile(file: File): Promise<void> {
    this.importing.set(true);
    try {
      await this.onListAction({ actionId: 'import', file });
    } finally {
      this.importing.set(false);
    }
  }
  protected readonly rowActions = [
    { id: 'edit', label: 'Ouvrir l’éditeur' },
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
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger' as const,
      visibleWhen: () => this.auth.isAdmin() && this.deletingFlow() === null,
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
    if (event.actionId === 'delete') {
      await this.confirmDeleteFlow(event.row);
      return;
    }
    if (event.actionId === 'edit') {
      void this.router.navigate(['/flows', String(event.row['id']), 'edit'], {
        info: { flowEditorDirection: 'forward' },
      });
      return;
    }
    if (event.actionId === 'component-pack') {
      const version = Number(event.row['publishedVersion']);
      this.packSource.set({ id: String(event.row['id']), version });
      this.templateSource.set(null);
      void this.loadSourcePreview(String(event.row['id']), version);
      this.actionDefinition.set({
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
      void this.loadSourcePreview(String(event.row['id']), version);
      this.actionDefinition.set({
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
      this.onFlowOpen(event.row);
      this.detailTab.set(1);
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
  protected async confirmDeleteFlow(row: DataTableRow): Promise<void> {
    if (!this.auth.isAdmin() || this.deletingFlow() !== null) return;
    const id = String(row['id']),
      name = String(row['name']);
    this.deletingFlow.set(id);
    try {
      const libraries = await this.api.usage(id);
      const confirmed = await firstValueFrom(
        this.dialog.open(FlowDeleteDialogComponent, { data: { name, libraries } }).afterClosed(),
      );
      if (!confirmed) return;
      await this.api.remove(id, libraries.length > 0);
      if (this.selectedFlow()?.id === id) this.onDetailClosed();
      this.refreshCatalog.next();
      this.notifications.show(`Le flow « ${name} » a été supprimé.`);
    } catch (error) {
      this.notifications.show(AuthService.errorMessage(error, 'Impossible de supprimer ce flow.'));
    } finally {
      this.deletingFlow.set(null);
    }
  }
  private readonly templates = inject(FlowTemplates);
  private readonly componentPacks = inject(FlowComponentPacks);
  private readonly catalog = toSignal(
    merge(timer(0, 5000), this.refreshCatalog).pipe(
      switchMap(() =>
        this.api.list().pipe(
          map((flows) => ({ state: 'ready' as const, flows })),
          catchError(() => of({ state: 'error' as const, flows: [] })),
        ),
      ),
      map(
        ({
          state,
          flows,
        }): { state: 'ready' | 'error' | 'loading'; records: readonly FlowRecord[] } => ({
          state,
          records: flows.map((flow) => ({
            id: flow.id,
            name: flow.name,
            category: flow.mediaType,
            mediaType:
              (MEDIA_TYPE_OPTIONS.find((type) => type.value === flow.mediaType)?.label ??
                flow.mediaType) +
              ' · ' +
              flow.nodeCount +
              (flow.nodeCount === 1 ? ' étape' : ' étapes'),
            steps: flow.nodeCount + (flow.nodeCount === 1 ? ' étape' : ' étapes'),
            nodeCount: flow.nodeCount,
            publishedVersion: flow.version,
            version: 'v' + flow.version,
            summary: flow.nodeCount + ' étapes',
            components: flow.nodeCount + ' composants',
            state: flow.isDraft ? 'Brouillon' : 'Publié',
            status: (flow.isDraft ? 'Brouillon' : 'Publié') + ' · v' + flow.version,
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
        }),
      ),
    ),
    { initialValue: { state: 'loading' as const, records: [] as readonly FlowRecord[] } },
  );
  protected readonly catalogState = computed(() => this.catalog().state);
  private readonly records = computed(() => this.catalog().records);

  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Flows');
  protected readonly searchQuery = signal('');
  protected readonly detailTab = signal(0);
  protected refreshDetails(): void {
    this.refreshCatalog.next();
  }
  protected readonly actionOpen = signal(false);
  protected readonly flowCreationForm = FLOW_CREATION_FORM;
  protected readonly selectedFlow = signal<FlowRecord | null>(null);
  protected readonly detailTitle = computed(
    () =>
      this.records().find((flow) => flow.id === this.selectedFlow()?.id)?.name ??
      this.selectedFlow()?.name ??
      'Flow',
  );
  protected readonly detailDescription = computed(() => {
    const flow = this.selectedFlow();
    if (!flow) return 'Détails du flow';
    return (
      flow.mediaType +
      ' · ' +
      (flow.state === 'Brouillon'
        ? 'Brouillon · ' +
          (flow.publishedVersion > 0
            ? 'Dernière publication ' + flow.version
            : 'Aucune version publiée')
        : flow.state + ' · ' + flow.version)
    );
  });
  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom du flow', sortable: true },
    { key: 'steps', label: 'Étapes', sortable: true, sortValueKey: 'nodeCount' },
    { key: 'status', label: 'État & version', sortable: true },
    { key: 'backup', label: 'Sauvegarde JSON', sortable: true },
    { key: 'modifiedAt', label: 'Modifié le', sortable: true, sortValueKey: 'modifiedOrder' },
  ];

  protected readonly filteredFlows = computed(() => {
    const type = this.typeFilter();
    const query = this.searchQuery().trim().toLocaleLowerCase('fr-FR');
    return this.records().filter(
      (flow) =>
        (!type || flow.category === type) &&
        (!query ||
          flow.name.toLocaleLowerCase('fr-FR').includes(query) ||
          flow.mediaType.toLocaleLowerCase('fr-FR').includes(query) ||
          flow.summary.toLocaleLowerCase('fr-FR').includes(query) ||
          flow.status.toLocaleLowerCase('fr-FR').includes(query) ||
          flow.state.toLocaleLowerCase('fr-FR').includes(query)),
    );
  });
  protected readonly typeFilter = signal<MediaType | null>(null);
  protected readonly totalCount = computed(() => this.records().length);
  protected readonly typeTiles = computed(() =>
    MEDIA_TYPE_OPTIONS.map((type) => ({
      ...type,
      icon: MEDIA_TYPE_ICONS[type.value],
      count: this.records().filter((flow) => flow.category === type.value).length,
    })),
  );
  protected toggleType(type: MediaType): void {
    this.typeFilter.update((current) => (current === type ? null : type));
  }
  protected readonly emptyState: CatalogEmptyState = {
    icon: '▶',
    eyebrow: 'FLOWS',
    title: 'Créez votre premier flow',
    description:
      'Un flow décrit les étapes appliquées à vos médias : analyse, conversion, sous-titres, publication. Partez d’un modèle ou importez un flow existant.',
    chips: MEDIA_TYPE_OPTIONS.map((type) => type.label),
    actionLabel: 'Créer le premier flow',
  };
  protected readonly rows = this.filteredFlows;
  protected readonly groups = computed(() =>
    MEDIA_TYPE_OPTIONS.map((type) => ({
      ...type,
      icon: MEDIA_TYPE_ICONS[type.value],
      rows: this.filteredFlows().filter((flow) => flow.category === type.value),
    })).filter((group) => group.rows.length > 0),
  );

  protected onFlowOpen(row: DataTableRow): void {
    this.detailTab.set(0);
    this.selectedFlow.set(this.records().find((flow) => flow.id === row['id']) ?? null);
  }

  protected onDetailClosed(): void {
    this.selectedFlow.set(null);
  }

  protected openFlowCreation(): void {
    this.clearSourcePreview();
    this.actionDefinition.set(null);
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
