import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import { EXECUTION_STATE_LABELS } from '../../shared/models/execution';
import { DatePipe } from '@angular/common';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { toSignal } from '@angular/core/rxjs-interop';
import { ClusterOverview, INITIAL_CLUSTER_OVERVIEW } from '../../shared/services/cluster-overview';
import {
  assessClusterHealth,
  NODE_LIFECYCLE_LABELS,
  NODE_LIFECYCLE_TONES,
  nodeLifecycle,
} from '../../shared/models/cluster-health';
import { HealthBannerComponent } from '../../shared/ui/health-banner.component';
import { MatrixCell, MatrixTableComponent } from '../../shared/ui/matrix-table.component';
import { formatNodeLabel, NodeResponse, NodeRole, NODE_ROLE_LABELS } from '../../shared/models/cluster';
import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { Router, RouterLink } from '@angular/router';
import { DetailDrawerComponent } from '../../shared/ui/detail-drawer/detail-drawer';
import { MetricCardComponent } from '../../shared/ui/metric-card.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { RecordFormComponent } from '../../shared/ui/record-form.component';
import { StatusBadgeComponent } from '../../shared/ui/status-badge.component';
import { ExecutionCardComponent } from '../../shared/ui/execution-card.component';

@Component({
  selector: 'app-home-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatProgressBarModule,
    DatePipe,
    DetailDrawerComponent,
    ExecutionCardComponent,
    HealthBannerComponent,
    MatrixTableComponent,
    MetricCardComponent,
    NgTemplateOutlet,
    PageHeaderComponent,
    RecordFormComponent,
    RouterLink,
    StatusBadgeComponent,
  ],
  templateUrl: './home-page.component.html',
  styleUrl: './home-page.component.scss',
})
export class HomePageComponent {
  private readonly router = inject(Router);
  protected readonly flowActionOpen = signal(false);
  protected readonly flowCreationForm = signal<
    import('../../shared/ui/record-form.component').RecordFormData | null
  >(null);
  protected readonly pageActionsTemplate = viewChild<TemplateRef<unknown>>('pageActions');
  protected readonly flowFormTemplate = viewChild<TemplateRef<unknown>>('flowForm');
  protected readonly homeContentTemplate = viewChild<TemplateRef<unknown>>('homeContent');

  protected async openFlowCreation(): Promise<void> {
    this.flowActionOpen.set(true);
    const { FLOW_CREATION_FORM } = await import('../../shared/models/flow-creation');
    this.flowCreationForm.set(FLOW_CREATION_FORM);
  }

  protected closeFlowCreation(): void {
    this.flowActionOpen.set(false);
  }

  protected startFlow(values: Record<string, string>): void {
    void this.router.navigate(['/flows/new'], {
      queryParams: {
        name: values['name'],
        mediaType: values['mediaType'],
        starter: values['starter'],
      },
      info: { flowEditorDirection: 'forward' },
    });
    this.flowActionOpen.set(false);
  }

  private readonly executionTracking = inject(ExecutionTracking);
  protected readonly executionState = toSignal(this.executionTracking.state$, {
    initialValue: INITIAL_EXECUTION_TRACKING,
  });
  protected readonly executionLabels = EXECUTION_STATE_LABELS;
  protected readonly activeTasks = computed(
    () =>
      this.executionState().data?.executions?.filter(
        (e) => e.state === 'running' || e.state === 'cancelling',
      ) ?? [],
  );
  protected readonly recentExecutions = computed(() => {
    const executions = this.executionState().data?.executions ?? [];
    return executions
      .slice()
      .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
      .slice(0, 5);
  });
  private readonly executionMetrics = computed(() => {
    const data = this.executionState().data;
    return [
      {
        label: 'Exécutions en cours',
        value: data ? String(data.activeCount) : '—',
        detail: this.standalone()
          ? 'Traitements enregistrés sur ce nœud'
          : 'Traitements enregistrés dans le cluster',
      },
      {
        label: 'Dans la file',
        value: data ? String(data.queuedCount) : '—',
        detail: 'Exécutions en attente',
      },
      {
        label: 'Terminées avec succès',
        value: data ? String(data.succeededCount) : '—',
        detail: 'Exécutions publiées',
      },
      {
        label: 'En échec',
        value: data ? String(data.failedCount) : '—',
        detail: 'Exécutions à examiner',
      },
      {
        label: 'En attente de reprise',
        value: data?.deferredCount === undefined ? '—' : String(data.deferredCount),
        detail: 'Exécutions suspendues',
      },
    ];
  });
  private readonly overview = inject(ClusterOverview);
  protected readonly state = toSignal(this.overview.state$, {
    initialValue: INITIAL_CLUSTER_OVERVIEW,
  });
  protected readonly standalone = computed(
    () => this.state().snapshot?.identity.standalone ?? false,
  );
  protected readonly observedAt = computed(() => this.state().snapshot?.observedAt ?? null);
  protected readonly metrics = computed(() => {
    const snapshot = this.state().snapshot;
    if (!snapshot)
      return [
        { label: 'État de l’installation', value: '—', detail: 'Données indisponibles' },
        { label: 'Worker local', value: '—', detail: 'Données indisponibles' },
        { label: 'Bibliothèques configurées', value: '—', detail: 'Données indisponibles' },
        ...this.executionMetrics(),
      ];
    const online = snapshot.nodes.filter((n) => n.status === 'online');
    const workers = snapshot.nodes.filter((n) => n.roles.includes('worker'));
    const onlineWorkers = online.filter((n) => n.roles.includes('worker'));
    const standalone = snapshot.identity.standalone;
    return [
      {
        label: standalone ? 'Ce nœud' : 'Nœuds en ligne',
        value: standalone
          ? online.length > 0
            ? 'En ligne'
            : 'Hors ligne'
          : online.length + ' / ' + snapshot.nodes.length,
        detail: standalone
          ? (snapshot.nodes[0] ? formatNodeLabel(snapshot.nodes[0].name, snapshot.nodes[0].displayName) : 'État local')
          : snapshot.nodes.length - online.length + ' hors ligne',
      },
      {
        label: standalone ? 'Worker local' : 'Workers en ligne',
        value: standalone
          ? workers.length === 0
            ? 'Non configuré'
            : onlineWorkers.length > 0
              ? 'En ligne'
              : 'Hors ligne'
          : onlineWorkers.length + ' / ' + workers.length,
        detail: standalone
          ? 'Fonction de traitement sur cette installation'
          : 'Présence observée, disponibilité de traitement non mesurée',
      },
      {
        label: 'Bibliothèques actives',
        value:
          snapshot.libraries.filter((l) => l.isActive).length + ' / ' + snapshot.libraries.length,
        detail: standalone ? 'Configuration locale' : 'Configuration enregistrée dans le cluster',
      },
      ...this.executionMetrics(),
    ];
  });
  protected readonly nodes = computed(() => this.state().snapshot?.nodes ?? []);
  protected readonly health = computed(() => {
    const snapshot = this.state().snapshot;
    return snapshot
      ? assessClusterHealth(
          snapshot.nodes,
          snapshot.libraries,
          snapshot.accesses,
          snapshot.identity.standalone,
        )
      : null;
  });
  protected readonly matrixRows = computed(() =>
    this.nodes().map((node) => ({ id: node.id, label: formatNodeLabel(node.name, node.displayName) })),
  );
  protected readonly matrixColumns = computed(() =>
    (this.state().snapshot?.libraries ?? []).map((library) => ({
      id: library.id,
      label: library.name,
    })),
  );
  protected readonly matrixCells = computed(() => {
    const cells: Record<string, MatrixCell> = {};
    for (const access of this.state().snapshot?.accesses ?? []) {
      cells[access.nodeId + '|' + access.libraryId] = !access.isEnabled
        ? { label: 'Désactivé', tone: 'neutral' }
        : access.mode === 'direct'
          ? { label: 'Accès direct', tone: 'success' }
          : access.mode === 'transfer'
            ? { label: 'Transfert', tone: 'accent' }
            : { label: 'Non pris en charge', tone: 'neutral' };
    }
    return cells;
  });
  protected lifecycleLabel(node: NodeResponse): string {
    return NODE_LIFECYCLE_LABELS[nodeLifecycle(node)];
  }
  protected lifecycleTone(node: NodeResponse) {
    return NODE_LIFECYCLE_TONES[nodeLifecycle(node)];
  }
  protected roleLabel(roles: readonly NodeRole[]): string {
    return roles.map((r) => NODE_ROLE_LABELS[r]).join(' · ');
  }
  protected refresh(): void {
    this.overview.refresh();
    this.executionTracking.refresh();
  }
}
