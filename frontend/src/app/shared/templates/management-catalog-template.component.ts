import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  contentChild,
  input,
  output,
} from '@angular/core';
import { DetailDrawerComponent } from '../ui/detail-drawer/detail-drawer';
import { PageHeaderComponent } from '../ui/page-header.component';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

export interface CatalogEmptyState {
  readonly icon: string;
  readonly eyebrow: string;
  readonly title: string;
  readonly description: string;
  readonly chips?: readonly string[];
  readonly actionLabel?: string;
  readonly footnote?: string;
}

@Component({
  selector: 'app-management-catalog-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DetailDrawerComponent,
    MatButtonModule,
    PageHeaderComponent,
    MatFormFieldModule,
    MatInputModule,
  ],
  template: `
    <app-detail-drawer
      [opened]="actionOpen() || detailOpen()"
      [size]="drawerSize()"
      [modal]="drawerModal()"
      [title]="actionOpen() ? actionTitle() : detailTitle()"
      [description]="actionOpen() ? actionDescription() : detailDescription()"
      [content]="actionOpen() ? actionContent() : detailContent()"
      [headerActions]="actionOpen() ? undefined : detailHeaderActions()"
      [footerActions]="actionOpen() ? undefined : detailFooterActions()"
      (closed)="onDrawerClosed()"
    >
      <div drawer-main>
        <app-page-header [title]="title()" [actions]="pageActions()" />
        <ng-content select="[catalogue-navigation]" />
        <div class="catalog-summary"><ng-content select="[catalogue-summary]" /></div>

        <section class="catalog-panel" [attr.aria-label]="title()">
          @if (showToolbar()) {
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
          }
          @if (catalogState() === 'loading') {
            <p class="catalog-state" role="status">{{ catalogMessage() || 'Chargement…' }}</p>
          } @else if (catalogState() === 'error') {
            <p class="catalog-state" role="alert">
              {{ catalogMessage() || 'Les données ne sont pas disponibles.' }}
            </p>
          } @else if (emptyState(); as empty) {
            <section class="catalog-empty" aria-labelledby="catalog-empty-title">
              <div class="catalog-empty-content">
                <div class="catalog-empty-icon" aria-hidden="true">{{ empty.icon }}</div>
                <p class="catalog-empty-eyebrow">{{ empty.eyebrow }}</p>
                <h2 id="catalog-empty-title">{{ empty.title }}</h2>
                <p class="catalog-empty-description">{{ empty.description }}</p>
                @if (empty.chips?.length) {
                  <div class="catalog-empty-chips" aria-label="Options disponibles">
                    @for (chip of empty.chips; track chip) {
                      <span>{{ chip }}</span>
                    }
                  </div>
                }
                @if (empty.actionLabel) {
                  <button matButton="filled" type="button" (click)="emptyStateAction.emit()">
                    {{ empty.actionLabel }}
                  </button>
                }
                @if (empty.footnote) {
                  <p class="catalog-empty-footnote">{{ empty.footnote }}</p>
                }
              </div>
            </section>
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
  readonly showToolbar = input(true);
  readonly emptyState = input<CatalogEmptyState | null>(null);
  readonly emptyStateAction = output<void>();
  readonly catalogState = input<'ready' | 'loading' | 'error'>('ready');
  readonly catalogMessage = input('');
  readonly detailOpen = input(false);
  readonly detailTitle = input('Détails');
  readonly detailDescription = input('');
  readonly actionOpen = input(false);
  readonly actionTitle = input('Ajouter');
  readonly actionDescription = input('');
  /** Largeur du tiroir : `standard` (560 px) pour une fiche, `wide` pour un éditeur. */
  readonly drawerSize = input<'standard' | 'wide'>('wide');
  /** Voile derrière le tiroir ; un clic dessus le ferme. */
  readonly drawerModal = input(false);
  readonly detailClosed = output<void>();
  readonly actionClosed = output<void>();
  protected readonly pageActions = contentChild('pageActions', { read: TemplateRef });
  protected readonly detailContent = contentChild('detailContent', { read: TemplateRef });
  protected readonly detailFooterActions = contentChild('detailFooterActions', {
    read: TemplateRef,
  });
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
