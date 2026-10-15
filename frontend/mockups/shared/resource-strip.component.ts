import { Component, input, output } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatChipsModule } from '@angular/material/chips';
import { StatusBadgeComponent, StatusTone } from '../../src/app/shared/ui/status-badge.component';
export interface ResourceStripData {
  readonly id: string;
  readonly title: string;
  readonly subtitle: string;
  readonly state: string;
  readonly tone: StatusTone;
  readonly badges: readonly string[];
  readonly metrics: readonly {
    label: string;
    value: string;
    detail: string;
    progress?: number;
    emphasis?: boolean;
  }[];
  readonly tags: readonly string[];
  readonly note: string;
  readonly actionLabel: string;
}
@Component({
  selector: 'mock-resource-strip',
  imports: [
    MatCardModule,
    MatButtonModule,
    MatMenuModule,
    MatProgressBarModule,
    MatChipsModule,
    StatusBadgeComponent,
  ],
  templateUrl: './resource-strip.component.html',
  styleUrl: './resource-strip.component.scss',
})
export class ResourceStripComponent {
  readonly data = input.required<ResourceStripData>();
  readonly opened = output<void>();
}
