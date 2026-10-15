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
import {
  DetailCard,
  DetailCardGridComponent,
  DetailCardIcon,
} from '../../shared/ui/detail-card-grid.component';
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
    DetailCardGridComponent,
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
      <app-data-table layout="cards"
        catalogue-table
        [columns]="columns"
        [rows]="filteredRows()"
        [rowOpenable]="true"
        rowOpenLabel="Consulter le pack"
        (rowOpen)="openPack($event)"
        emptyMessage="Aucun pack. Créez-en un depuis une version publiée dans Flows."
      />
      <ng-template #detailContent catalogue-detail>
        <app-detail-card-grid
          [cards]="packCards()"
          [initiallyExpanded]="true"
          [collapsible]="false"
          ariaLabel="Résumé, origine et interface du pack"
          emptyMessage="Aucune information disponible pour ce pack."
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
  private readonly selectedDetailsLoadState = signal<'idle' | 'loading' | 'ready' | 'error'>(
    'idle',
  );
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
  protected readonly packCards = computed<readonly DetailCard[]>(() => {
    const pack = this.selectedPack();
    if (!pack) return [];

    const details = this.selectedDetails();
    const inputNode = details?.definition.nodes.find((node) => node.id === details.inputNodeId);
    const inputLabel = inputNode?.name || inputNode?.component.componentId || 'Premier composant';
    const loadState = this.selectedDetailsLoadState();
    const interfaceStatus =
      loadState === 'ready'
        ? { label: 'Détails chargés', tone: 'success' as const }
        : loadState === 'error'
          ? { label: 'Indisponible', tone: 'warning' as const }
          : { label: 'Chargement', tone: 'neutral' as const };
    const mediaType =
      MEDIA_TYPE_OPTIONS.find((item) => item.value === pack.mediaType)?.label ?? pack.mediaType;

    return [
      {
        id: 'pack-summary',
        title: 'Pack de composants',
        subtitle: mediaType,
        icon: this.mediaIcon(pack.mediaType),
        status: { label: 'Disponible', tone: 'success' },
        tags: [
          { label: `${pack.componentCount} composant(s)` },
          { label: new Date(pack.createdAt).toLocaleDateString('fr-FR'), tone: 'neutral' },
        ],
        sections: [
          {
            title: 'Description',
            items: [{ label: pack.name, description: pack.description || 'Aucune description.' }],
          },
        ],
      },
      {
        id: 'pack-source',
        title: 'Flow source',
        subtitle: pack.sourceFlowName,
        icon: 'tool',
        status: { label: `Version publiée v${pack.sourceVersion}`, tone: 'accent' },
        tags: [],
        sections: [
          {
            title: 'Référence conservée',
            items: [
              { label: 'Flow', description: pack.sourceFlowName },
              { label: 'Identifiant', description: pack.sourceFlowId },
              { label: 'Version', description: `v${pack.sourceVersion}` },
            ],
          },
        ],
      },
      {
        id: 'pack-interface',
        title: 'Interface du pack',
        subtitle: 'Entrée et sorties exposées',
        icon: 'plugin',
        status: interfaceStatus,
        tags: [],
        sections: [
          {
            title: 'Entrée',
            items: [
              {
                label: details ? inputLabel : 'Entrée du pack',
                description: details
                  ? 'Point d’entrée public du fragment'
                  : loadState === 'error'
                    ? 'Impossible de charger la définition du pack.'
                    : 'Chargement de la définition…',
                tone: details ? 'accent' : 'neutral',
              },
            ],
          },
          {
            title: 'Sorties',
            items: details?.outputs.length
              ? details.outputs.map((output) => ({
                  label: output.label,
                  description: 'Sortie exposée',
                }))
              : [
                  {
                    label: 'Interface',
                    description: details
                      ? 'Aucune sortie exposée'
                      : loadState === 'error'
                        ? 'Détail indisponible'
                        : 'Chargement…',
                  },
                ],
          },
        ],
      },
    ];
  });

  private mediaIcon(mediaType: string): DetailCardIcon {
    if (mediaType === 'music' || mediaType === 'audio') return 'audio';
    if (mediaType === 'image') return 'image';
    return 'video';
  }

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
    this.selectedDetailsLoadState.set(pack ? 'loading' : 'idle');
    if (!pack) return;
    try {
      const details = await this.api.get(pack.id);
      if (this.selectedPack()?.id === pack.id) {
        this.selectedDetails.set(details);
        this.selectedDetailsLoadState.set('ready');
      }
    } catch {
      if (this.selectedPack()?.id === pack.id) {
        this.selectedDetails.set(null);
        this.selectedDetailsLoadState.set('error');
      }
    }
  }

  protected closeDetail(): void {
    this.selectedPack.set(null);
    this.selectedDetails.set(null);
    this.selectedDetailsLoadState.set('idle');
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
