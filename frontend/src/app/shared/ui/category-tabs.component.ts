import { Component, computed, input, output } from '@angular/core';
import { MatTabsModule } from '@angular/material/tabs';

export interface CategoryTabOption {
  readonly value: string;
  readonly label: string;
  readonly count?: number;
}

@Component({
  selector: 'app-category-tabs',
  imports: [MatTabsModule],
  template: `
    <mat-tab-group
      class="category-tabs"
      [attr.aria-label]="label()"
      [selectedIndex]="selectedIndex()"
      (selectedIndexChange)="onSelectedIndexChange($event)"
    >
      @for (option of options(); track option.value) {
        <mat-tab>
          <ng-template mat-tab-label>
            {{ option.label }}
            @if (option.count !== undefined) {
              <span class="option-count">{{ option.count }}</span>
            }
          </ng-template>
        </mat-tab>
      }
    </mat-tab-group>
  `,
  styleUrl: './category-tabs.component.scss',
})
export class CategoryTabsComponent {
  readonly label = input.required<string>();
  readonly options = input.required<readonly CategoryTabOption[]>();
  readonly selected = input.required<string>();
  readonly selectedChange = output<string>();

  protected readonly selectedIndex = computed(() =>
    Math.max(
      0,
      this.options().findIndex((option) => option.value === this.selected()),
    ),
  );

  protected onSelectedIndexChange(index: number): void {
    const option = this.options()[index];
    if (option) {
      this.selectedChange.emit(option.value);
    }
  }
}
