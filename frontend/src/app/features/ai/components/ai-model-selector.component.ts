import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatSelectModule } from '@angular/material/select';
import { MatSliderModule } from '@angular/material/slider';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AiAssistantModelOption } from '../../../shared/models/ai-tools';
import { aiEffortLabel, aiModelKey, aiTypeLabel } from './ai-model-selection';

@Component({
  selector: 'app-ai-model-selector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatSelectModule, MatSliderModule, MatTooltipModule],
  template: `
    <div class="selection-row">
      <mat-select
        class="provider-select"
        aria-label="Fournisseur IA"
        matTooltip="Fournisseur IA actif"
        [value]="provider()"
        [disabled]="disabled()"
        (selectionChange)="typeChange.emit($event.value)"
      >
        @if (provider() && !types().includes(provider())) {
          <mat-option [value]="provider()" disabled>{{ typeLabel(provider()) }}</mat-option>
        }
        @for (type of types(); track type) {
          <mat-option [value]="type">{{ typeLabel(type) }}</mat-option>
        }
      </mat-select>

      <mat-select
        class="model-select"
        aria-label="Modèle"
        matTooltip="Modèles disponibles pour le fournisseur sélectionné"
        placeholder="Modèle"
        [value]="selectedKey()"
        [disabled]="disabled() || !filteredModels().length"
        (selectionChange)="modelChange.emit($event.value)"
      >
        <mat-select-trigger>{{
          selected()?.label ?? unavailableModel() ?? 'Modèle indisponible'
        }}</mat-select-trigger>
        @if (!selected() && selectedKey()) {
          <mat-option [value]="selectedKey()" disabled>{{
            unavailableModel() ?? 'Modèle indisponible'
          }}</mat-option>
        }
        @for (option of filteredModels(); track key(option)) {
          <mat-option [value]="key(option)"
            >{{ option.label }}{{ option.nodeName ? ' · ' + option.nodeName : '' }}</mat-option
          >
        }
      </mat-select>
    </div>

    @if (selected()?.reasoningEfforts.length) {
      <section class="effort-control" aria-label="Niveau de réflexion">
        <div class="effort-heading">
          <div>
            <strong>Niveau de réflexion</strong>
            <small>Efforts disponibles pour ce modèle</small>
          </div>
          <span class="effort-value">{{ selectedEffortLabel() }}</span>
        </div>
        <mat-slider
          class="effort-slider"
          min="0"
          [max]="reasoningEfforts().length"
          step="1"
          discrete
          showTickMarks
          [displayWith]="effortSliderLabel"
          [disabled]="disabled()"
        >
          <input
            matSliderThumb
            aria-label="Effort de raisonnement"
            [value]="sliderValue()"
            (valueChange)="selectEffortAt($event)"
          />
        </mat-slider>
        <div class="effort-labels" aria-hidden="true">
          <span>Auto</span>
          <span>{{ effortLabel(reasoningEfforts()[0]) }}</span>
          <span>{{ effortLabel(reasoningEfforts()[reasoningEfforts().length - 1]) }}</span>
        </div>
      </section>
    } @else if (selected()) {
      <section class="effort-control effort-unavailable" aria-label="Niveau de réflexion">
        <strong>Niveau de réflexion</strong>
        <small>{{ reasoningAvailabilityMessage() }}</small>
      </section>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-2);
      min-width: 0;
    }
    .selection-row {
      display: grid;
      grid-template-columns: minmax(6.5rem, 0.8fr) minmax(8rem, 1.4fr);
      gap: var(--space-2);
      min-width: 0;
    }
    mat-select {
      width: 100%;
      min-width: 0;
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
      font-size: var(--font-label);
    }
    mat-select:focus-visible {
      outline: 2px solid var(--accent-strong);
      outline-offset: 2px;
    }
    .effort-control {
      display: grid;
      gap: var(--space-1);
      min-width: 0;
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-container-low);
    }
    .effort-heading {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-2);
      min-width: 0;
    }
    .effort-heading strong,
    .effort-heading small,
    .effort-unavailable strong,
    .effort-unavailable small {
      display: block;
    }
    .effort-heading strong,
    .effort-unavailable strong {
      font-size: var(--font-label);
      font-weight: 600;
    }
    .effort-heading small,
    .effort-unavailable small {
      margin-top: 2px;
      color: var(--text-muted);
      font-size: var(--font-label);
    }
    .effort-value {
      flex: 0 0 auto;
      padding: var(--space-1) var(--space-2);
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
      font-size: var(--font-label);
    }
    .effort-slider {
      display: block;
      width: 100%;
      margin: 0;
    }
    .effort-labels {
      display: flex;
      justify-content: space-between;
      gap: var(--space-2);
      color: var(--text-muted);
      font-size: var(--font-label);
    }
    .effort-labels span:nth-child(2) {
      text-align: center;
    }
    .effort-labels span:last-child {
      text-align: right;
    }
    .effort-unavailable {
      min-height: 56px;
      align-content: center;
    }
  `,
})
export class AiModelSelectorComponent {
  readonly options = input<readonly AiAssistantModelOption[]>([]);
  readonly selectedKey = input('');
  readonly fallbackProvider = input('');
  readonly unavailableModel = input<string | null>(null);
  readonly effort = input<string | null>(null);
  readonly disabled = input(false);
  readonly typeChange = output<string>();
  readonly modelChange = output<string>();
  readonly effortChange = output<string | null>();
  protected readonly key = aiModelKey;
  protected readonly typeLabel = aiTypeLabel;
  protected readonly effortLabel = aiEffortLabel;
  protected readonly selected = computed(
    () => this.options().find((option) => aiModelKey(option) === this.selectedKey()) ?? null,
  );
  protected readonly provider = computed(
    () =>
      this.selected()?.provider ?? (this.selectedKey().split('\n')[0] || this.fallbackProvider()),
  );
  protected readonly types = computed(() => [
    ...new Set(this.options().map((option) => option.provider)),
  ]);
  protected readonly filteredModels = computed(() =>
    this.options().filter((option) => option.provider === this.provider()),
  );
  protected readonly reasoningEfforts = computed(() => this.selected()?.reasoningEfforts ?? []);
  protected readonly sliderValue = computed(() => {
    const effort = this.effort();
    if (!effort) return 0;
    const index = this.reasoningEfforts().indexOf(effort);
    return index < 0 ? 0 : index + 1;
  });
  protected readonly selectedEffortLabel = computed(() =>
    this.effort() ? this.effortLabel(this.effort()!) : 'Automatique',
  );
  protected readonly reasoningAvailabilityMessage = computed(() =>
    this.selected()?.reasoningKnown
      ? 'Ce modèle ne propose pas de réglage d’effort.'
      : 'Les niveaux d’effort ne sont pas communiqués par le fournisseur.',
  );
  protected readonly effortSliderLabel = (value: number): string =>
    value === 0 ? 'Automatique' : this.effortLabel(this.reasoningEfforts()[value - 1] ?? '');

  protected selectEffortAt(value: number): void {
    this.effortChange.emit(value === 0 ? null : (this.reasoningEfforts()[value - 1] ?? null));
  }
}
