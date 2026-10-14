import { Component, TemplateRef, contentChild, input, output } from '@angular/core';
import { DetailDrawerComponent } from '../ui/detail-drawer/detail-drawer';
import { PageHeaderComponent } from '../ui/page-header.component';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

@Component({
  selector: 'app-management-catalog-template',
  imports: [DetailDrawerComponent, PageHeaderComponent, MatFormFieldModule, MatInputModule],
  template: `
    <app-detail-drawer
      [opened]="actionOpen() || detailOpen()"
      [title]="actionOpen() ? actionTitle() : detailTitle()"
      [description]="actionOpen() ? actionDescription() : detailDescription()"
      [content]="actionOpen() ? actionContent() : detailContent()"
      [headerActions]="actionOpen() ? undefined : detailHeaderActions()"
      (closed)="onDrawerClosed()"
    >
      <div drawer-main>
        <app-page-header [title]="title()" [actions]="pageActions()" />
        <ng-content select="[catalogue-navigation]" />

        <section class="catalog-panel" [attr.aria-label]="title()">
          <div class="catalog-toolbar">
            <div class="catalog-filters">
              <span class="catalog-filter-label">{{ filterLabel() }}</span>
              @if (searchLabel()) {
                <mat-form-field
                  appearance="outline"
                  subscriptSizing="dynamic"
                  class="catalog-search"
                >
                  <mat-label>{{ searchLabel() }}</mat-label>
                  <input
                    matInput
                    type="search"
                    [value]="searchQuery()"
                    (input)="onSearchInput($event)"
                  />
                </mat-form-field>
              }
              <ng-content select="[catalogue-filters]" />
            </div>
          </div>
          @if (catalogState() === 'loading') {
            <p class="catalog-state" role="status">{{ catalogMessage() || 'Chargement…' }}</p>
          } @else if (catalogState() === 'error') {
            <p class="catalog-state" role="alert">
              {{ catalogMessage() || 'Les données ne sont pas disponibles.' }}
            </p>
          } @else {
            <ng-content select="[catalogue-table]" />
          }
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
  readonly filterLabel = input('Filtres');
  readonly searchLabel = input('');
  readonly searchQuery = input('');
  readonly searchQueryChange = output<string>();
  readonly note = input('');
  readonly catalogState = input<'ready' | 'loading' | 'error'>('ready');
  readonly catalogMessage = input('');
  readonly detailOpen = input(false);
  readonly detailTitle = input('Détails');
  readonly detailDescription = input('');
  readonly actionOpen = input(false);
  readonly actionTitle = input('Ajouter');
  readonly actionDescription = input('');
  readonly detailClosed = output<void>();
  readonly actionClosed = output<void>();
  protected readonly pageActions = contentChild('pageActions', { read: TemplateRef });
  protected readonly detailContent = contentChild('detailContent', { read: TemplateRef });
  protected readonly detailHeaderActions = contentChild('detailHeaderActions', {
    read: TemplateRef,
  });
  protected readonly actionContent = contentChild('actionContent', { read: TemplateRef });

  protected onSearchInput(event: Event): void {
    this.searchQueryChange.emit((event.target as HTMLInputElement).value);
  }

  protected onDrawerClosed(): void {
    if (this.actionOpen()) this.actionClosed.emit();
    else this.detailClosed.emit();
  }
}
