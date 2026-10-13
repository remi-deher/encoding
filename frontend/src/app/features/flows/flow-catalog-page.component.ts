import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import {
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
} from '../../shared/ui/data-table.component';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import { MEDIA_TYPE_OPTIONS, MediaType } from '../../shared/models/media-type';

interface FlowRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly category: MediaType;
  readonly mediaType: string;
  readonly version: string;
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
    state: 'Désactivé',
    modifiedAt: '28 sept. 2026',
    modifiedOrder: Date.UTC(2026, 8, 28),
  },
];

@Component({
  selector: 'app-flow-catalog-page',
  imports: [DataTableComponent, ManagementCatalogTemplateComponent, CategoryTabsComponent],
  templateUrl: './flow-catalog-page.component.html',
})
export class FlowCatalogPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Flows');
  protected readonly description = computed(
    () => (this.routeData()['description'] as string) ?? '',
  );
  protected readonly selectedCategory = signal('all');

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
}
