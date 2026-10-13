import { Component, computed, signal } from '@angular/core';
import {
  DataTableActionEvent,
  DataTableColumn,
  DataTableRow,
  DataTableRowAction,
  DataTableSelectionAction,
  DataTableSelectionActionEvent,
} from '../../shared/ui/data-table.component';
import {
  ExecutionMonitorTemplateComponent,
  ExecutionMonitorView,
} from '../../shared/templates/execution-monitor-template.component';

interface ExecutionRecord extends DataTableRow {
  readonly id: string;
  readonly media: string;
  readonly library: string;
  readonly flow: string;
  readonly node: string;
  readonly state: string;
  readonly group: ExecutionMonitorView;
  readonly detail: string;
  readonly queuedAt: string;
  readonly startedAt: string;
  readonly finishedAt: string;
  readonly duration: string;
  readonly attempt: number;
}

const INITIAL_EXECUTIONS: readonly ExecutionRecord[] = [
  {
    id: 'exec-1006',
    media: 'Dune — Deuxième partie (2024).mkv',
    library: 'Films',
    flow: 'Films — H.265 · v1',
    node: 'worker-gpu-01',
    state: 'En cours',
    group: 'active',
    detail: 'Encodage vidéo · étape 3 sur 5',
    queuedAt: 'Aujourd’hui, 10:40',
    startedAt: 'Aujourd’hui, 10:42',
    finishedAt: '—',
    duration: '12 min',
    attempt: 1,
  },
  {
    id: 'exec-1005',
    media: 'The Bear — Saison 3 — Épisode 4.mkv',
    library: 'Séries',
    flow: 'Séries — H.265 · v3',
    node: '—',
    state: 'En attente',
    group: 'queue',
    detail: 'En attente d’un worker compatible',
    queuedAt: 'Aujourd’hui, 10:38',
    startedAt: '—',
    finishedAt: '—',
    duration: '—',
    attempt: 1,
  },
  {
    id: 'exec-1004',
    media: 'Blade Runner 2049 (2017).mkv',
    library: 'Films',
    flow: 'Films — H.265 · v1',
    node: '—',
    state: 'Bloquée',
    group: 'queue',
    detail: 'Accès à la bibliothèque indisponible sur les nœuds éligibles',
    queuedAt: 'Aujourd’hui, 09:51',
    startedAt: '—',
    finishedAt: '—',
    duration: '—',
    attempt: 1,
  },
  {
    id: 'exec-1003',
    media: 'Arrival (2016).mkv',
    library: 'Films',
    flow: 'Films — H.265 · v1',
    node: 'worker-gpu-01',
    state: 'Réussie',
    group: 'history',
    detail: 'Exécution terminée avec succès',
    queuedAt: 'Aujourd’hui, 08:14',
    startedAt: 'Aujourd’hui, 08:16',
    finishedAt: 'Aujourd’hui, 08:34',
    duration: '18 min',
    attempt: 1,
  },
  {
    id: 'exec-1002',
    media: 'Oppenheimer (2023).mkv',
    library: 'Films',
    flow: 'Films — H.265 · v1',
    node: 'worker-gpu-02',
    state: 'Échec',
    group: 'errors',
    detail: 'Échec à l’étape de validation de la sortie',
    queuedAt: 'Aujourd’hui, 07:38',
    startedAt: 'Aujourd’hui, 07:40',
    finishedAt: 'Aujourd’hui, 07:53',
    duration: '13 min',
    attempt: 1,
  },
  {
    id: 'exec-1001',
    media: 'Interstellar (2014).mkv',
    library: 'Films',
    flow: 'Films — H.265 · v1',
    node: 'worker-cpu-02',
    state: 'Annulée',
    group: 'cancelled',
    detail: 'Annulée par un opérateur',
    queuedAt: 'Hier, 22:10',
    startedAt: 'Hier, 22:12',
    finishedAt: 'Hier, 22:15',
    duration: '3 min',
    attempt: 1,
  },
];

@Component({
  selector: 'app-task-monitoring-page',
  imports: [ExecutionMonitorTemplateComponent],
  templateUrl: './task-monitoring-page.component.html',
})
export class TaskMonitoringPageComponent {
  protected readonly executions = signal<readonly ExecutionRecord[]>(INITIAL_EXECUTIONS);
  protected readonly selectedView = signal<ExecutionMonitorView>('queue');
  protected readonly feedback = signal('');

  protected readonly queueExecutions = computed(() =>
    this.executions().filter((execution) => execution.group === 'queue'),
  );
  protected readonly activeExecutions = computed(() =>
    this.executions().filter((execution) => execution.group === 'active'),
  );
  protected readonly errorExecutions = computed(() =>
    this.executions().filter((execution) => execution.group === 'errors'),
  );
  protected readonly cancelledExecutions = computed(() =>
    this.executions().filter((execution) => execution.group === 'cancelled'),
  );
  protected readonly historyExecutions = computed(() =>
    this.executions().filter((execution) => execution.group === 'history'),
  );
  protected readonly countText = computed(
    () =>
      `${this.queueExecutions().length} à traiter · ${this.activeExecutions().length} en cours · ${this.errorExecutions().length} erreurs`,
  );

  protected readonly queueColumns: readonly DataTableColumn[] = [
    { key: 'media', label: 'Média', sortable: true },
    { key: 'library', label: 'Bibliothèque', sortable: true },
    { key: 'flow', label: 'Flow', sortable: true },
    { key: 'state', label: 'État', sortable: true },
    { key: 'detail', label: 'Disponibilité', sortable: true },
    { key: 'queuedAt', label: 'Ajoutée à la file', sortable: true },
  ];
  protected readonly activeColumns: readonly DataTableColumn[] = [
    { key: 'media', label: 'Média', sortable: true },
    { key: 'library', label: 'Bibliothèque', sortable: true },
    { key: 'flow', label: 'Flow', sortable: true },
    { key: 'node', label: 'Worker', sortable: true },
    { key: 'detail', label: 'Étape actuelle', sortable: true },
    { key: 'startedAt', label: 'Démarrée', sortable: true },
  ];
  protected readonly errorColumns: readonly DataTableColumn[] = [
    { key: 'media', label: 'Média', sortable: true },
    { key: 'flow', label: 'Flow', sortable: true },
    { key: 'node', label: 'Worker', sortable: true },
    { key: 'detail', label: 'Erreur', sortable: true },
    { key: 'finishedAt', label: 'Échouée le', sortable: true },
  ];
  protected readonly cancelledColumns: readonly DataTableColumn[] = [
    { key: 'media', label: 'Média', sortable: true },
    { key: 'flow', label: 'Flow', sortable: true },
    { key: 'node', label: 'Worker', sortable: true },
    { key: 'detail', label: 'Détail', sortable: true },
    { key: 'finishedAt', label: 'Annulée le', sortable: true },
    { key: 'duration', label: 'Durée', sortable: true },
  ];
  protected readonly historyColumns: readonly DataTableColumn[] = [
    { key: 'media', label: 'Média', sortable: true },
    { key: 'library', label: 'Bibliothèque', sortable: true },
    { key: 'flow', label: 'Flow', sortable: true },
    { key: 'node', label: 'Worker', sortable: true },
    { key: 'finishedAt', label: 'Réussie le', sortable: true },
    { key: 'duration', label: 'Durée', sortable: true },
  ];
  protected readonly errorActions: readonly DataTableRowAction[] = [
    {
      id: 'retry',
      label: 'Relancer',
      confirmation: (row) => ({
        title: 'Relancer cette exécution ?',
        message: `Une nouvelle exécution de « ${row['media']} » sera ajoutée à la file.`,
        confirmLabel: 'Relancer',
      }),
    },
  ];
  protected readonly selectionActions: readonly DataTableSelectionAction[] = [
    {
      id: 'cancel-selected',
      label: 'Annuler les exécutions',
      visibleWhen: (rows) => rows.every((row) => row['group'] === 'queue' || row['group'] === 'active'),
      confirmation: (rows) => ({
        title: 'Annuler les exécutions sélectionnées ?',
        message: `Les ${rows.length} exécutions sélectionnées seront annulées.`,
        confirmLabel: 'Annuler les exécutions',
      }),
    },
    {
      id: 'retry-selected',
      label: 'Relancer les exécutions',
      visibleWhen: (rows) => rows.every((row) => row['group'] === 'errors'),
      confirmation: (rows) => ({
        title: 'Relancer les exécutions sélectionnées ?',
        message: `Une nouvelle tentative sera ajoutée à la file pour ${rows.length} exécution${rows.length > 1 ? 's' : ''}.`,
        confirmLabel: 'Relancer',
      }),
    },
  ];

  protected onViewChange(view: ExecutionMonitorView): void {
    this.selectedView.set(view);
  }

  protected onErrorAction(event: DataTableActionEvent): void {
    if (event.actionId !== 'retry') {
      return;
    }

    const failedExecution = this.executions().find((execution) => execution.id === event.row['id']);
    if (!failedExecution || failedExecution.group !== 'errors') {
      return;
    }

    this.executions.update((executions) => [this.createRetry(failedExecution), ...executions]);
    this.selectedView.set('queue');
    this.feedback.set(
      `Relance de « ${failedExecution.media} » ajoutée à la file de démonstration. Aucun worker n’a été sollicité.`,
    );
  }

  protected onSelectionAction(event: DataTableSelectionActionEvent): void {
    const selectedIds = new Set(event.rows.map((row) => String(row['id'])));
    const selected = this.executions().filter((execution) => selectedIds.has(execution.id));
    if (!selected.length || selected.length !== selectedIds.size) return;

    if (event.actionId === 'cancel-selected' && selected.every((execution) => execution.group === 'queue' || execution.group === 'active')) {
      this.executions.update((executions) => executions.map((execution) => selectedIds.has(execution.id)
        ? { ...execution, state: 'Annulée', group: 'cancelled', detail: 'Annulée par un opérateur', finishedAt: 'À l’instant' }
        : execution));
      this.selectedView.set('cancelled');
      this.feedback.set(`${selected.length} exécution${selected.length > 1 ? 's' : ''} annulée${selected.length > 1 ? 's' : ''} dans la file de démonstration.`);
      return;
    }

    if (event.actionId === 'retry-selected' && selected.every((execution) => execution.group === 'errors')) {
      const retries = selected.map((execution) => this.createRetry(execution));
      this.executions.update((executions) => [...retries, ...executions]);
      this.selectedView.set('queue');
      this.feedback.set(`${retries.length} relance${retries.length > 1 ? 's' : ''} ajoutée${retries.length > 1 ? 's' : ''} à la file de démonstration. Aucun worker n’a été sollicité.`);
    }
  }

  private createRetry(failedExecution: ExecutionRecord): ExecutionRecord {
    return {
      ...failedExecution,
      id: `${failedExecution.id}-retry-${failedExecution.attempt + 1}`,
      state: 'En attente',
      group: 'queue',
      node: '—',
      detail: 'En attente d’un worker compatible',
      queuedAt: 'À l’instant',
      startedAt: '—',
      finishedAt: '—',
      duration: '—',
      attempt: failedExecution.attempt + 1,
    };
  }
}
