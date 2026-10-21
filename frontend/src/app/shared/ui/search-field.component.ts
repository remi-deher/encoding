import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

export type SearchFieldVariant = 'bar' | 'filled' | 'compact';

/** Champ de recherche Material 3 : barre (pages catalogues), rempli (panneaux), compact (barres d'outils). */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-search-field',
  template: `
    <label class="search-field" [class]="'search-field search-field_' + variant()">
      <svg class="search-field-icon" viewBox="0 0 24 24" aria-hidden="true">
        <circle cx="10.8" cy="10.8" r="6.3" />
        <path d="m15.5 15.5 5 5" />
      </svg>
      <input
        #field
        type="search"
        autocomplete="off"
        [attr.name]="name() || null"
        [attr.aria-label]="label()"
        [attr.placeholder]="placeholder() || label()"
        [value]="value()"
        (input)="valueChange.emit(field.value)"
        (keydown.escape)="onEscape($event, field)"
      />
      @if (hasValue()) {
        <button
          type="button"
          class="search-field-clear"
          aria-label="Effacer la recherche"
          (click)="clear(field)"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 6l12 12M18 6 6 18" /></svg>
        </button>
      }
    </label>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .search-field {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      width: 100%;
      box-sizing: border-box;
      color: var(--text-muted);
      cursor: text;
      transition:
        background-color 0.15s ease,
        box-shadow 0.15s ease,
        color 0.15s ease;
    }
    .search-field svg {
      flex: none;
      width: 22px;
      height: 22px;
      fill: none;
      stroke: currentColor;
      stroke-width: 2;
      stroke-linecap: round;
    }
    input {
      flex: 1;
      min-width: 0;
      border: 0;
      outline: 0;
      background: transparent;
      color: var(--text-primary);
      font: inherit;
    }
    input::placeholder {
      color: var(--text-muted);
    }
    input::-webkit-search-cancel-button {
      display: none;
    }
    .search-field-clear {
      display: grid;
      flex: none;
      width: 36px;
      height: 36px;
      place-items: center;
      border: 0;
      border-radius: 50%;
      background: transparent;
      color: inherit;
      cursor: pointer;
    }
    .search-field-clear:hover {
      background: var(--surface-container-highest);
    }
    .search-field-clear:focus-visible {
      outline: 2px solid var(--accent);
    }
    .search-field-clear svg {
      width: 18px;
      height: 18px;
    }

    .search-field_bar {
      min-height: 56px;
      padding: 0 8px 0 16px;
      border-radius: 28px;
      background: var(--surface-container-high);
      font-size: 16px;
    }
    .search-field_bar:hover {
      background: var(--surface-container-highest);
    }
    .search-field_bar:focus-within {
      background: var(--surface-container-high);
      box-shadow: 0 1px 3px rgba(0, 0, 0, 0.25);
    }

    .search-field_filled {
      min-height: 48px;
      padding: 0 6px 0 14px;
      border-bottom: 2px solid transparent;
      border-radius: 12px;
      background: var(--surface-container);
      font-size: 15px;
    }
    .search-field_filled:focus-within {
      border-bottom-color: var(--accent);
      background: var(--surface-container-high);
    }

    .search-field_compact {
      min-height: 40px;
      padding: 0 4px 0 12px;
      border-radius: 20px;
      background: var(--surface-container);
      font-size: 14px;
    }
    .search-field_compact svg {
      width: 20px;
      height: 20px;
    }
    .search-field_compact .search-field-clear {
      width: 30px;
      height: 30px;
    }
    .search-field_compact:focus-within {
      background: var(--accent-soft);
      color: var(--accent);
    }
  `,
})
export class SearchFieldComponent {
  readonly value = input('');
  readonly label = input.required<string>();
  readonly placeholder = input('');
  readonly name = input('');
  readonly variant = input<SearchFieldVariant>('filled');
  readonly valueChange = output<string>();

  protected readonly hasValue = computed(() => this.value().length > 0);

  protected clear(field: HTMLInputElement): void {
    field.value = '';
    this.valueChange.emit('');
    field.focus();
  }

  protected onEscape(event: Event, field: HTMLInputElement): void {
    if (!field.value) return;
    event.stopPropagation();
    this.clear(field);
  }
}
