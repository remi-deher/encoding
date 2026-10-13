import { Component, TemplateRef, contentChild, input, output } from '@angular/core';
import { DetailDrawerComponent } from '../ui/detail-drawer/detail-drawer';
import { PageHeaderComponent } from '../ui/page-header.component';

@Component({
  selector: 'app-management-catalog-template',
  imports: [DetailDrawerComponent, PageHeaderComponent],
  template: `
    <app-detail-drawer
      [opened]="detailOpen()"
      [title]="detailTitle()"
      [description]="detailDescription()"
      [content]="detailContent()"
      (closed)="detailClosed.emit()"
    >
      <div drawer-main>
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
      </div>

      <div drawer-content>
        <ng-content select="[catalogue-detail]" />
      </div>
    </app-detail-drawer>
  `,
  styleUrl: './management-catalog-template.component.scss',
})
export class ManagementCatalogTemplateComponent {
  readonly title = input.required<string>();
  readonly description = input('');
  readonly filterLabel = input('Filtres');
  readonly note = input('');
  readonly detailOpen = input(false);
  readonly detailTitle = input('Détails');
  readonly detailDescription = input('');
  readonly detailClosed = output<void>();
  protected readonly detailContent = contentChild('detailContent', { read: TemplateRef });
}
