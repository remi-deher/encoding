import { Router } from '@angular/router';
import { PageTitle } from '../services/page-title';
import { NgTemplateOutlet } from '@angular/common';
import { Component, TemplateRef, input, inject, effect, DestroyRef } from '@angular/core';

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
  readonly shellTitle = input<string>();
  private readonly pageTitle = inject(PageTitle);
  private readonly router = inject(Router);
  constructor() {
    effect(() => {
      const title = this.shellTitle();
      if (title) this.pageTitle.set(this, this.router.url, title);
      else this.pageTitle.clear(this);
    });
    inject(DestroyRef).onDestroy(() => this.pageTitle.clear(this));
  }
  readonly actions = input<TemplateRef<unknown> | undefined>();
}
