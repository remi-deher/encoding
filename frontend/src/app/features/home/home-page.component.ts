import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import { EXECUTION_STATE_LABELS } from '../../shared/models/execution';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { DatePipe } from '@angular/common';
import { toSignal } from '@angular/core/rxjs-interop';
import { ClusterOverview, INITIAL_CLUSTER_OVERVIEW } from '../../shared/services/cluster-overview';
import { NodeRole, NODE_ROLE_LABELS } from '../../shared/models/cluster';
import { Component, TemplateRef, computed, inject, signal, viewChild } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { Router, RouterLink } from '@angular/router';
import { DetailDrawerComponent } from '../../shared/ui/detail-drawer/detail-drawer';
import { MetricCardComponent } from '../../shared/ui/metric-card.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { RecordFormComponent } from '../../shared/ui/record-form.component';
import { StatusBadgeComponent } from '../../shared/ui/status-badge.component';

@Component({
  selector: 'app-home-page',
  imports: [
    MatButtonModule,
    MatProgressBarModule,
    DatePipe,
    DetailDrawerComponent,
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
      this.executionState().data?.executions.filter(
        (e) => e.state === 'running' || e.state === 'cancelling',
      ) ?? [],
  );
  protected readonly activity = computed(
    () => this.executionState().data?.activity.slice(0, 5) ?? [],
  );
  private readonly executionMetrics = computed(() => {
    const data = this.executionState().data;
    return [
      {
        label: 'Exécutions en cours',
        value: data ? String(data.activeCount) : '—',
        detail: 'Traitements enregistrés dans le cluster',
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
  protected readonly observedAt = computed(() => this.state().snapshot?.observedAt ?? null);
  protected readonly metrics = computed(() => {
    const snapshot = this.state().snapshot;
    if (!snapshot)
      return [
        { label: 'Nœuds en ligne', value: '—', detail: 'Données indisponibles' },
        { label: 'Workers en ligne', value: '—', detail: 'Données indisponibles' },
        { label: 'Bibliothèques actives', value: '—', detail: 'Données indisponibles' },
        { label: 'Espace économisé', value: '—', detail: 'Mesure non disponible' },
        ...this.executionMetrics(),
      ];
    const online = snapshot.nodes.filter((n) => n.status === 'online');
    const workers = snapshot.nodes.filter((n) => n.roles.includes('worker'));
    return [
      {
        label: 'Nœuds en ligne',
        value: online.length + ' / ' + snapshot.nodes.length,
        detail: snapshot.nodes.length - online.length + ' hors ligne',
      },
      {
        label: 'Workers en ligne',
        value: online.filter((n) => n.roles.includes('worker')).length + ' / ' + workers.length,
        detail: 'Présence observée, disponibilité de traitement non mesurée',
      },
      {
        label: 'Bibliothèques actives',
        value:
          snapshot.libraries.filter((l) => l.isActive).length + ' / ' + snapshot.libraries.length,
        detail: 'Configuration enregistrée dans le cluster',
      },
      { label: 'Espace économisé', value: '—', detail: 'Mesure non disponible' },
      ...this.executionMetrics(),
    ];
  });
  protected readonly nodes = computed(() => this.state().snapshot?.nodes ?? []);
  protected roleLabel(roles: readonly NodeRole[]): string {
    return roles.map((r) => NODE_ROLE_LABELS[r]).join(' · ');
  }
  protected refresh(): void {
    this.overview.refresh();
    this.executionTracking.refresh();
  }
}
