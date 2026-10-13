import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import {
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
} from '../../shared/ui/data-table.component';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';

type PluginFamily = 'base' | 'media' | 'automation';

interface PluginRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly family: PluginFamily;
  readonly familyLabel: string;
  readonly category: string;
  readonly version: string;
  readonly components: string;
  readonly state: string;
}

const PLUGIN_RECORDS: readonly PluginRecord[] = [
  {
    id: 'base',
    name: 'Base',
    family: 'base',
    familyLabel: 'Noyau',
    category: 'Composants de flow',
    version: 'Intégré',
    components: 'Composants généraux',
    state: 'Actif',
  },
  {
    id: 'plex',
    name: 'Plex',
    family: 'media',
    familyLabel: 'Média',
    category: 'Serveur multimédia',
    version: '—',
    components: 'Déclencheurs et actions',
    state: 'Prévu',
  },
  {
    id: 'jellyfin',
    name: 'Jellyfin',
    family: 'media',
    familyLabel: 'Média',
    category: 'Serveur multimédia',
    version: '—',
    components: 'Déclencheurs et actions',
    state: 'Prévu',
  },
  {
    id: 'sonarr',
    name: 'Sonarr',
    family: 'automation',
    familyLabel: 'Automatisation',
    category: 'Intégration *ARR',
    version: '—',
    components: 'Déclencheurs et actions',
    state: 'Prévu',
  },
  {
    id: 'radarr',
    name: 'Radarr',
    family: 'automation',
    familyLabel: 'Automatisation',
    category: 'Intégration *ARR',
    version: '—',
    components: 'Déclencheurs et actions',
    state: 'Prévu',
  },
];

const PLUGIN_FAMILY_OPTIONS: readonly { readonly value: PluginFamily; readonly label: string }[] = [
  { value: 'base', label: 'Noyau' },
  { value: 'media', label: 'Média' },
  { value: 'automation', label: 'Automatisation' },
];

@Component({
  selector: 'app-plugin-catalog-page',
  imports: [DataTableComponent, ManagementCatalogTemplateComponent, CategoryTabsComponent],
  templateUrl: './plugin-catalog-page.component.html',
})
export class PluginCatalogPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Plugins');
  protected readonly description = computed(
    () => (this.routeData()['description'] as string) ?? '',
  );
  protected readonly selectedFamily = signal('all');

  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom', sortable: true },
    { key: 'familyLabel', label: 'Famille', sortable: true },
    { key: 'category', label: 'Type', sortable: true },
    { key: 'version', label: 'Version', sortable: true },
    { key: 'components', label: 'Composants fournis', sortable: true },
    { key: 'state', label: 'État', sortable: true },
  ];

  protected readonly familyOptions = computed<readonly CategoryTabOption[]>(() => [
    { value: 'all', label: 'Tous', count: PLUGIN_RECORDS.length },
    ...PLUGIN_FAMILY_OPTIONS.map((family) => ({
      ...family,
      count: PLUGIN_RECORDS.filter((plugin) => plugin.family === family.value).length,
    })),
  ]);

  protected readonly filteredPlugins = computed(() => {
    const family = this.selectedFamily();
    return PLUGIN_RECORDS.filter((plugin) => family === 'all' || plugin.family === family);
  });

  protected onFamilyChange(family: string): void {
    this.selectedFamily.set(family);
  }
}
