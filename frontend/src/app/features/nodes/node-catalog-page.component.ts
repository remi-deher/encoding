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
import { DetailField, DetailFieldListComponent } from '../../shared/ui/detail-field-list.component';

type NodeRole = 'server' | 'worker';

interface NodeRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly roleFilter: string;
  readonly roleLabel: string;
  readonly capabilities: string;
  readonly libraryAccess: string;
  readonly workerLoad: string;
  readonly queueStatus: string;
  readonly state: string;
  readonly lastSignal: string;
}

const NODE_RECORDS: readonly NodeRecord[] = [
  {
    id: 'media-core-01',
    name: 'media-core-01',
    roleFilter: '|server|worker|',
    roleLabel: 'Serveur · Worker',
    capabilities: 'FFmpeg · CPU',
    libraryAccess: 'Films · accès direct',
    workerLoad: '1 tâche active · 4 simultanées max.',
    queueStatus: '3 tâches compatibles en attente',
    state: 'En ligne',
    lastSignal: 'À l’instant',
  },
  {
    id: 'server-core-02',
    name: 'server-core-02',
    roleFilter: '|server|',
    roleLabel: 'Serveur',
    capabilities: 'API · orchestration',
    libraryAccess: 'Aucun accès worker',
    workerLoad: 'Non applicable · serveur uniquement',
    queueStatus: 'Orchestration active',
    state: 'En ligne',
    lastSignal: 'À l’instant',
  },
  {
    id: 'worker-gpu-01',
    name: 'worker-gpu-01',
    roleFilter: '|worker|',
    roleLabel: 'Worker · NVIDIA',
    capabilities: 'FFmpeg · NVENC',
    libraryAccess: 'Films · accès direct',
    workerLoad: '2 tâches actives · 3 simultanées max.',
    queueStatus: '2 tâches compatibles en attente',
    state: 'En ligne',
    lastSignal: 'Il y a 8 s',
  },
  {
    id: 'worker-cpu-02',
    name: 'worker-cpu-02',
    roleFilter: '|worker|',
    roleLabel: 'Worker · CPU',
    capabilities: 'FFmpeg · CPU',
    libraryAccess: 'Séries · transfert WebDAV',
    workerLoad: '1 tâche active · 2 simultanées max.',
    queueStatus: '1 tâche compatible en attente',
    state: 'En ligne',
    lastSignal: 'Il y a 12 s',
  },
  {
    id: 'worker-gpu-03',
    name: 'worker-gpu-03',
    roleFilter: '|worker|',
    roleLabel: 'Worker · NVIDIA',
    capabilities: 'FFmpeg · NVENC',
    libraryAccess: 'Aucun accès configuré',
    workerLoad: 'Indisponible',
    queueStatus: 'Aucune tâche attribuée',
    state: 'Hors ligne',
    lastSignal: 'Il y a 28 min',
  },
];

const NODE_ROLE_OPTIONS: readonly { readonly value: NodeRole; readonly label: string }[] = [
  { value: 'server', label: 'Serveur' },
  { value: 'worker', label: 'Worker' },
];

@Component({
  selector: 'app-node-catalog-page',
  imports: [DataTableComponent, DetailFieldListComponent, ManagementCatalogTemplateComponent, CategoryTabsComponent],
  templateUrl: './node-catalog-page.component.html',
  styleUrl: './node-catalog-page.component.scss',
})
export class NodeCatalogPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Nœuds');
  protected readonly description = computed(
    () => (this.routeData()['description'] as string) ?? '',
  );
  protected readonly selectedRole = signal('all');
  protected readonly selectedNode = signal<NodeRecord | null>(null);
  protected readonly detailTitle = computed(
    () => this.selectedNode()?.name ?? 'Configurer un nœud',
  );
  protected readonly detailDescription = computed(
    () => this.selectedNode()?.roleLabel ?? 'Configuration du nœud',
  );
  protected readonly detailFields = computed<readonly DetailField[]>(() => {
    const node = this.selectedNode();
    return node ? [
      { section: 'Nœud', label: 'Rôle', value: node.roleLabel },
      { section: 'Nœud', label: 'État', value: node.state },
      { section: 'Nœud', label: 'Dernier signal', value: node.lastSignal },
      { section: 'Capacités worker', label: 'Outils et accélération', value: node.capabilities },
      { section: 'Capacités worker', label: 'Charge', value: node.workerLoad },
      { section: 'Capacités worker', label: 'File compatible', value: node.queueStatus },
      { section: 'Accès aux bibliothèques', label: 'Bibliothèques accessibles', value: node.libraryAccess },
    ] : [];
  });

  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom du nœud', sortable: true },
    { key: 'roleLabel', label: 'Rôle', sortable: true },
    { key: 'capabilities', label: 'Capacités', sortable: true },
    { key: 'libraryAccess', label: 'Accès aux bibliothèques', sortable: true },
    { key: 'state', label: 'État', sortable: true },
    { key: 'lastSignal', label: 'Dernier signal', sortable: true },
  ];

  protected readonly roleOptions = computed<readonly CategoryTabOption[]>(() => [
    { value: 'all', label: 'Tous', count: NODE_RECORDS.length },
    ...NODE_ROLE_OPTIONS.map((role) => ({
      ...role,
      count: NODE_RECORDS.filter((node) => node.roleFilter.includes(`|${role.value}|`)).length,
    })),
  ]);

  protected readonly filteredNodes = computed(() => {
    const role = this.selectedRole();
    return NODE_RECORDS.filter((node) => role === 'all' || node.roleFilter.includes(`|${role}|`));
  });

  protected onRoleChange(role: string): void {
    this.selectedRole.set(role);
  }

  protected onNodeOpen(row: DataTableRow): void {
    const selectedNode = NODE_RECORDS.find((node) => node.id === row['id']);
    this.selectedNode.set(selectedNode ?? null);
  }

  protected onDetailClosed(): void {
    this.selectedNode.set(null);
  }
}
