import { Component, computed, inject, signal } from '@angular/core';
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
import { DetailCard, DetailCardGridComponent } from '../../shared/ui/detail-card-grid.component';
import { FLOW_COMPONENT_KIND_LABELS } from '../../shared/models/flow-component';

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
  },
];

const PLUGIN_FAMILY_OPTIONS: readonly { readonly value: PluginFamily; readonly label: string }[] = [
  { value: 'base', label: 'Noyau' },
  { value: 'cluster', label: 'Cluster' },
  { value: 'media', label: 'Média' },
  { value: 'automation', label: 'Automatisation' },
];

@Component({
  selector: 'app-plugin-catalog-page',
  imports: [
    DataTableComponent,
    DetailCardGridComponent,
    ManagementCatalogTemplateComponent,
    CategoryTabsComponent,
    ComponentConfigurationFormComponent,
    FlowComponentListComponent,
    MatTabsModule,
  ],
  templateUrl: './plugin-catalog-page.component.html',
})
export class PluginCatalogPageComponent {
  private readonly flowValidation = inject(FlowValidation);
  private readonly configurationApi = inject(PluginConfigurationApi);
  private readonly notifications = inject(NotificationService);
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Plugins');
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
  protected readonly pluginCards = computed<readonly DetailCard[]>(() => {
    const plugin = this.selectedPlugin();
    if (!plugin) return [];

    const components = this.pluginComponents(plugin.id);
    const schema = this.configurationSchema();
    const active = plugin.state.toLocaleLowerCase('fr-FR').includes('actif');
    const mediaTypes = [...new Set(components.map((component) => component.mediaType))];

    return [
      {
        id: 'plugin-identity',
        title: 'Identité du plugin',
        subtitle: plugin.category,
        icon: 'plugin',
        status: { label: plugin.state, tone: active ? 'success' : 'accent' },
        tags: [
          { label: plugin.familyLabel, tone: 'accent' },
          { label: `Version ${plugin.version}` },
        ],
        sections: [
          {
            title: 'Catalogue',
            items: [
              { label: 'Famille', description: plugin.familyLabel },
              { label: 'Type', description: plugin.category },
            ],
          },
        ],
      },
      {
        id: 'plugin-runtime',
        title: 'Exécution et configuration',
        subtitle: 'Contrat du plugin',
        icon: 'tool',
        status: {
          label: schema ? `${schema.fields.length} champ(s)` : 'Sans schéma',
          tone: 'neutral',
        },
        tags: [
          { label: plugin.runtime, tone: 'neutral' },
          { label: plugin.configuration, tone: 'neutral' },
        ],
        sections: [
          {
            title: 'Informations déclarées',
            items: [
              { label: 'Mode d’exécution', description: plugin.runtime },
              { label: 'Configuration', description: plugin.configuration },
            ],
          },
        ],
      },
      {
        id: 'plugin-components',
        title: 'Blocs fournis',
        subtitle: `${components.length} composant(s) dans le catalogue`,
        icon: 'video',
        status: {
          label: components.length ? 'Catalogue chargé' : 'Aucun bloc',
          tone: components.length ? 'success' : 'warning',
        },
        tags: mediaTypes.map((mediaType) => ({ label: mediaType, tone: 'accent' })),
        sections: [
          {
            title: 'Composants',
            items: components.map((component) => ({
              label: component.name,
              description: `${FLOW_COMPONENT_KIND_LABELS[component.kind]} · ${component.description}`,
            })),
          },
        ],
      },
    ];
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
