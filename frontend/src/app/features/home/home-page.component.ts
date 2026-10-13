import { Component, TemplateRef, inject, signal, viewChild } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { DetailDrawerComponent } from '../../shared/ui/detail-drawer/detail-drawer';
import { MetricCardComponent } from '../../shared/ui/metric-card.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { RecordFormComponent } from '../../shared/ui/record-form.component';
import { StatusBadgeComponent, StatusTone } from '../../shared/ui/status-badge.component';

interface ActiveTask {
  readonly title: string;
  readonly library: string;
  readonly node: string;
  readonly progress: number;
  readonly eta: string;
}

interface NodeSummary {
  readonly name: string;
  readonly role: string;
  readonly state: string;
  readonly tone: StatusTone;
  readonly workload: string;
}

@Component({
  selector: 'app-home-page',
  imports: [
    MatButtonModule,
    MatProgressBarModule,
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
  protected readonly flowCreationForm = signal<import('../../shared/ui/record-form.component').RecordFormData | null>(null);
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
      queryParams: { name: values['name'], mediaType: values['mediaType'], starter: values['starter'] },
      info: { flowEditorDirection: 'forward' },
    });
    this.flowActionOpen.set(false);
  }

  protected readonly metrics = [
    { label: 'Nœuds disponibles', value: '3 / 4', detail: '1 nœud nécessite votre attention' },
    { label: 'Encodages actifs', value: '4', detail: 'Sur 6 workers configurés' },
    { label: 'Dans la file', value: '18', detail: '2 bibliothèques concernées' },
    { label: 'Espace économisé', value: '2,4 To', detail: 'Depuis la mise en service' },
  ] as const;

  protected readonly activeTasks: readonly ActiveTask[] = [
    {
      title: 'The Expanse — S03E04',
      library: 'Séries',
      node: 'worker-gpu-01',
      progress: 72,
      eta: '04 min restantes',
    },
    {
      title: 'Dune: Part Two (2024)',
      library: 'Films',
      node: 'worker-cpu-02',
      progress: 46,
      eta: '18 min restantes',
    },
    {
      title: 'Blue Planet II — E01',
      library: 'Documentaires',
      node: 'worker-gpu-02',
      progress: 23,
      eta: '11 min restantes',
    },
  ];

  protected readonly nodes: readonly NodeSummary[] = [
    {
      name: 'media-core-01',
      role: 'Serveur · Worker',
      state: 'En ligne',
      tone: 'success',
      workload: '1 tâche',
    },
    {
      name: 'worker-gpu-01',
      role: 'Worker · NVIDIA',
      state: 'En ligne',
      tone: 'success',
      workload: '2 tâches',
    },
    {
      name: 'worker-cpu-02',
      role: 'Worker · CPU',
      state: 'En ligne',
      tone: 'success',
      workload: '1 tâche',
    },
    {
      name: 'worker-gpu-03',
      role: 'Worker · NVIDIA',
      state: 'Hors ligne',
      tone: 'warning',
      workload: 'Inactif',
    },
  ];
}
