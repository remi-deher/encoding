import { NgTemplateOutlet } from '@angular/common';
import { Component, TemplateRef, input } from '@angular/core';

@Component({
  selector: 'app-page-header',
  imports: [NgTemplateOutlet],
  template: `
    <div class="page-header">
      <div class="page-heading">
        <h1>{{ title() }}</h1>
      </div>
      <div class="page-actions">
        <ng-content select="[page-actions]" />
        <ng-container [ngTemplateOutlet]="actions()" />
      </div>
    </div>
  `,
  styleUrl: './page-header.component.scss',
})
export class PageHeaderComponent {
  readonly title = input.required<string>();
  readonly actions = input<TemplateRef<unknown> | undefined>();
}
