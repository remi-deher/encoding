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
import { DetailField, DetailFieldListComponent } from '../../shared/ui/detail-field-list.component';
import { MEDIA_TYPE_OPTIONS, MediaType } from '../../shared/models/media-type';

interface LibraryRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly category: MediaType;
  readonly mediaType: string;
  readonly configuredNodes: string;
  readonly accessModes: string;
  readonly nodePaths: string;
  readonly lastScan: string;
  readonly associatedFlows: string;
  readonly state: string;
  readonly modifiedAt: string;
  readonly modifiedOrder: number;
}

const LIBRARY_RECORDS: readonly LibraryRecord[] = [
  {
    id: 'library-films',
    name: 'Films',
    category: 'video',
    mediaType: 'Vidéo',
    configuredNodes: '3 / 4 nœuds',
    accessModes: '2 directs · 1 transfert WebDAV',
    nodePaths: 'media-core-01 · /mnt/media/films\nworker-gpu-01 · /mnt/media/films\nworker-cpu-02 · transfert WebDAV',
    lastScan: 'Aujourd’hui, 09:30',
    associatedFlows: 'Films — H.265 · Films — Vérification de la source',
    state: 'Accès configuré',
    modifiedAt: '13 oct. 2026',
    modifiedOrder: Date.UTC(2026, 9, 13),
  },
  {
    id: 'library-series',
    name: 'Séries',
    category: 'video',
    mediaType: 'Vidéo',
    configuredNodes: '2 / 4 nœuds',
    accessModes: '2 directs',
    nodePaths: 'media-core-01 · /mnt/media/series\nworker-cpu-02 · /mnt/media/series',
    lastScan: 'Hier, 22:10',
    associatedFlows: 'Séries — H.265',
    state: 'Accès configuré',
    modifiedAt: '10 oct. 2026',
    modifiedOrder: Date.UTC(2026, 9, 10),
  },
  {
    id: 'library-photos',
    name: 'Photos',
    category: 'image',
    mediaType: 'Image',
    configuredNodes: '1 / 4 nœuds',
    accessModes: '1 direct',
    nodePaths: 'media-core-01 · /mnt/media/photos',
    lastScan: '8 oct. 2026',
    associatedFlows: 'Images — Conversion WebP',
    state: 'Accès configuré',
    modifiedAt: '8 oct. 2026',
    modifiedOrder: Date.UTC(2026, 9, 8),
  },
  {
    id: 'library-music',
    name: 'Musique',
    category: 'music',
    mediaType: 'Musique',
    configuredNodes: '2 / 4 nœuds',
    accessModes: '1 direct · 1 transfert WebDAV',
    nodePaths: 'media-core-01 · /mnt/media/music\nworker-cpu-02 · transfert WebDAV',
    lastScan: '2 oct. 2026',
    associatedFlows: 'Musique — Vérification FLAC',
    state: 'Accès configuré',
    modifiedAt: '2 oct. 2026',
    modifiedOrder: Date.UTC(2026, 9, 2),
  },
  {
    id: 'library-subtitles',
    name: 'Sous-titres',
    category: 'subtitles',
    mediaType: 'Sous-titres',
    configuredNodes: '0 / 4 nœuds',
    accessModes: 'Aucun accès déclaré',
    nodePaths: 'Aucun point d’entrée configuré',
    lastScan: 'Jamais',
    associatedFlows: 'Aucun flow associé',
    state: 'À configurer',
    modifiedAt: '28 sept. 2026',
    modifiedOrder: Date.UTC(2026, 8, 28),
  },
];

@Component({
  selector: 'app-library-catalog-page',
  imports: [DataTableComponent, DetailFieldListComponent, ManagementCatalogTemplateComponent, CategoryTabsComponent],
  templateUrl: './library-catalog-page.component.html',
})
export class LibraryCatalogPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  protected readonly title = computed(
    () => (this.routeData()['title'] as string) ?? 'Bibliothèques',
  );
  protected readonly description = computed(
    () => (this.routeData()['description'] as string) ?? '',
  );
  protected readonly selectedCategory = signal('all');
  protected readonly selectedLibrary = signal<LibraryRecord | null>(null);
  protected readonly detailTitle = computed(() => this.selectedLibrary()?.name ?? 'Bibliothèque');
  protected readonly detailDescription = computed(() => this.selectedLibrary()?.mediaType ?? 'Détails de la bibliothèque');
  protected readonly detailFields = computed<readonly DetailField[]>(() => {
    const library = this.selectedLibrary();
    return library ? [
      { section: 'Bibliothèque', label: 'Type de média', value: library.mediaType },
      { section: 'Bibliothèque', label: 'État', value: library.state },
      { section: 'Accès par nœud', label: 'Nœuds concernés', value: library.configuredNodes },
      { section: 'Accès par nœud', label: 'Mode de transfert', value: library.accessModes },
      { section: 'Accès par nœud', label: 'Points d’entrée', value: library.nodePaths },
      { section: 'Activité', label: 'Dernière analyse', value: library.lastScan },
      { section: 'Activité', label: 'Flows associés', value: library.associatedFlows },
      { section: 'Activité', label: 'Dernière modification', value: library.modifiedAt },
    ] : [];
  });

  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom', sortable: true },
    { key: 'mediaType', label: 'Type', sortable: true },
    { key: 'configuredNodes', label: 'Accès déclarés', sortable: true },
    { key: 'accessModes', label: 'Modes d’accès', sortable: true },
    { key: 'state', label: 'État', sortable: true },
    { key: 'modifiedAt', label: 'Modifiée le', sortable: true, sortValueKey: 'modifiedOrder' },
  ];

  protected readonly categoryOptions = computed<readonly CategoryTabOption[]>(() => [
    { value: 'all', label: 'Tous', count: LIBRARY_RECORDS.length },
    ...MEDIA_TYPE_OPTIONS.map((category) => ({
      ...category,
      count: LIBRARY_RECORDS.filter((library) => library.category === category.value).length,
    })),
  ]);

  protected readonly filteredLibraries = computed(() => {
    const category = this.selectedCategory();
    return LIBRARY_RECORDS.filter((library) => category === 'all' || library.category === category);
  });

  protected onCategoryChange(category: string): void {
    this.selectedCategory.set(category);
  }

  protected onLibraryOpen(row: DataTableRow): void {
    this.selectedLibrary.set(LIBRARY_RECORDS.find((library) => library.id === row['id']) ?? null);
  }

  protected onDetailClosed(): void {
    this.selectedLibrary.set(null);
  }
}
