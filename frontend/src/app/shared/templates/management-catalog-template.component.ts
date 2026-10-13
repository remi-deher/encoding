import { Component, input } from '@angular/core';
import { PageHeaderComponent } from '../ui/page-header.component';

@Component({
  selector: 'app-management-catalog-template',
  imports: [PageHeaderComponent],
  template: `
    <app-page-header [title]="title()" [description]="description()">
      <ng-content select="[page-actions]" />
    </app-page-header>

    <section class="catalog-panel" [attr.aria-label]="title()">
      <div class="catalog-filters">
        <span class="catalog-filter-label">{{ filterLabel() }}</span>
        <ng-content select="[catalogue-filters]" />
      </div>
      <ng-content select="[catalogue-table]" />
    </section>

    @if (note()) {
      <p class="catalog-note" role="note">{{ note() }}</p>
    }
  `,
  styleUrl: './management-catalog-template.component.scss',
})
export class ManagementCatalogTemplateComponent {
  readonly title = input.required<string>();
  readonly description = input('');
  readonly filterLabel = input('Filtres');
  readonly note = input('');
}
