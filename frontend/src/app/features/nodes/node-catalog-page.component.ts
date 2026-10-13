import { HttpClient } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { catchError, EMPTY, switchMap, timer } from 'rxjs';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import {
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
} from '../../shared/ui/data-table.component';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { DetailField, DetailFieldListComponent } from '../../shared/ui/detail-field-list.component';
import { GuideDialogComponent, GuideDialogData } from '../../shared/ui/guide-dialog.component';

type NodeRole = 'server' | 'worker';
type NodeStatus = 'online' | 'offline';

interface NodeToolCapability {
  readonly toolId: string;
  readonly version: string;
  readonly mediaTypes: readonly string[];
  readonly accelerationTypes: readonly string[];
}

interface NodeResponse {
  readonly id: string;
  readonly name: string;
  readonly roles: readonly NodeRole[];
  readonly maxConcurrentTasks: number;
  readonly capabilities: readonly NodeToolCapability[];
  readonly status: NodeStatus;
  readonly lastSeenAt: string;
}

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

const NODE_ROLE_LABELS: Readonly<Record<NodeRole, string>> = {
  server: 'Serveur',
  worker: 'Worker',
};

const NODE_ROLE_OPTIONS: readonly { readonly value: NodeRole; readonly label: string }[] = [
  { value: 'server', label: 'Serveur' },
  { value: 'worker', label: 'Worker' },
];

@Component({
  selector: 'app-node-catalog-page',
  imports: [DataTableComponent, DetailFieldListComponent, ManagementCatalogTemplateComponent, CategoryTabsComponent, MatButtonModule],
  templateUrl: './node-catalog-page.component.html',
  styleUrl: './node-catalog-page.component.scss',
})
export class NodeCatalogPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly http = inject(HttpClient);
  private readonly dialog = inject(MatDialog);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });
  private readonly nodes = signal<readonly NodeResponse[]>([]);

  protected readonly loadState = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly selectedRole = signal('all');
  protected readonly selectedNode = signal<NodeRecord | null>(null);
  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Nœuds');
  protected readonly description = computed(() => (this.routeData()['description'] as string) ?? '');
  protected readonly catalogMessage = computed(() => this.loadState() === 'error'
    ? 'Impossible de joindre l’API du cluster. Vérifiez que le backend et PostgreSQL sont démarrés.'
    : 'Chargement des nœuds du cluster…');
  protected readonly detailTitle = computed(() => this.selectedNode()?.name ?? 'Détails du nœud');
  protected readonly detailDescription = computed(() => this.selectedNode()?.roleLabel ?? 'État observé par le cluster');
  protected readonly detailFields = computed<readonly DetailField[]>(() => {
    const node = this.selectedNode();
    return node ? [
      { section: 'Nœud', label: 'Rôle', value: node.roleLabel },
      { section: 'Nœud', label: 'État', value: node.state },
      { section: 'Nœud', label: 'Dernier signal', value: node.lastSignal },
      { section: 'Capacités worker', label: 'Outils et accélération', value: node.capabilities },
      { section: 'Capacités worker', label: 'Limite simultanée', value: node.workerLoad },
      { section: 'Accès aux bibliothèques', label: 'État', value: node.libraryAccess },
    ] : [];
  });

  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom du nœud', sortable: true },
    { key: 'roleLabel', label: 'Rôle', sortable: true },
    { key: 'capabilities', label: 'Capacités observées', sortable: true },
    { key: 'libraryAccess', label: 'Accès aux bibliothèques', sortable: true },
    { key: 'state', label: 'État', sortable: true },
    { key: 'lastSignal', label: 'Dernier signal', sortable: true },
  ];

  protected readonly roleOptions = computed<readonly CategoryTabOption[]>(() => {
    const nodes = this.nodes();
    return [
      { value: 'all', label: 'Tous', count: nodes.length },
      ...NODE_ROLE_OPTIONS.map((role) => ({
        ...role,
        count: nodes.filter((node) => node.roles.includes(role.value)).length,
      })),
    ];
  });

  protected readonly filteredNodes = computed(() => {
    const role = this.selectedRole();
    return this.nodes()
      .filter((node) => role === 'all' || node.roles.includes(role as NodeRole))
      .map((node) => this.toRecord(node));
  });

  constructor() {
    timer(0, 10_000).pipe(
      switchMap(() => this.http.get<readonly NodeResponse[]>('/api/v1/nodes').pipe(
        catchError(() => {
          this.loadState.set(this.nodes().length ? 'ready' : 'error');
          return EMPTY;
        }),
      )),
      takeUntilDestroyed(),
    ).subscribe((nodes) => {
      this.nodes.set(nodes);
      this.loadState.set('ready');
    });
  }

  protected onRoleChange(role: string): void {
    this.selectedRole.set(role);
  }

  protected addNode(): void {
    const data: GuideDialogData = {
      title: 'Enrôler un nœud',
      description: 'Le serveur ajoute automatiquement une installation au cluster dès que son agent lui envoie un premier signal valide.',
      steps: [
        'Installez et démarrez l’agent Encoding sur la machine à ajouter (image Docker de développement disponible dans backend/).',
        'Configurez un identifiant UUID stable et unique, un nom, ses rôles (server, worker ou les deux) et sa limite de tâches.',
        'Indiquez l’URL de l’API du cluster, accessible depuis cette machine, ainsi que le jeton configuré côté serveur dans Cluster:NodeApiToken.',
        'Démarrez l’agent. Il envoie un signal toutes les 10 secondes ; le nouveau nœud apparaît ensuite automatiquement dans ce catalogue.',
      ],
      note: 'Le jeton est partagé dans le socle actuel : transmettez-le par un canal secret et ne le collez pas dans un ticket ou un journal. La création de jetons d’enrôlement à usage unique sera une évolution distincte.',
    };
    this.dialog.open(GuideDialogComponent, { data, width: 'min(620px, 100vw)', maxWidth: '100vw', autoFocus: 'first-tabbable', restoreFocus: true });
  }

  protected onNodeOpen(row: DataTableRow): void {
    this.selectedNode.set(this.filteredNodes().find((node) => node.id === row['id']) ?? null);
  }

  protected onDetailClosed(): void {
    this.selectedNode.set(null);
  }

  private toRecord(node: NodeResponse): NodeRecord {
    const roleLabel = node.roles.map((role) => NODE_ROLE_LABELS[role]).join(' · ');
    const capabilityLabels = node.capabilities.map((capability) => {
      const acceleration = capability.accelerationTypes.length
        ? ` · ${capability.accelerationTypes.join(', ')}`
        : '';
      return `${capability.toolId} ${capability.version}${acceleration}`;
    });
    const worker = node.roles.includes('worker');
    return {
      id: node.id,
      name: node.name,
      roleFilter: `|${node.roles.join('|')}|`,
      roleLabel,
      capabilities: capabilityLabels.length ? capabilityLabels.join(' · ') : 'Aucune capacité déclarée',
      libraryAccess: 'À configurer',
      workerLoad: worker ? `${node.maxConcurrentTasks} tâche(s) max.` : 'Non applicable',
      queueStatus: 'Non disponible',
      state: node.status === 'online' ? 'En ligne' : 'Hors ligne',
      lastSignal: this.formatLastSeen(node.lastSeenAt),
    };
  }

  private formatLastSeen(value: string): string {
    const elapsedSeconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
    if (elapsedSeconds < 5) return 'À l’instant';
    if (elapsedSeconds < 60) return `Il y a ${elapsedSeconds} s`;
    const minutes = Math.floor(elapsedSeconds / 60);
    if (minutes < 60) return `Il y a ${minutes} min`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `Il y a ${hours} h`;
    return new Intl.DateTimeFormat('fr-FR', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value));
  }
}
