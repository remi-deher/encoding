import { Component, input, output } from '@angular/core';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatChipsModule } from '@angular/material/chips';
import { StatusBadgeComponent, StatusTone } from './status-badge.component';
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
  selector: 'app-resource-strip',
  imports: [
    MatCardModule,
    MatCheckboxModule,
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
  readonly selectable = input(false);
  readonly selected = input(false);
  readonly openable = input(true);
  readonly actions = input<readonly { id: string; label: string; tone?: 'default' | 'danger' }[]>(
    [],
  );
  readonly selectionChanged = output<void>();
  readonly actionSelected = output<string>();
}
