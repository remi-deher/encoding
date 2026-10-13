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
  readonly modifiedAt: string;
  readonly modifiedOrder: number;
}

const FLOW_RECORDS: readonly FlowRecord[] = [
  {
    id: 'flow-video-library',
    name: 'Films — H.265',
    category: 'video',
    mediaType: 'Vidéo',
    version: 'v1',
    summary: 'Analyse le média, encode la vidéo en H.265 et actualise la bibliothèque Plex.',
    components: 'Analyse du média · Encodage vidéo · Actualisation Plex',
    state: 'Publié',
    modifiedAt: '13 oct. 2026',
    modifiedOrder: Date.UTC(2026, 9, 13),
  },
  {
    id: 'flow-video-series',
    name: 'Séries — H.265',
    category: 'video',
    mediaType: 'Vidéo',
    version: 'v3',
    summary: 'Encode les épisodes de séries selon le profil H.265 configuré.',
    components: 'Analyse du média · Encodage vidéo',
    state: 'Publié',
    modifiedAt: '10 oct. 2026',
    modifiedOrder: Date.UTC(2026, 9, 10),
  },
  {
    id: 'flow-video-source-check',
    name: 'Films — Vérification de la source',
    category: 'video',
    mediaType: 'Vidéo',
    version: '—',
    summary: 'Contrôle les informations de la source avant son traitement.',
    components: 'Analyse du média · Vérification de la source',
    state: 'Brouillon',
    modifiedAt: '8 oct. 2026',
    modifiedOrder: Date.UTC(2026, 9, 8),
  },
  {
    id: 'flow-image-webp',
    name: 'Images — Conversion WebP',
    category: 'image',
    mediaType: 'Image',
    version: 'v1',
    summary: 'Convertit les images de la bibliothèque au format WebP.',
    components: 'Conversion d’image',
    state: 'Publié',
    modifiedAt: '6 oct. 2026',
    modifiedOrder: Date.UTC(2026, 9, 6),
  },
  {
    id: 'flow-music-flac',
    name: 'Musique — Vérification FLAC',
    category: 'music',
    mediaType: 'Musique',
    version: 'v2',
    summary: 'Vérifie les fichiers audio FLAC de la bibliothèque.',
    components: 'Analyse audio · Vérification FLAC',
    state: 'Publié',
    modifiedAt: '2 oct. 2026',
    modifiedOrder: Date.UTC(2026, 9, 2),
  },
  {
    id: 'flow-subtitles-cleanup',
    name: 'Sous-titres — Nettoyage',
    category: 'subtitles',
    mediaType: 'Sous-titres',
    version: 'v1',
    summary: 'Nettoie les fichiers de sous-titres sélectionnés.',
    components: 'Nettoyage de sous-titres',
    state: 'Désactivé',
    modifiedAt: '28 sept. 2026',
    modifiedOrder: Date.UTC(2026, 8, 28),
  },
];

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
    return flow ? [
      { section: 'Flow', label: 'Type de média', value: flow.mediaType },
      { section: 'Flow', label: 'État', value: flow.state },
      { section: 'Flow', label: 'Version', value: flow.version },
      { section: 'Traitement', label: 'Résumé', value: flow.summary },
      { section: 'Traitement', label: 'Composants', value: flow.components },
      { section: 'Historique', label: 'Dernière modification', value: flow.modifiedAt },
    ] : [];
  });

  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom du flow', sortable: true },
    { key: 'mediaType', label: 'Type', sortable: true },
    { key: 'version', label: 'Version', sortable: true },
    { key: 'state', label: 'État', sortable: true },
    { key: 'modifiedAt', label: 'Modifié le', sortable: true, sortValueKey: 'modifiedOrder' },
  ];

  protected readonly categoryOptions = computed<readonly CategoryTabOption[]>(() => [
    { value: 'all', label: 'Tous', count: FLOW_RECORDS.length },
    ...MEDIA_TYPE_OPTIONS.map((category) => ({
      ...category,
      count: FLOW_RECORDS.filter((flow) => flow.category === category.value).length,
    })),
  ]);

  protected readonly filteredFlows = computed(() => {
    const category = this.selectedCategory();
    return FLOW_RECORDS.filter((flow) => category === 'all' || flow.category === category);
  });

  protected onCategoryChange(category: string): void {
    this.selectedCategory.set(category);
  }

  protected onFlowOpen(row: DataTableRow): void {
    this.selectedFlow.set(FLOW_RECORDS.find((flow) => flow.id === row['id']) ?? null);
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
      queryParams: { name: values['name'], mediaType: values['mediaType'], starter: values['starter'] },
      info: { flowEditorDirection: 'forward' },
    });
    this.actionOpen.set(false);
  }
}
