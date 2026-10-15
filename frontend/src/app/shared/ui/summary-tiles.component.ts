import { Component, input } from '@angular/core';
import { MetricCardComponent } from './metric-card.component';

export interface SummaryTile {
  readonly label: string;
  readonly value: string;
  readonly detail: string;
  readonly tone?: 'default' | 'attention';
}

/** Rangée de tuiles de synthèse d'une page de gestion : valeur et légende, jamais d'action. */
@Component({
  selector: 'app-summary-tiles',
  imports: [MetricCardComponent],
  template: `
    <section class="summary-tiles" [attr.aria-label]="label()">
      @for (tile of tiles(); track tile.label) {
        <app-metric-card
          [label]="tile.label"
          [value]="tile.value"
          [detail]="tile.detail"
          [tone]="tile.tone ?? 'default'"
        />
      }
    </section>
  `,
  styles: `
    .summary-tiles {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 12rem), 1fr));
      gap: var(--space-4);
    }
  `,
})
export class SummaryTilesComponent {
  readonly label = input('Synthèse');
  readonly tiles = input.required<readonly SummaryTile[]>();
}
