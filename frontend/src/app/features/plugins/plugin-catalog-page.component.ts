import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { MatTabsModule } from '@angular/material/tabs';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import {
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
} from '../../shared/ui/data-table.component';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { ComponentConfigurationFormComponent } from '../../shared/ui/component-configuration-form.component';
import { FlowValidation } from '../../shared/services/flow-validation';
import { PluginConfigurationApi } from '../../shared/services/plugin-configuration';
import { NotificationService } from '../../shared/services/notification.service';
import {
  FlowComponentConfiguration,
  FlowComponentConfigurationSchema,
} from '../../shared/models/flow-component';
import { FlowPluginCatalog } from '../../shared/models/flow-validation';
import { FlowComponentDefinition } from '../../shared/models/flow-component';
import { FlowComponentListComponent } from '../../shared/ui/flow-component-list.component';
import {
  ResourceSheetComponent,
  ResourceSheetSection,
} from '../../shared/ui/resource-sheet.component';

import { MatButtonModule } from '@angular/material/button';
import { ClusterExtensionsService } from '../../shared/services/cluster-extensions.service';
import {
  ClusterTool,
  ExtensionTab,
  StorePackage,
  StoreRepository,
} from '../../shared/models/cluster-extensions';

type PluginFamily = 'base' | 'cluster' | 'media' | 'automation';

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
  readonly iconUrl: string;
}

const PLUGIN_RECORDS: readonly PluginRecord[] = [
  {
    id: 'base',
    name: 'Base',
    family: 'base',
    familyLabel: 'Noyau',
    category: 'Composants de flow',
    version: 'Intégré',
    components:
      'Composants généraux, attente durable (délai, état d’un worker, charge, condition de plugin)',
    runtime: 'Fourni par le serveur',
    configuration: 'Aucune connexion externe requise',
    state: 'Actif',
    iconUrl: '/plugins/base.svg',
  },
  {
    id: 'nodes',
    name: 'Nœuds',
    family: 'cluster',
    familyLabel: 'Cluster',
    category: 'Orchestration des workers',
    version: '1.1.0',
    components: 'Transférer la suite, choisir un nœud ou exiger une capacité',
    runtime: 'Intégré au répartiteur et au worker',
    configuration: 'Cible par défaut, avec dérogations par flow et par bloc',
    state: 'Actif par défaut',
    iconUrl: '/plugins/nodes.svg',
  },
  {
    id: 'conditions',
    name: 'Conditions',
    family: 'base',
    familyLabel: 'Noyau',
    category: 'Aiguillage des flows',
    version: '1.0.0',
    components: 'Règles et groupes ET / OU imbriqués',
    runtime: 'Évaluation native sur les workers',
    configuration: 'Conditions définies sur chaque bloc',
    state: 'Actif par défaut',
    iconUrl: '/plugins/conditions.svg',
  },
  {
    id: 'plex',
    name: 'Plex',
    family: 'media',
    familyLabel: 'Média',
    category: 'Serveur multimédia',
    version: '1.0.0',
    components: 'Scan, attente et vérification de présence',
    runtime: 'Processus plugin livré avec l’image worker (déclaré dans Runtime:Plugins)',
    configuration: 'Connexion, correspondance des chemins et test de connexion',
    state: 'Catalogue intégré',
    iconUrl: '/plugins/plex.svg',
  },
  {
    id: 'jellyfin',
    name: 'Jellyfin',
    family: 'media',
    familyLabel: 'Média',
    category: 'Serveur multimédia',
    version: '1.0.0',
    components: 'Actualisation et vérification des éléments',
    runtime: 'Processus plugin livré avec l’image worker (déclaré dans Runtime:Plugins)',
    configuration: 'Connexion, correspondance des chemins et test de connexion',
    state: 'Catalogue intégré',
    iconUrl: '/plugins/jellyfin.svg',
  },
  {
    id: 'sonarr',
    name: 'Sonarr',
    family: 'automation',
    familyLabel: 'Automatisation',
    category: 'Intégration *ARR',
    version: '1.0.0',
    components: 'Actualisation, vérification et recherche d’épisodes',
    runtime: 'Processus plugin livré avec l’image worker (déclaré dans Runtime:Plugins)',
    configuration:
      'Connexion, correspondance des chemins, déclencheur d’import (webhook) et test de connexion',
    state: 'Catalogue intégré',
    iconUrl: '/plugins/sonarr.svg',
  },
  {
    id: 'radarr',
    name: 'Radarr',
    family: 'automation',
    familyLabel: 'Automatisation',
    category: 'Intégration *ARR',
    version: '1.0.0',
    components: 'Actualisation, vérification et recherche de films',
    runtime: 'Processus plugin livré avec l’image worker (déclaré dans Runtime:Plugins)',
    configuration:
      'Connexion, correspondance des chemins, déclencheur d’import (webhook) et test de connexion',
    state: 'Catalogue intégré',
    iconUrl: '/plugins/radarr.svg',
  },
];

const PLUGIN_FAMILY_OPTIONS: readonly { readonly value: PluginFamily; readonly label: string }[] = [
  { value: 'base', label: 'Noyau' },
  { value: 'cluster', label: 'Cluster' },
  { value: 'media', label: 'Média' },
  { value: 'automation', label: 'Automatisation' },
];

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-plugin-catalog-page',
  imports: [
    DataTableComponent,
    ResourceSheetComponent,
    ManagementCatalogTemplateComponent,
    CategoryTabsComponent,
    ComponentConfigurationFormComponent,
    FlowComponentListComponent,
    MatTabsModule,
    MatButtonModule,
  ],
  templateUrl: './plugin-catalog-page.component.html',
  styleUrl: './plugin-catalog-page.component.scss',
})
export class PluginCatalogPageComponent {
  private readonly flowValidation = inject(FlowValidation);
  private readonly configurationApi = inject(PluginConfigurationApi);
  private readonly extensionsService = inject(ClusterExtensionsService);
  private readonly notifications = inject(NotificationService);
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Plugins & Outils');
  protected readonly activeTab = signal<ExtensionTab>('plugins');
  protected readonly clusterTools = signal<readonly ClusterTool[]>([]);
  protected readonly storeRepositories = signal<readonly StoreRepository[]>([]);
  protected readonly storePackages = signal<readonly StorePackage[]>([]);
  protected readonly selectedStoreCategory = signal('all');

  protected readonly extensionTabs = computed<readonly CategoryTabOption[]>(() => [
    { value: 'plugins', label: 'Plugins', count: PLUGIN_RECORDS.length },
    { value: 'tools', label: 'Outils média', count: this.clusterTools().length || 6 },
    { value: 'store', label: 'Store & Dépôts', count: this.storePackages().length || 4 },
  ]);

  protected readonly storeCategoryOptions = computed<readonly CategoryTabOption[]>(() => [
    { value: 'all', label: 'Tous', count: this.storePackages().length },
    {
      value: 'subtitles',
      label: 'Sous-titres',
      count: this.storePackages().filter((p) => p.category === 'subtitles').length,
    },
    {
      value: 'audio',
      label: 'Audio',
      count: this.storePackages().filter((p) => p.category === 'audio').length,
    },
    {
      value: 'tools',
      label: 'Outils Média',
      count: this.storePackages().filter((p) => p.category === 'tools').length,
    },
  ]);

  protected readonly filteredStorePackages = computed(() => {
    const cat = this.selectedStoreCategory();
    const pkgs = this.storePackages();
    return cat === 'all' ? pkgs : pkgs.filter((p) => p.category === cat);
  });

  protected readonly selectedFamily = signal('all');
  protected readonly selectedPlugin = signal<PluginRecord | null>(null);
  private readonly pluginCatalog = signal<readonly FlowPluginCatalog[]>([]);
  protected readonly configurationValues = signal<FlowComponentConfiguration>({});
  protected readonly configuredSecrets = signal<readonly string[]>([]);
  protected readonly configurationRevision = signal(0);
  protected readonly configurationBusy = signal(false);
  protected readonly connectionTestBusy = signal(false);
  protected readonly connectionTestMessage = signal('');
  protected readonly connectionTestSuccess = signal(false);
  private configurationOperation = 0;
  private connectionTestOperation = 0;
  protected readonly configurationSchema = computed<FlowComponentConfigurationSchema | undefined>(
    () => {
      const pluginId = this.selectedPlugin()?.id;
      const schema = this.pluginCatalog().find(
        (plugin) => plugin.id === pluginId,
      )?.configurationSchema;
      if (!schema) return undefined;
      const secrets = this.configuredSecrets();
      return {
        ...schema,
        fields: schema.fields.map((field) =>
          field.type === 'secret' && secrets.includes(field.id)
            ? { ...field, required: false }
            : field,
        ),
      };
    },
  );
  protected readonly detailTitle = computed(() => this.selectedPlugin()?.name ?? 'Plugin');
  protected readonly detailDescription = computed(
    () => this.selectedPlugin()?.familyLabel ?? 'Détails du plugin',
  );
  protected readonly selectedComponents = computed<readonly FlowComponentDefinition[]>(() => {
    const plugin = this.selectedPlugin();
    if (!plugin) return [];
    return this.pluginComponents(plugin.id).map((component) => ({
      ...component,
      pluginId: plugin.id,
      pluginName: plugin.name,
    }));
  });
  protected readonly pluginSheet = computed<{
    readonly title: string;
    readonly subtitle: string;
    readonly iconUrl?: string;
    readonly sections: readonly ResourceSheetSection[];
  } | null>(() => {
    const plugin = this.selectedPlugin();
    if (!plugin) return null;

    const components = this.pluginComponents(plugin.id);
    const schema = this.configurationSchema();
    const active = plugin.state.toLocaleLowerCase('fr-FR').includes('actif');
    const mediaTypes = [...new Set(components.map((component) => component.mediaType))];

    const sections: readonly ResourceSheetSection[] = [
      {
        id: 'plugin-identity',
        title: 'Identité du plugin',
        items: [
          {
            label: plugin.name,
            detail: plugin.category,
            badges: [
              { label: plugin.state, tone: active ? 'success' : 'accent' },
              { label: plugin.familyLabel, tone: 'accent' },
              { label: `Version ${plugin.version}`, tone: 'neutral' },
            ],
          },
        ],
        fields: [
          { label: 'Famille', value: plugin.familyLabel },
          { label: 'Type', value: plugin.category },
          { label: 'Version', value: plugin.version },
        ],
      },
      {
        id: 'plugin-runtime',
        title: 'Exécution et configuration',
        caption: schema ? `${schema.fields.length} champ(s)` : 'Sans schéma',
        fields: [
          { label: 'Mode d’exécution', value: plugin.runtime },
          { label: 'Configuration', value: plugin.configuration },
        ],
      },
      {
        id: 'plugin-components',
        title: 'Catalogue des composants',
        caption: `${components.length} composant(s)`,
        items: [
          {
            label: 'Blocs déclarés',
            detail: `${components.length} composant(s) · consulter l’onglet Composants`,
            badges: [
              {
                label: components.length ? 'Catalogue chargé' : 'Aucun bloc',
                tone: components.length ? 'success' : 'warning',
              },
              ...mediaTypes.map((mediaType) => ({ label: mediaType, tone: 'accent' as const })),
            ],
          },
        ],
      },
    ];

    return {
      title: plugin.name,
      subtitle: `${plugin.familyLabel} · ${plugin.category}`,
      iconUrl: plugin.iconUrl,
      sections,
    };
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

  private pluginComponents(pluginId: string) {
    return this.pluginCatalog().find((plugin) => plugin.id === pluginId)?.components ?? [];
  }

  constructor() {
    void this.flowValidation
      .catalog()
      .then((plugins) => {
        this.pluginCatalog.set(plugins);
        const selected = this.selectedPlugin();
        if (selected) void this.loadConfiguration(selected.id);
      })
      .catch(() =>
        this.notifications.show('Le catalogue de configuration des plugins est indisponible.'),
      );

    void this.extensionsService
      .listTools()
      .then((tools) => this.clusterTools.set(tools))
      .catch(() => {});

    void this.extensionsService
      .listRepositories()
      .then((repos) => this.storeRepositories.set(repos))
      .catch(() => {});

    void this.extensionsService
      .listPackages()
      .then((pkgs) => this.storePackages.set(pkgs))
      .catch(() => {});
  }

  protected onExtensionTabChange(tab: string): void {
    this.activeTab.set(tab as ExtensionTab);
  }

  protected onStoreCategoryChange(cat: string): void {
    this.selectedStoreCategory.set(cat);
  }

  protected async testTool(tool: ClusterTool): Promise<void> {
    try {
      const res = await this.extensionsService.testToolConformance(tool.id);
      this.notifications.show(res.message);
    } catch {
      this.notifications.show(`Échec du test de conformité pour ${tool.name}.`);
    }
  }

  protected installPackage(pkg: StorePackage): void {
    this.notifications.show(
      `Le paquet '${pkg.name}' (${pkg.version}) est programmé pour déploiement sur le cluster.`,
    );
  }

  protected addCustomRepository(): void {
    this.notifications.show(
      'Pour ajouter un dépôt : saisissez une URL HTTP/HTTPS exposant un fichier repository.json conforme.',
    );
  }

  protected triggerZipUpload(): void {
    this.notifications.show(
      'Sideloading : Sélectionnez une archive .zip contenant plugin.json ou tool.json validé.',
    );
  }

  protected onFamilyChange(family: string): void {
    this.selectedFamily.set(family);
  }

  protected onPluginOpen(row: DataTableRow): void {
    const plugin = PLUGIN_RECORDS.find((candidate) => candidate.id === row['id']) ?? null;
    this.selectedPlugin.set(plugin);
    this.configurationValues.set({});
    this.configuredSecrets.set([]);
    this.configurationRevision.set(0);
    this.connectionTestMessage.set('');
    this.connectionTestOperation++;
    this.connectionTestBusy.set(false);
    if (plugin && this.pluginCatalog().some((candidate) => candidate.id === plugin.id)) {
      void this.loadConfiguration(plugin.id);
    }
  }

  private async loadConfiguration(pluginId: string): Promise<void> {
    const operation = ++this.configurationOperation;
    this.configurationBusy.set(true);
    try {
      const result = await this.configurationApi.getGlobal(pluginId);
      if (operation !== this.configurationOperation || this.selectedPlugin()?.id !== pluginId)
        return;
      this.configurationValues.set(result.values);
      this.configuredSecrets.set(result.configuredSecrets);
      this.configurationRevision.set(result.revision);
    } catch {
      if (operation === this.configurationOperation)
        this.notifications.show('Impossible de charger la configuration du plugin.');
    } finally {
      if (operation === this.configurationOperation) this.configurationBusy.set(false);
    }
  }

  protected async saveConfiguration(
    values: FlowComponentConfiguration,
    clearSecrets: readonly string[] = [],
  ): Promise<void> {
    const plugin = this.selectedPlugin();
    if (!plugin || this.configurationBusy()) return;
    const operation = ++this.configurationOperation;
    this.configurationBusy.set(true);
    try {
      const saved = await this.configurationApi.saveGlobal(
        plugin.id,
        this.configurationRevision(),
        values,
        clearSecrets,
      );
      if (operation !== this.configurationOperation || this.selectedPlugin()?.id !== plugin.id)
        return;
      this.configurationValues.set(saved.values);
      this.configuredSecrets.set(saved.configuredSecrets);
      this.configurationRevision.set(saved.revision);
      this.notifications.show('Configuration globale du plugin enregistrée.');
    } catch {
      if (operation === this.configurationOperation)
        this.notifications.show('Configuration non enregistrée. Rechargez-la avant de réessayer.');
    } finally {
      if (operation === this.configurationOperation) this.configurationBusy.set(false);
    }
  }

  protected onConfigurationCancelled(): void {
    const plugin = this.selectedPlugin();
    if (plugin) void this.loadConfiguration(plugin.id);
  }

  protected async testConnection(values: FlowComponentConfiguration): Promise<void> {
    const plugin = this.selectedPlugin();
    if (!plugin || this.connectionTestBusy()) return;
    const operation = ++this.connectionTestOperation;
    this.connectionTestBusy.set(true);
    this.connectionTestMessage.set('');
    try {
      const result = await this.configurationApi.testConnection(plugin.id, values);
      if (operation !== this.connectionTestOperation || this.selectedPlugin()?.id !== plugin.id)
        return;
      this.connectionTestSuccess.set(result.success);
      this.connectionTestMessage.set(result.message);
    } catch (error) {
      if (operation === this.connectionTestOperation && this.selectedPlugin()?.id === plugin.id) {
        this.connectionTestSuccess.set(false);
        const validationMessage =
          error instanceof HttpErrorResponse && typeof error.error?.title === 'string'
            ? error.error.title
            : undefined;
        this.connectionTestMessage.set(
          validationMessage ?? 'Test impossible. Vérifiez les champs puis réessayez.',
        );
      }
    } finally {
      if (operation === this.connectionTestOperation) this.connectionTestBusy.set(false);
    }
  }

  protected onDetailClosed(): void {
    this.configurationOperation++;
    this.connectionTestOperation++;
    this.configurationBusy.set(false);
    this.connectionTestBusy.set(false);
    this.selectedPlugin.set(null);
  }
}
