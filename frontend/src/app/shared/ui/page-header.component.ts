import { Router } from '@angular/router';
import { PageTitle } from '../services/page-title';
import { NgTemplateOutlet } from '@angular/common';
import { Component, TemplateRef, input, inject, effect, DestroyRef } from '@angular/core';

@Component({
  selector: 'app-page-header',
  imports: [NgTemplateOutlet],
  template: `
    <div class="page-header" [class.page-header_workspace]="layout() === 'workspace'">
      @if (showHeading()) {
        <div class="page-heading">
          <h1>{{ title() }}</h1>
          @if (description()) {
            <p class="page-description">{{ description() }}</p>
          }
          <div class="page-metadata"><ng-content select="[page-metadata]" /></div>
        </div>
      }
      <div class="page-actions">
        <ng-content select="[page-actions]" />
        <ng-container [ngTemplateOutlet]="actions()" />
      </div>
      <div class="page-tools"><ng-content select="[page-tools]" /></div>
    </div>
  `,
  styleUrl: './page-header.component.scss',
})
export class PageHeaderComponent {
  readonly title = input.required<string>();
  readonly showHeading = input(true);
  readonly layout = input<'default' | 'workspace'>('default');
  readonly description = input('');
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
