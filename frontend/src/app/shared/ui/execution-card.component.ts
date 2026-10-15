import { DatePipe } from '@angular/common';
import { Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ExecutionResponse, EXECUTION_STATE_LABELS } from '../models/execution';
import { StatusBadgeComponent, StatusTone } from './status-badge.component';

@Component({
  selector: 'app-execution-card',
  imports: [DatePipe, MatButtonModule, MatProgressBarModule, StatusBadgeComponent],
  template: `
    <article class="execution-card">
      <header class="execution-card__header">
        <div class="execution-card__title">
          <h3>{{ execution().media }}</h3>
          <p>{{ execution().library }} · {{ execution().flow }} · v{{ execution().flowVersion }}</p>
        </div>
        <app-status-badge
          [label]="stateLabels[execution().state]"
          [tone]="stateTone(execution().state)"
        />
      </header>

      <p class="execution-card__detail">{{ execution().detail }}</p>

      <div class="execution-card__facts">
        <div>
          <span>Worker</span>
          <strong>{{ execution().node ?? 'Non attribué' }}</strong>
        </div>
        <div>
          <span>Dernière activité</span>
          <strong>{{ execution().updatedAt | date: 'dd/MM/yyyy HH:mm:ss' }}</strong>
        </div>
        <div>
          <span>Tentative</span>
          <strong>{{ execution().attempt }}</strong>
        </div>
        <div>
          <span>Progression</span>
          <strong>{{ execution().progress === null ? 'Indisponible' : execution().progress + ' %' }}</strong>
        </div>
      </div>

      @if (execution().progress !== null) {
        <mat-progress-bar mode="determinate" [value]="execution().progress ?? 0" />
      }

      @if (showPipelineAction()) {
        <footer class="execution-card__footer">
          <button matButton="outlined" type="button" (click)="pipelineRequested.emit()">
            Voir le pipeline
          </button>
        </footer>
      }
    </article>
  `,
  styles: `
    :host { display: block; }
    .execution-card {
      min-width: 0;
      padding: 16px;
      display: grid;
      gap: 14px;
      border: 1px solid var(--border-subtle);
      border-radius: 12px;
      background: var(--surface);
    }
    .execution-card__header {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 12px;
    }
    .execution-card__title { min-width: 0; }
    .execution-card h3 {
      margin: 0;
      overflow-wrap: anywhere;
      color: var(--text-primary);
      font-size: 16px;
      font-weight: 700;
    }
    .execution-card__title p {
      margin: 5px 0 0;
      overflow-wrap: anywhere;
      color: var(--text-secondary);
      font-size: 14px;
      line-height: 1.45;
    }
    .execution-card__detail {
      margin: 0;
      overflow-wrap: anywhere;
      color: var(--text-primary);
      font-size: 14px;
      line-height: 1.5;
    }
    .execution-card__facts {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 150px), 1fr));
      gap: 12px;
    }
    .execution-card__facts div { min-width: 0; display: grid; gap: 4px; }
    .execution-card__facts span { color: var(--text-muted); font-size: 13px; }
    .execution-card__facts strong {
      overflow-wrap: anywhere;
      color: var(--text-primary);
      font-size: 14px;
      font-weight: 600;
    }
    mat-progress-bar { display: block; border-radius: 99px; }
    .execution-card__footer {
      display: flex;
      justify-content: flex-end;
      padding-top: 12px;
      border-top: 1px solid var(--border-subtle);
    }
    @media (max-width: 480px) {
      .execution-card { padding: 13px; }
      .execution-card__header { flex-direction: column; }
    }
  `,
})
export class ExecutionCardComponent {
  readonly execution = input.required<ExecutionResponse>();
  readonly showPipelineAction = input(false);
  readonly pipelineRequested = output<void>();
  protected readonly stateLabels = EXECUTION_STATE_LABELS;

  protected stateTone(state: ExecutionResponse['state']): StatusTone {
    switch (state) {
      case 'succeeded':
        return 'success';
      case 'failed':
      case 'cancelling':
        return 'danger';
      case 'running':
        return 'accent';
      case 'queued':
      case 'deferred':
      case 'cancelled':
        return 'warning';
    }
  }
}
