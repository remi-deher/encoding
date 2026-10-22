import { ResourceListComponent, ResourceListItem } from '../../shared/ui/resource-list.component';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { firstValueFrom, map } from 'rxjs';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { DataTableRow } from '../../shared/ui/data-table.component';
import {
  ResourceSheetComponent,
  ResourceSheetSection,
} from '../../shared/ui/resource-sheet.component';
import { DrawerActionButtonComponent } from '../../shared/ui/drawer-action-button/drawer-action-button.component';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import { RecordFormComponent, RecordFormData } from '../../shared/ui/record-form.component';
import { FlowTemplates } from '../../shared/services/flow-templates';
import { FlowTemplateSummary } from '../../shared/models/flow-templates';
import { MEDIA_TYPE_OPTIONS } from '../../shared/models/media-type';

interface FlowTemplateRow extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly mediaType: string;
  readonly source: string;
  readonly sourceVersion: string;
  readonly components: string;
  readonly description: string;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-template-library-page',
  imports: [
    ResourceListComponent,
    CategoryTabsComponent,
    DrawerActionButtonComponent,
    ManagementCatalogTemplateComponent,
    RecordFormComponent,
    ResourceSheetComponent,
  ],
  template: `
    <app-management-catalog-template
      [title]="title()"
      filterLabel="Modèles de flow"
      searchLabel="Rechercher un modèle"
      [searchQuery]="query()"
      (searchQueryChange)="query.set($event)"
      [catalogState]="catalogState()"
      [catalogMessage]="catalogMessage()"
      [actionOpen]="actionOpen()"
      [actionTitle]="actionForm().title"
      [actionDescription]="actionForm().description"
      [detailOpen]="selectedTemplate() !== null"
      [detailTitle]="selectedTemplate()?.name ?? 'Modèle'"
      [detailDescription]="selectedTemplate()?.sourceFlowName ?? ''"
      detailLayout="page"
      searchLayout="centered"
      (actionClosed)="closeCreateFlow()"
    >
      <app-category-tabs
        catalogue-navigation
        label="Ressources"
        [options]="librarySections"
        selected="templates"
        (selectedChange)="onSectionChanged($event)"
      />

      <app-resource-list
        catalogue-table
        icon="copy"
        [items]="items()"
        [actions]="rowActions"
        openLabel="Ouvrir"
        (open)="openTemplate({ id: $event })"
        (action)="onRowAction($event)"
        emptyMessage="Aucun modèle. Depuis Flows, ouvrez le menu d’un flow publié puis choisissez Créer un modèle."
      />

      <ng-template #detailContent catalogue-detail>
        @if (selectedTemplate(); as template) {
          <app-resource-sheet
            ariaLabel="Résumé et origine du modèle"
            [title]="template.name"
            [subtitle]="templateSubtitle()"
            [sections]="templateSections()"
          />
        }
      </ng-template>

      <ng-template #detailHeaderActions>
        @if (selectedTemplate(); as template) {
          <app-drawer-action-button
            label="Créer un flow depuis ce modèle"
            (activated)="openCreateFlow(template)"
          />
        }
      </ng-template>

      <ng-template #actionContent>
        <app-record-form
          [data]="actionForm()"
          (submitted)="continueToFlow($event)"
          (cancelled)="closeCreateFlow()"
        />
      </ng-template>
    </app-management-catalog-template>
  `,
})
export class FlowTemplateLibraryPageComponent {
  private readonly api = inject(FlowTemplates);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });
  protected readonly title = computed(
    () => (this.routeData()['title'] as string) ?? 'Modèles de flow',
  );
  protected readonly librarySections: readonly CategoryTabOption[] = [
    { value: 'media', label: 'Bibliothèques' },
    { value: 'scripts', label: 'Scripts' },
    { value: 'templates', label: 'Modèles de flow' },
    { value: 'packs', label: 'Packs de composants' },
    { value: 'encoding', label: 'Profils d’encodage' },
  ];
  protected readonly catalogState = signal<'ready' | 'loading' | 'error'>('loading');
  protected readonly catalogMessage = signal('Chargement des modèles…');
  protected readonly templates = signal<readonly FlowTemplateSummary[]>([]);
  protected readonly selectedTemplate = signal<FlowTemplateSummary | null>(null);
  protected readonly templateForCreation = signal<FlowTemplateSummary | null>(null);
  protected readonly actionOpen = signal(false);
  protected readonly actionForm = computed<RecordFormData>(() => {
    const template = this.templateForCreation();
    return {
      title: 'Nom du nouveau flow',
      description: template
        ? `Il partira du modèle « ${template.name} » et sera enregistré comme un flow distinct.`
        : 'Choisissez le nom de ce nouveau flow.',
      submitLabel: 'Ouvrir l’éditeur',
      initialValues: { name: template ? `${template.name} — Copie` : '' },
      fields: [{ id: 'name', label: 'Nom du flow', type: 'text', required: true, maxLength: 128 }],
    };
  });
  protected readonly query = signal('');
  protected readonly rowActions = [{ id: 'create-flow', label: 'Créer un flow' }];
  protected readonly rows = computed<readonly FlowTemplateRow[]>(() =>
    this.templates().map((template) => ({
      id: template.id,
      name: template.name,
      mediaType:
        MEDIA_TYPE_OPTIONS.find((media) => media.value === template.mediaType)?.label ??
        template.mediaType,
      source: template.sourceFlowName,
      sourceVersion: `v${template.sourceVersion}`,
      components: String(template.nodeCount),
      description: template.description,
    })),
  );
  protected readonly filteredRows = computed(() => {
    const query = this.query().trim().toLocaleLowerCase();
    return query
      ? this.rows().filter(
          (row) =>
            row.name.toLocaleLowerCase().includes(query) ||
            row.source.toLocaleLowerCase().includes(query) ||
            row.description.toLocaleLowerCase().includes(query),
        )
      : this.rows();
  });
  protected readonly items = computed<readonly ResourceListItem[]>(() =>
    this.filteredRows().map((row) => ({
      id: row.id,
      name: row.name,
      subtitle: row.description || undefined,
      tags: [
        { label: row.mediaType },
        { label: `${row.components} composant(s)` },
        { label: `${row.source} · ${row.sourceVersion}`, tone: 'ok' as const },
      ],
    })),
  );
  protected readonly templateSubtitle = computed(() => {
    const template = this.selectedTemplate();
    if (!template) return '';
    return (
      MEDIA_TYPE_OPTIONS.find((media) => media.value === template.mediaType)?.label ??
      template.mediaType
    );
  });
  protected readonly templateSections = computed<readonly ResourceSheetSection[]>(() => {
    const template = this.selectedTemplate();
    if (!template) return [];
    const mediaType =
      MEDIA_TYPE_OPTIONS.find((media) => media.value === template.mediaType)?.label ??
      template.mediaType;
    const createdAt = new Date(template.createdAt).toLocaleString('fr-FR');

    return [
      {
        id: 'template-summary',
        title: 'Modèle',
        caption: mediaType,
        items: [
          {
            label: template.name,
            detail: template.description || 'Aucune description.',
            badges: [
              { label: 'Copie immuable', tone: 'accent' },
              { label: `${template.nodeCount} composant(s)` },
              { label: `Créé le ${createdAt}`, tone: 'neutral' },
            ],
          },
        ],
      },
      {
        id: 'template-origin',
        title: 'Flow source',
        caption: `Version publiée v${template.sourceVersion}`,
        fields: [
          { label: 'Flow', value: template.sourceFlowName },
          { label: 'Identifiant', value: template.sourceFlowId, mono: true },
          { label: 'Version', value: `v${template.sourceVersion}` },
        ],
      },
    ];
  });

  private readonly itemParam = toSignal(
    this.route.queryParamMap.pipe(map((params) => params.get('template'))),
    { initialValue: this.route.snapshot.queryParamMap.get('template') },
  );

  constructor() {
    void this.loadTemplates();
    // La fiche est portée par l'URL (?template=<id>) : le menu latéral et le bouton précédent
    // du navigateur ramènent à la liste.
    effect(() => {
      const id = this.itemParam();
      const items = this.templates();
      untracked(() => {
        if (!id) {
          if (this.selectedTemplate()) this.selectedTemplate.set(null);
          return;
        }
        if (this.selectedTemplate()?.id === id) return;
        const item = items.find((candidate) => candidate.id === id);
        if (item) this.selectedTemplate.set(item);
      });
    });
  }

  protected onSectionChanged(section: string): void {
    if (section === 'media') void this.router.navigateByUrl('/libraries');
    if (section === 'scripts') void this.router.navigateByUrl('/libraries/scripts');
    if (section === 'packs') void this.router.navigateByUrl('/libraries/packs');
    if (section === 'encoding') void this.router.navigateByUrl('/libraries/encoding-profiles');
  }

  protected openTemplate(row: DataTableRow): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { template: String(row['id']) },
    });
  }

  protected onRowAction(event: { readonly actionId: string; readonly row: DataTableRow }): void {
    if (event.actionId !== 'create-flow') return;
    const template = this.templates().find((candidate) => candidate.id === event.row['id']);
    if (template) this.openCreateFlow(template);
  }

  protected openCreateFlow(template: FlowTemplateSummary): void {
    this.templateForCreation.set(template);
    this.actionOpen.set(true);
  }

  protected closeCreateFlow(): void {
    this.actionOpen.set(false);
    this.templateForCreation.set(null);
  }

  protected continueToFlow(values: Record<string, string>): void {
    const template = this.templateForCreation();
    if (!template) return;
    void this.router.navigate(['/flows/new'], {
      queryParams: {
        templateId: template.id,
        name: values['name'],
        mediaType: template.mediaType,
      },
      info: { flowEditorDirection: 'forward' },
    });
    this.closeCreateFlow();
  }

  private async loadTemplates(): Promise<void> {
    try {
      this.templates.set(await firstValueFrom(this.api.list()));
      this.catalogState.set('ready');
      this.catalogMessage.set('');
    } catch (error) {
      this.catalogState.set('error');
      this.catalogMessage.set(
        error instanceof HttpErrorResponse
          ? (error.error?.title ?? 'Les modèles ne sont pas disponibles.')
          : 'Les modèles ne sont pas disponibles.',
      );
    }
  }
}
