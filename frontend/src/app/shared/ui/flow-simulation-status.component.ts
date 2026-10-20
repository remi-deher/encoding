import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { SimulationResult } from '../models/flow-simulation';
import { FlowGraphNode } from '../models/flow-component';
import { IconComponent } from './icon.component';
@Component({
  selector: 'app-flow-simulation-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `@if (step(); as current) {
    <button
      type="button"
      (click)="$event.stopPropagation(); detailsRequested.emit()"
      [class.stopped]="stopped()"
      [title]="current.detail"
      [attr.aria-label]="label() + ' · Voir les détails du test'"
    >
      <app-icon [name]="stopped() ? 'settings' : 'check'" /><span
        >{{ label() }}
        @if (outputLabel()) {
          · {{ outputLabel() }}
        }
      </span>
    </button>
  }`,

  styles:
    'button { display: flex; align-items: center; gap: 6px; border: 0; border-radius: var(--radius-pill); padding: 8px 12px; min-height: 44px; margin: 8px 12px; max-width: calc(100% - 24px); background: var(--success-soft); color: var(--text-primary); font: inherit; font-size: var(--font-label); cursor: pointer; text-align: start; } button.stopped { background: var(--warning-soft); } button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }',
})
export class FlowSimulationStatusComponent {
  readonly node = input.required<FlowGraphNode>();
  readonly result = input<SimulationResult | null>(null);
  readonly detailsRequested = output<void>();
  protected readonly step = computed(
    () =>
      this.result()?.steps?.find((step) => step.nodeId === this.node().id) ??
      (this.result()?.waitingNodeId === this.node().id
        ? { detail: this.result()?.detail ?? '', outputId: null, assumed: false }
        : undefined),
  );
  protected readonly stopped = computed(
    () =>
      this.result()?.waitingNodeId === this.node().id ||
      (this.result()?.status === 'failed' &&
        this.result()?.steps?.at(-1)?.nodeId === this.node().id),
  );
  protected readonly label = computed(() =>
    this.result()?.waitingNodeId === this.node().id
      ? 'En attente de votre choix'
      : this.stopped()
        ? 'Arrêt du test'
        : this.step()?.assumed
          ? 'Passage supposé'
          : 'Étape parcourue',
  );
  protected readonly outputLabel = computed(
    () =>
      this.node().component.outputs.find((output) => output.id === this.step()?.outputId)?.label ??
      this.step()?.outputId ??
      '',
  );
}
