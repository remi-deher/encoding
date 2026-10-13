import { Component, input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';

@Component({
  selector: 'app-metric-card',
  imports: [MatCardModule],
  template: `
    <mat-card appearance="outlined" class="metric-card">
      <mat-card-content>
        <p class="metric-label">{{ label() }}</p>
        <p class="metric-value">{{ value() }}</p>
        <p class="metric-detail">{{ detail() }}</p>
      </mat-card-content>
    </mat-card>
  `,
  styleUrl: './metric-card.component.scss',
})
export class MetricCardComponent {
  readonly label = input.required<string>();
  readonly value = input.required<string>();
  readonly detail = input.required<string>();
}
