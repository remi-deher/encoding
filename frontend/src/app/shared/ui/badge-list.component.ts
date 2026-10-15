import { Component, input } from '@angular/core';

@Component({
  selector: 'app-badge-list',
  template: `
    <span class="badges">
      @for (badge of badges(); track $index) {
        <span class="badge">{{ badge }}</span>
      } @empty {
        —
      }
    </span>
  `,
  styles: `
    .badges {
      display: inline-flex;
      flex-wrap: wrap;
      gap: 4px;
    }
    .badge {
      padding: 2px 8px;
      border: 1px solid var(--border-subtle);
      border-radius: 999px;
      background: var(--surface-muted);
      color: var(--text-secondary);
      font-size: 14px;
      font-weight: 600;
      white-space: nowrap;
    }
  `,
})
export class BadgeListComponent {
  readonly badges = input.required<readonly string[]>();
}
