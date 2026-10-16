import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

export interface CategoryTabOption {
  readonly value: string;
  readonly label: string;
  readonly count?: number;
}

@Component({
  selector: 'app-category-tabs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [],
  template: `
    <nav class="category-tabs" [attr.aria-label]="label()" role="tablist">
      @for (option of options(); track option.value) {
        <button
          type="button"
          class="category-tab-pill"
          role="tab"
          [class.is-active]="option.value === selected()"
          [attr.aria-selected]="option.value === selected()"
          (click)="selectedChange.emit(option.value)"
        >
          <span class="tab-label">{{ option.label }}</span>
          @if (option.count !== undefined) {
            <span class="option-count">{{ option.count }}</span>
          }
        </button>
      }
    </nav>
  `,
  styleUrl: './category-tabs.component.scss',
})
export class CategoryTabsComponent {
  readonly label = input.required<string>();
  readonly options = input.required<readonly CategoryTabOption[]>();
  readonly selected = input.required<string>();
  readonly selectedChange = output<string>();
}
