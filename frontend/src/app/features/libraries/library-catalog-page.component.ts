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

interface LibraryRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly category: MediaType;
  readonly mediaType: string;
  readonly configuredNodes: string;
  readonly accessModes: string;
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
    state: 'À configurer',
    modifiedAt: '28 sept. 2026',
    modifiedOrder: Date.UTC(2026, 8, 28),
  },
];

@Component({
  selector: 'app-library-catalog-page',
  imports: [DataTableComponent, ManagementCatalogTemplateComponent, CategoryTabsComponent],
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
}
