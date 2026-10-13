import { Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { RouterLink } from '@angular/router';

export type DrawerActionRoute = string | readonly string[];

@Component({
  selector: 'app-drawer-action-button',
  imports: [MatButtonModule, RouterLink],
  template: `
    @if (routerLink(); as route) {
      <a
        matButton="filled"
        class="drawer-action-button"
        [routerLink]="route"
        [info]="navigationInfo()"
      >{{ label() }}</a>
    } @else {
      <button
        matButton="filled"
        class="drawer-action-button"
        type="button"
        (click)="activated.emit()"
      >{{ label() }}</button>
    }
  `,
  styleUrl: './drawer-action-button.component.scss',
})
export class DrawerActionButtonComponent {
  readonly label = input.required<string>();
  readonly routerLink = input<DrawerActionRoute | null>(null);
  readonly navigationInfo = input<Readonly<Record<string, unknown>> | undefined>(undefined);
  readonly activated = output<void>();
}
