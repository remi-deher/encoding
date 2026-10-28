import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ExecutionResponse, ExecutionPipelineStep, executionStateLabel } from '../../../shared/models/execution';
import { LibraryActivityFile } from '../../../shared/models/library-activity';
import { formatFileSize } from '../../../shared/models/file-size';
import { formatFullDateTime } from '../../../shared/models/date-format';
import { StatusBadgeComponent } from '../../../shared/ui/status-badge.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { NotificationService } from '../../../shared/services/notification.service';

function baseName(path: string): string {
  return path.split(/[\\/]/).pop() || path;
}

function parseSize(metadata: unknown): number | null {
  if (!metadata || typeof metadata !== 'object') return null;
  const format = (metadata as { format?: { size?: unknown } }).format;
  if (!format || format.size == null) return null;
  const num = Number(format.size);
  return Number.isFinite(num) && num > 0 ? num : null;
}

function executionDuration(execution: ExecutionResponse | null): string {
  if (!execution?.startedAt) return '—';
  const end = execution.finishedAt ? Date.parse(execution.finishedAt) : Date.now();
  const elapsed = Math.max(0, end - Date.parse(execution.startedAt));
  const seconds = Math.floor(elapsed / 1000);
  if (seconds < 60) return `${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = seconds % 60;
  if (minutes < 60) {
    return `${minutes} min ${remainingSeconds.toString().padStart(2, '0')} s`;
  }
  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;
  return `${hours} h ${remainingMinutes} min`;
}

function stepDuration(step: ExecutionPipelineStep): string {
  if (!step.startedAt) return '—';
  const end = step.completedAt ? Date.parse(step.completedAt) : Date.now();
  const elapsed = Math.max(0, end - Date.parse(step.startedAt));
  if (elapsed < 1000) return `${elapsed} ms`;
  const seconds = Math.floor(elapsed / 1000);
  if (seconds < 60) {
    const fraction = ((elapsed % 1000) / 100).toFixed(0);
    return `${seconds}.${fraction} s`;
  }
  const minutes = Math.floor(seconds / 60);
  const remainingSec = seconds % 60;
  return `${minutes} min ${remainingSec} s`;
}

@Component({
  selector: 'app-activity-execution-summary',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CommonModule,
    MatButtonModule,
    MatTooltipModule,
    StatusBadgeComponent,
    IconComponent,
  ],
  template: `
    <div class="summary-container">
      @if (loading()) {
        <p class="summary-loading" role="status">Chargement des détails de l'exécution…</p>
      } @else {
        <!-- Connected M3 Container Cards -->
        <div class="summary-cards-grid">
          <!-- Carte 1: Détails -->
          <div class="summary-card">
            <div class="card-header">
              <span class="card-icon"><app-icon name="info" /></span>
              <h4 class="card-title">Détails</h4>
            </div>
            <div class="card-rows">
              <div class="card-row">
                <span class="row-label">Fichier</span>
                <span class="row-value file-value" [title]="fullPath()">{{ fileName() }}</span>
              </div>
              <div class="card-row">
                <span class="row-label">Bibliothèque</span>
                <span class="row-value">{{ libraryName() }}</span>
              </div>
              <div class="card-row">
                <span class="row-label">Flow</span>
                <span class="row-value">{{ flowDescription() }}</span>
              </div>
              @if (execution(); as exec) {
                <div class="card-row">
                  <span class="row-label">Identifiant</span>
                  <div class="row-value-with-action">
                    <span class="row-code" [title]="exec.id">{{ exec.id.slice(0, 8) }}…</span>
                    <button
                      type="button"
                      class="mini-copy-btn"
                      (click)="copy(exec.id, 'Identifiant d’exécution')"
                      title="Copier l'identifiant"
                      aria-label="Copier l'identifiant"
                    >
                      <app-icon name="copy" />
                    </button>
                  </div>
                </div>
              }
            </div>
          </div>

          <!-- Carte 2: Traitement -->
          <div class="summary-card">
            <div class="card-header">
              <span class="card-icon"><app-icon name="play" /></span>
              <h4 class="card-title">Traitement</h4>
            </div>
            <div class="card-rows">
              <div class="card-row">
                <span class="row-label">Statut</span>
                <span class="row-value">
                  @if (execution(); as exec) {
                    <app-status-badge
                      [label]="executionStateLabel(exec)"
                      [tone]="stateTone(exec.state)"
                    />
                  } @else {
                    <span class="text-muted">—</span>
                  }
                </span>
              </div>
              <div class="card-row">
                <span class="row-label">Nœud / Worker</span>
                <span class="row-value">{{ workerName() }}</span>
              </div>
              <div class="card-row">
                <span class="row-label">Durée totale</span>
                <span class="row-value duration-value">{{ totalDuration() }}</span>
              </div>
              <div class="card-row">
                <span class="row-label">Progression</span>
                <span class="row-value">
                  @if (execution()?.progress != null) {
                    {{ execution()!.progress }} %
                  } @else {
                    —
                  }
                </span>
              </div>
            </div>
          </div>

          <!-- Carte 3: Stockage & Gain -->
          <div class="summary-card">
            <div class="card-header">
              <span class="card-icon"><app-icon name="library" /></span>
              <h4 class="card-title">Stockage & Gain</h4>
            </div>
            <div class="card-rows">
              <div class="card-row">
                <span class="row-label">Taille source</span>
                <span class="row-value">{{ sourceSize() }}</span>
              </div>
              <div class="card-row">
                <span class="row-label">Taille finale</span>
                <span class="row-value">{{ finalSize() }}</span>
              </div>
              <div class="card-row">
                <span class="row-label">Variation</span>
                <span class="row-value">
                  @if (sizeDifference(); as diff) {
                    <span
                      class="diff-badge"
                      [class.diff-reduced]="diff.reduced"
                      [class.diff-expanded]="diff.expanded"
                      [class.diff-neutral]="diff.neutral"
                    >
                      {{ diff.label }} ({{ diff.percent }})
                    </span>
                  } @else {
                    <span class="text-muted">—</span>
                  }
                </span>
              </div>
              @if (execution()?.publishedPath) {
                <div class="card-row">
                  <span class="row-label">Sortie</span>
                  <div class="row-value-with-action">
                    <span class="row-value published-value" [title]="execution()!.publishedPath!">
                      {{ baseName(execution()!.publishedPath!) }}
                    </span>
                    <button
                      type="button"
                      class="mini-copy-btn"
                      (click)="copy(execution()!.publishedPath!, 'Emplacement publié')"
                      title="Copier le chemin publié"
                      aria-label="Copier le chemin publié"
                    >
                      <app-icon name="copy" />
                    </button>
                  </div>
                </div>
              }
            </div>
          </div>
        </div>

        <!-- Section: Éléments de Flow exécutés -->
        <section class="summary-steps-section" aria-label="Éléments de Flow exécutés">
          <div class="steps-header">
            <h3>Éléments de Flow exécutés</h3>
            <span class="steps-count-badge">{{ steps().length }}</span>
          </div>

          @if (steps().length === 0) {
            <p class="steps-empty">Aucun élément de flow exécuté pour le moment.</p>
          } @else {
            <div class="steps-table-wrapper">
              <table class="steps-table">
                <thead>
                  <tr>
                    <th class="col-num" scope="col">#</th>
                    <th class="col-node" scope="col">Étape</th>
                    <th class="col-output" scope="col">Sortie / Décision</th>
                    <th class="col-worker" scope="col">Worker</th>
                    <th class="col-duration" scope="col">Durée</th>
                    <th class="col-status" scope="col">Statut</th>
                  </tr>
                </thead>
                <tbody>
                  @for (step of steps(); track step.invocationId || step.nodeId + $index; let idx = $index) {
                    <tr [class.is-failed]="step.status === 'failed'">
                      <td class="col-num">{{ idx + 1 }}</td>
                      <td class="col-node">
                        <div class="node-cell">
                          <span class="node-name">{{ step.nodeName || step.nodeId }}</span>
                          <span class="node-kind">{{ kindLabel(step.componentKind) }}</span>
                        </div>
                      </td>
                      <td class="col-output">
                        @if (step.outputLabel) {
                          <span class="output-pill">{{ step.outputLabel }}</span>
                        } @else if (step.outputId) {
                          <span class="output-pill">{{ step.outputId }}</span>
                        } @else {
                          <span class="text-muted">—</span>
                        }
                      </td>
                      <td class="col-worker">{{ workerName() }}</td>
                      <td class="col-duration">{{ formatStepDuration(step) }}</td>
                      <td class="col-status">
                        <app-status-badge
                          [label]="stepStatusLabel(step.status)"
                          [tone]="stepStatusTone(step.status)"
                        />
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
    }

    .summary-container {
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
      padding: var(--space-3) 0;
    }

    .summary-loading {
      color: var(--text-secondary);
      font-size: var(--font-body);
      margin: var(--space-3) 0;
    }

    .summary-cards-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
      gap: var(--space-3);
    }

    .summary-card {
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
      padding: var(--space-4);
      border-radius: var(--radius-xl);
      background: var(--surface-container);
      box-shadow: var(--elevation-1);
      transition: background-color var(--motion-spring-fast);

      &:hover {
        background: var(--surface-container-high);
      }
    }

    .card-header {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      color: var(--accent-strong);
    }

    .card-icon {
      display: grid;
      place-items: center;
      width: 28px;
      height: 28px;
      border-radius: var(--radius-md);
      background: var(--surface-container-lowest);
      color: var(--accent);
    }

    .card-title {
      margin: 0;
      color: var(--text-primary);
      font-size: var(--font-body);
      font-weight: 700;
      letter-spacing: 0.02em;
    }

    .card-rows {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }

    .card-row {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
      gap: var(--space-2);
      font-size: var(--font-label);
    }

    .row-label {
      color: var(--text-muted);
      font-weight: 500;
      flex: 0 0 auto;
    }

    .row-value {
      color: var(--text-primary);
      font-weight: 600;
      text-align: end;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .file-value {
      max-width: 220px;
    }

    .published-value {
      max-width: 180px;
    }

    .row-value-with-action {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      justify-content: flex-end;
    }

    .row-code {
      font-family: monospace;
      font-size: var(--font-label);
      color: var(--text-secondary);
      background: var(--surface-container-lowest);
      padding: 1px 6px;
      border-radius: var(--radius-xs);
    }

    .mini-copy-btn {
      display: inline-grid;
      place-items: center;
      width: 24px;
      height: 24px;
      border: 0;
      background: transparent;
      color: var(--text-muted);
      border-radius: var(--radius-xs);
      cursor: pointer;
      padding: 0;

      &:hover {
        color: var(--accent-strong);
        background: var(--surface-container-highest);
      }
    }

    .duration-value {
      font-variant-numeric: tabular-nums;
      color: var(--accent-strong);
      font-weight: 700;
    }

    .diff-badge {
      display: inline-flex;
      align-items: center;
      padding: 2px 8px;
      border-radius: var(--radius-pill);
      font-size: var(--font-label);
      font-weight: 700;
      font-variant-numeric: tabular-nums;
    }

    .diff-reduced {
      background: var(--success-soft);
      color: var(--success);
    }

    .diff-expanded {
      background: var(--warning-soft);
      color: var(--warning);
    }

    .diff-neutral {
      background: var(--surface-container-lowest);
      color: var(--text-secondary);
    }

    .text-muted {
      color: var(--text-muted);
    }

    /* Steps table */
    .summary-steps-section {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      margin-top: var(--space-2);
    }

    .steps-header {
      display: flex;
      align-items: center;
      gap: var(--space-2);

      h3 {
        margin: 0;
        font-size: var(--font-body);
        font-weight: 700;
        color: var(--text-primary);
      }
    }

    .steps-count-badge {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-width: 22px;
      height: 22px;
      padding: 0 6px;
      border-radius: var(--radius-pill);
      background: var(--surface-container);
      color: var(--text-secondary);
      font-size: var(--font-label);
      font-weight: 700;
    }

    .steps-empty {
      color: var(--text-muted);
      font-size: var(--font-label);
      padding: var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-container-low);
    }

    .steps-table-wrapper {
      overflow-x: auto;
      border-radius: var(--radius-lg);
      border: 1px solid var(--border-subtle);
      background: var(--surface-container-lowest);
    }

    .steps-table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--font-label);
      text-align: start;

      th {
        padding: var(--space-2) var(--space-3);
        background: var(--surface-container);
        color: var(--text-muted);
        font-weight: 700;
        letter-spacing: 0.04em;
        text-transform: uppercase;
        border-bottom: 1px solid var(--border-subtle);
        white-space: nowrap;
      }

      td {
        padding: var(--space-2) var(--space-3);
        border-bottom: 1px solid var(--border-subtle);
        vertical-align: middle;
      }

      tr:last-child td {
        border-bottom: 0;
      }

      tbody tr:hover {
        background: var(--surface-container-low);
      }

      tbody tr.is-failed {
        background: color-mix(in srgb, var(--danger-soft) 40%, transparent);
      }
    }

    .col-num {
      width: 44px;
      text-align: center;
      color: var(--text-muted);
      font-variant-numeric: tabular-nums;
    }

    .node-cell {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }

    .node-name {
      font-weight: 600;
      color: var(--text-primary);
    }

    .node-kind {
      font-size: 0.78rem;
      color: var(--text-muted);
    }

    .col-output {
      min-width: 120px;
    }

    .output-pill {
      display: inline-flex;
      align-items: center;
      padding: 2px 8px;
      border-radius: var(--radius-pill);
      background: var(--surface-container);
      color: var(--text-secondary);
      font-weight: 600;
      font-size: 0.8rem;
    }

    .col-worker {
      color: var(--text-secondary);
      white-space: nowrap;
    }

    .col-duration {
      font-variant-numeric: tabular-nums;
      color: var(--text-primary);
      white-space: nowrap;
      font-weight: 500;
    }

    .col-status {
      width: 120px;
    }
  `,
})
export class ActivityExecutionSummaryComponent {
  private readonly notifications = inject(NotificationService);

  readonly execution = input<ExecutionResponse | null>(null);
  readonly file = input<LibraryActivityFile | null>(null);
  readonly steps = input<readonly ExecutionPipelineStep[]>([]);
  readonly loading = input<boolean>(false);

  protected readonly baseName = baseName;
  protected readonly executionStateLabel = executionStateLabel;

  protected readonly fileName = computed(() => {
    const media = this.execution()?.media;
    if (media) return baseName(media);
    const rel = this.file()?.relativePath;
    return rel ? baseName(rel) : '—';
  });

  protected readonly fullPath = computed(() => {
    return this.execution()?.media || this.file()?.relativePath || '';
  });

  protected readonly libraryName = computed(() => {
    return this.execution()?.library || this.file()?.library || '—';
  });

  protected readonly flowDescription = computed(() => {
    const exec = this.execution();
    if (exec?.flow) {
      return `${exec.flow} · v${exec.flowVersion}`;
    }
    const f = this.file();
    if (f?.flow) {
      return `${f.flow} · v${f.flowVersion}`;
    }
    return 'Aucun flow attribué';
  });

  protected readonly workerName = computed(() => {
    return this.execution()?.node || this.file()?.node || '—';
  });

  protected readonly totalDuration = computed(() => {
    return executionDuration(this.execution());
  });

  protected readonly sourceSize = computed(() => {
    const fromMeta = parseSize(this.execution()?.beforeMetadata);
    if (fromMeta != null) return formatFileSize(fromMeta);
    const fileBytes = this.file()?.bytes;
    if (fileBytes != null && fileBytes > 0) return formatFileSize(fileBytes);
    return '—';
  });

  protected readonly finalSize = computed(() => {
    const fromMeta = parseSize(this.execution()?.afterMetadata);
    if (fromMeta != null) return formatFileSize(fromMeta);
    return '—';
  });

  protected readonly sizeDifference = computed(() => {
    const source = parseSize(this.execution()?.beforeMetadata) ?? this.file()?.bytes ?? null;
    const final = parseSize(this.execution()?.afterMetadata);
    if (source == null || final == null || source <= 0) return null;
    const diff = final - source;
    const percentNum = (diff / source) * 100;
    const sign = diff > 0 ? '+' : '';
    const percentStr = `${sign}${percentNum.toFixed(1)} %`;
    const labelStr = `${sign}${formatFileSize(diff)}`;
    return {
      diff,
      percent: percentStr,
      label: labelStr,
      reduced: diff < 0,
      expanded: diff > 0,
      neutral: diff === 0,
    };
  });

  protected formatStepDuration(step: ExecutionPipelineStep): string {
    return stepDuration(step);
  }

  protected kindLabel(kind: string): string {
    return (
      {
        condition: 'Condition',
        input: 'Entrée',
        terminal: 'Sortie',
        execution: 'Traitement',
        preparation: 'Préparation',
        action: 'Action',
        variable: 'Variable',
        script: 'Script',
        unknown: 'Bloc',
      }[kind] ?? kind
    );
  }

  protected stateTone(
    state: ExecutionResponse['state'],
  ): 'accent' | 'neutral' | 'danger' | 'success' | 'warning' {
    switch (state) {
      case 'succeeded':
        return 'success';
      case 'running':
        return 'accent';
      case 'failed':
        return 'danger';
      case 'cancelled':
      case 'cancelling':
        return 'warning';
      default:
        return 'neutral';
    }
  }

  protected stepStatusTone(
    status: ExecutionPipelineStep['status'],
  ): 'accent' | 'neutral' | 'danger' | 'success' | 'warning' {
    switch (status) {
      case 'completed':
        return 'success';
      case 'failed':
        return 'danger';
      case 'cancelled':
      case 'cancelling':
        return 'warning';
      case 'running':
        return 'accent';
      default:
        return 'neutral';
    }
  }

  protected stepStatusLabel(status: ExecutionPipelineStep['status']): string {
    switch (status) {
      case 'completed':
        return 'Terminé';
      case 'failed':
        return 'Échec';
      case 'cancelled':
        return 'Annulé';
      case 'cancelling':
        return 'Annulation';
      case 'running':
        return 'En cours';
      default:
        return status;
    }
  }

  protected copy(text: string, label: string): void {
    if (!text) return;
    void navigator.clipboard.writeText(text).then(
      () => this.notifications.show(`${label} copié dans le presse-papiers.`),
      () => this.notifications.show(`Impossible de copier dans le presse-papiers.`),
    );
  }
}
