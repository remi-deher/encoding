import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import {
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
} from '../../shared/ui/data-table.component';
import { DetailField, DetailFieldListComponent } from '../../shared/ui/detail-field-list.component';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import { FlowComponentPacks } from '../../shared/services/flow-component-packs';
import { FlowComponentPackSummary } from '../../shared/models/flow-component-packs';
import { FlowComponentPackDetails } from '../../shared/models/flow-component-packs';
import { MEDIA_TYPE_OPTIONS } from '../../shared/models/media-type';

interface PackRow extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly mediaType: string;
  readonly components: string;
  readonly source: string;
  readonly version: string;
  readonly description: string;
}

@Component({
  selector: 'app-component-pack-library-page',
  imports: [
    CategoryTabsComponent,
    DataTableComponent,
    DetailFieldListComponent,
    ManagementCatalogTemplateComponent,
  ],
  template: `
    <app-management-catalog-template
      [title]="title()"
      filterLabel="Packs de composants"
      searchLabel="Rechercher un pack"
      [searchQuery]="query()"
      (searchQueryChange)="query.set($event)"
      note="Un pack est un fragment copié depuis une version publiée. Dans un flow, ses composants peuvent être dépliés et modifiés indépendamment du pack source."
      [catalogState]="catalogState()"
      [catalogMessage]="catalogMessage()"
      [detailOpen]="selectedPack() !== null"
      [detailTitle]="selectedPack()?.name ?? 'Pack de composants'"
      [detailDescription]="selectedPack()?.description ?? ''"
      (detailClosed)="closeDetail()"
    >
      <app-category-tabs
        catalogue-navigation
        label="Type de librairie"
        [options]="librarySections"
        selected="packs"
        (selectedChange)="onSectionChanged($event)"
      />
      <app-data-table
        catalogue-table
        [columns]="columns"
        [rows]="filteredRows()"
        [rowOpenable]="true"
        rowOpenLabel="Consulter le pack"
        (rowOpen)="openPack($event)"
        emptyMessage="Aucun pack. Créez-en un depuis une version publiée dans Flows."
      />
      <ng-template #detailContent catalogue-detail>
        <app-detail-field-list
          [fields]="detailFields()"
          [tabbed]="true"
          tabsLabel="Sections du pack"
        />
      </ng-template>
    </app-management-catalog-template>
  `,
})
export class ComponentPackLibraryPageComponent {
  private readonly api = inject(FlowComponentPacks);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });
  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Librairies');
  protected readonly librarySections: readonly CategoryTabOption[] = [
    { value: 'media', label: 'Médias' },
    { value: 'scripts', label: 'Scripts' },
    { value: 'templates', label: 'Modèles' },
    { value: 'packs', label: 'Packs de composants' },
    { value: 'encoding', label: 'Profils d’encodage' },
  ];
  protected readonly catalogState = signal<'ready' | 'loading' | 'error'>('loading');
  protected readonly catalogMessage = signal('Chargement des packs…');
  protected readonly packs = signal<readonly FlowComponentPackSummary[]>([]);
  protected readonly selectedPack = signal<FlowComponentPackSummary | null>(null);
  private readonly selectedDetails = signal<FlowComponentPackDetails | null>(null);
  protected readonly query = signal('');
  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Pack', sortable: true },
    { key: 'mediaType', label: 'Type de média', sortable: true },
    { key: 'components', label: 'Composants', sortable: true },
    { key: 'source', label: 'Flow source', sortable: true },
    { key: 'version', label: 'Version source', sortable: true },
  ];
  protected readonly rows = computed<readonly PackRow[]>(() =>
    this.packs().map((pack) => ({
      id: pack.id,
      name: pack.name,
      mediaType:
        MEDIA_TYPE_OPTIONS.find((item) => item.value === pack.mediaType)?.label ?? pack.mediaType,
      components: String(pack.componentCount),
      source: pack.sourceFlowName,
      version: `v${pack.sourceVersion}`,
      description: pack.description,
    })),
  );
  protected readonly filteredRows = computed(() => {
    const query = this.query().trim().toLocaleLowerCase();
    return query
      ? this.rows().filter((row) =>
          [row.name, row.mediaType, row.source, row.description].some((value) =>
            value.toLocaleLowerCase().includes(query),
          ),
        )
      : this.rows();
  });
  protected readonly detailFields = computed<readonly DetailField[]>(() => {
    const pack = this.selectedPack();
    return pack
      ? [
          { section: 'Pack', label: 'Nom', value: pack.name },
          { section: 'Pack', label: 'Description', value: pack.description || '—' },
          {
            section: 'Pack',
            label: 'Type de média',
            value:
              MEDIA_TYPE_OPTIONS.find((item) => item.value === pack.mediaType)?.label ??
              pack.mediaType,
          },
          { section: 'Pack', label: 'Composants', value: String(pack.componentCount) },
          {
            section: 'Interface',
            label: 'Entrée',
            value:
              this.selectedDetails()?.definition.nodes.find(
                (node) => node.id === this.selectedDetails()?.inputNodeId,
              )?.name ||
              this.selectedDetails()?.definition.nodes.find(
                (node) => node.id === this.selectedDetails()?.inputNodeId,
              )?.component.componentId ||
              'Premier composant',
          },
          {
            section: 'Interface',
            label: 'Sorties',
            value:
              this.selectedDetails()
                ?.outputs.map((output) => output.label)
                .join(', ') || 'Aucune sortie exposée',
          },
          { section: 'Origine', label: 'Flow', value: pack.sourceFlowName },
          { section: 'Origine', label: 'Version publiée', value: `v${pack.sourceVersion}` },
          {
            section: 'Pack',
            label: 'Créé le',
            value: new Date(pack.createdAt).toLocaleString('fr-FR'),
          },
        ]
      : [];
  });

  constructor() {
    void this.loadPacks();
  }

  protected onSectionChanged(section: string): void {
    if (section === 'media') void this.router.navigateByUrl('/libraries');
    if (section === 'scripts') void this.router.navigateByUrl('/libraries/scripts');
    if (section === 'templates') void this.router.navigateByUrl('/libraries/templates');
    if (section === 'encoding') void this.router.navigateByUrl('/libraries/encoding-profiles');
  }

  protected async openPack(row: DataTableRow): Promise<void> {
    const pack = this.packs().find((candidate) => candidate.id === row['id']) ?? null;
    this.selectedPack.set(pack);
    this.selectedDetails.set(null);
    if (!pack) return;
    try {
      const details = await this.api.get(pack.id);
      if (this.selectedPack()?.id === pack.id) this.selectedDetails.set(details);
    } catch {
      if (this.selectedPack()?.id === pack.id) this.selectedDetails.set(null);
    }
  }

  protected closeDetail(): void {
    this.selectedPack.set(null);
    this.selectedDetails.set(null);
  }

  private async loadPacks(): Promise<void> {
    try {
      this.packs.set(await firstValueFrom(this.api.list()));
      this.catalogState.set('ready');
      this.catalogMessage.set('');
    } catch (error) {
      this.catalogState.set('error');
      this.catalogMessage.set(
        error instanceof HttpErrorResponse
          ? (error.error?.title ?? 'Les packs ne sont pas disponibles.')
          : 'Les packs ne sont pas disponibles.',
      );
    }
  }
}
