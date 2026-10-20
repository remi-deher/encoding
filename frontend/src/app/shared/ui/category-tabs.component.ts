import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  input,
  output,
  viewChildren,
} from '@angular/core';

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
    <div
      class="category-tabs"
      [class.is-underline]="variant() === 'underline'"
      [attr.aria-label]="label()"
      role="tablist"
    >
      @for (option of options(); track option.value; let index = $index) {
        <button
          #tabButton
          type="button"
          class="category-tab-pill"
          role="tab"
          [class.is-active]="option.value === selected()"
          [attr.aria-selected]="option.value === selected()"
          [attr.tabindex]="option.value === selected() ? 0 : -1"
          (click)="selectedChange.emit(option.value)"
          (keydown)="onKeydown($event, index)"
        >
          <span class="tab-label">{{ option.label }}</span>
          @if (option.count !== undefined) {
            <span class="option-count">{{ option.count }}</span>
          }
        </button>
      }
    </div>
  `,
  styleUrl: './category-tabs.component.scss',
})
export class CategoryTabsComponent {
  readonly label = input.required<string>();
  readonly options = input.required<readonly CategoryTabOption[]>();
  readonly selected = input.required<string>();
  readonly variant = input<'pill' | 'underline'>('pill');
  readonly selectedChange = output<string>();

  private readonly tabButtons = viewChildren<ElementRef<HTMLButtonElement>>('tabButton');

  protected onKeydown(event: KeyboardEvent, index: number): void {
    const list = this.options();
    if (!list.length) return;

    let nextIndex = -1;
    if (event.key === 'ArrowRight') {
      nextIndex = (index + 1) % list.length;
    } else if (event.key === 'ArrowLeft') {
      nextIndex = (index - 1 + list.length) % list.length;
    } else if (event.key === 'Home') {
      nextIndex = 0;
    } else if (event.key === 'End') {
      nextIndex = list.length - 1;
    }

    if (nextIndex >= 0) {
      event.preventDefault();
      const target = list[nextIndex];
      if (target) {
        this.selectedChange.emit(target.value);
        this.tabButtons()[nextIndex]?.nativeElement.focus();
      }
    }
  }
}
