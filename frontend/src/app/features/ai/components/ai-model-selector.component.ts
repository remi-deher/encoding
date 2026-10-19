import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSelectModule } from '@angular/material/select';
import { AiAssistantModelOption } from '../../../shared/models/ai-tools';
import { aiEffortLabel, aiModelKey, aiTypeLabel } from './ai-model-selection';

@Component({
  selector: 'app-ai-model-selector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatSelectModule, MatTooltipModule],
  template: `
    <mat-select
      class="type-select"
      aria-label="Type"
      matTooltip="Type"
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
      matTooltip="Modèle"
      placeholder="Modèle"
      [value]="selectedKey()"
      [disabled]="disabled()"
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
    @if (selected()?.reasoningEfforts.length) {
      <mat-select
        class="effort-select"
        aria-label="Effort de raisonnement"
        matTooltip="Effort de raisonnement"
        [value]="effort() ?? ''"
        [disabled]="disabled()"
        (selectionChange)="effortChange.emit($event.value || null)"
      >
        <mat-select-trigger>{{ effort() ? effortLabel(effort()!) : 'Auto' }}</mat-select-trigger>
        <mat-option value="">Automatique</mat-option>
        @for (value of selected()!.reasoningEfforts; track value) {
          <mat-option [value]="value">{{ effortLabel(value) }}</mat-option>
        }
      </mat-select>
    } @else {
      <mat-select
        class="effort-select"
        aria-label="Effort de raisonnement"
        [matTooltip]="
          selected()?.reasoningKnown
            ? 'Ce modèle ne propose aucun niveau de raisonnement'
            : 'Niveaux non communiqués par le fournisseur'
        "
        value="unavailable"
        disabled
      >
        <mat-option value="unavailable">{{
          selected()?.reasoningKnown ? 'Sans effort' : 'Inconnu'
        }}</mat-option>
      </mat-select>
    }
  `,
  styles: `
    :host {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: var(--space-1);
      min-width: 0;
    }
    mat-select {
      width: auto;
      min-width: 0;
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-pill);
      background: var(--surface-container);
      font-size: var(--font-label);
    }
    .type-select {
      flex: 0 1 5rem;
    }
    .model-select {
      flex: 1 1 7rem;
      max-width: 15rem;
    }
    .effort-select {
      flex: 0 1 6.5rem;
    }
    mat-select:focus-visible {
      outline: 2px solid var(--accent-strong);
      outline-offset: 2px;
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
}
