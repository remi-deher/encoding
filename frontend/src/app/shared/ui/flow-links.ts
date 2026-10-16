import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
export interface FlowLinkDrawing {
  readonly id: string;
  readonly path: string;
  readonly error: boolean;
  readonly insertion: { readonly x: number; readonly y: number };
}
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-links',
  imports: [MatButtonModule],
  templateUrl: './flow-links.html',
  styleUrl: './flow-links.scss',
})
export class FlowLinks {
  readonly insertionRequested = output<string>();
  readonly size = input.required<{ readonly width: number; readonly height: number }>();
  readonly links = input.required<readonly FlowLinkDrawing[]>();
}
