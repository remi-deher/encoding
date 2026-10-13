import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import { DetailField, DetailFieldListComponent } from '../../shared/ui/detail-field-list.component';
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
  readonly runtime: string;
  readonly configuration: string;
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
    runtime: 'Fourni par le serveur',
    configuration: 'Aucune connexion externe requise',
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
    runtime: 'Non installé',
    configuration: 'Connexion non configurée',
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
    runtime: 'Non installé',
    configuration: 'Connexion non configurée',
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
    runtime: 'Non installé',
    configuration: 'Connexion non configurée',
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
    runtime: 'Non installé',
    configuration: 'Connexion non configurée',
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
  imports: [DataTableComponent, DetailFieldListComponent, ManagementCatalogTemplateComponent, CategoryTabsComponent],
  templateUrl: './plugin-catalog-page.component.html',
})
export class PluginCatalogPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Plugins');
  protected readonly selectedFamily = signal('all');
  protected readonly selectedPlugin = signal<PluginRecord | null>(null);
  protected readonly detailTitle = computed(() => this.selectedPlugin()?.name ?? 'Plugin');
  protected readonly detailDescription = computed(() => this.selectedPlugin()?.familyLabel ?? 'Détails du plugin');
  protected readonly detailFields = computed<readonly DetailField[]>(() => {
    const plugin = this.selectedPlugin();
    return plugin ? [
      { section: 'Plugin', label: 'Famille', value: plugin.familyLabel },
      { section: 'Plugin', label: 'Type', value: plugin.category },
      { section: 'Plugin', label: 'Version', value: plugin.version },
      { section: 'Disponibilité', label: 'État', value: plugin.state },
      { section: 'Disponibilité', label: 'Exécution', value: plugin.runtime },
      { section: 'Intégration', label: 'Composants fournis', value: plugin.components },
      { section: 'Intégration', label: 'Configuration', value: plugin.configuration },
    ] : [];
  });

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

  protected onPluginOpen(row: DataTableRow): void {
    this.selectedPlugin.set(PLUGIN_RECORDS.find((plugin) => plugin.id === row['id']) ?? null);
  }

  protected onDetailClosed(): void {
    this.selectedPlugin.set(null);
  }
}
