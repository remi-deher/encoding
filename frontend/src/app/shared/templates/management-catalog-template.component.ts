import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  contentChild,
  input,
  output,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { DetailDrawerComponent } from '../ui/detail-drawer/detail-drawer';
import { SearchFieldComponent } from '../ui/search-field.component';
import { PageHeaderComponent } from '../ui/page-header.component';
import { MatButtonModule } from '@angular/material/button';

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
    NgTemplateOutlet,
    PageHeaderComponent,
    SearchFieldComponent,
  ],
  template: `
    <app-detail-drawer
      [opened]="actionOpen() || (detailOpen() && detailLayout() === 'drawer')"
      [size]="drawerSize()"
      [modal]="drawerModal()"
      [title]="actionOpen() ? actionTitle() : detailTitle()"
      [description]="actionOpen() ? actionDescription() : detailDescription()"
      [content]="actionOpen() ? actionContent() : detailContent()"
      [headerActions]="
        actionOpen() || detailLayout() === 'page' ? undefined : detailHeaderActions()
      "
      [footerActions]="actionOpen() ? actionFooterActions() : detailFooterActions()"
      [step]="actionOpen() ? actionStep() : null"
      (closed)="onDrawerClosed()"
    >
      <div drawer-main>
        @if (detailOpen() && detailLayout() === 'page') {
          <app-page-header
            [title]="detailTitle()"
            [description]="detailDescription()"
            [actions]="detailHeaderActions()"
          />
          <ng-container [ngTemplateOutlet]="detailContent()" />
        } @else {
          @if (showPageHeader()) {
            <app-page-header [title]="title()" [actions]="pageActions()" />
          }
          <div class="catalog-navigation-wrapper">
            <ng-content select="[catalogue-navigation]" />
          </div>
          <div class="catalog-summary"><ng-content select="[catalogue-summary]" /></div>

          <section class="catalog-panel" [attr.aria-label]="title()">
            @if (showToolbar()) {
              <ng-template #filtersContent><ng-content select="[catalogue-filters]" /></ng-template>
              <div class="catalog-toolbar">
                <div class="catalog-filters">
                  @if (filterLabel() && searchLayout() === 'end') {
                    <span class="catalog-filter-label">{{ filterLabel() }}</span>
                  }
                  <div class="catalog-toolbar-row" [class.is-centered]="searchLayout() !== 'end'">
                    @if (searchLayout() === 'end') {
                      <ng-container [ngTemplateOutlet]="filtersContent" />
                    }
                    @if (searchLabel()) {
                      <app-search-field
                        class="catalog-search"
                        variant="bar"
                        [label]="searchLabel()"
                        [value]="searchQuery()"
                        (valueChange)="searchQueryChange.emit($event)"
                        [filterCount]="searchLayout() === 'centered-sheet' ? filterCount() : null"
                      >
                        @if (searchLayout() === 'centered-sheet') {
                          <div search-filters class="catalog-sheet-filters">
                            <ng-container [ngTemplateOutlet]="filtersContent" />
                          </div>
                        }
                      </app-search-field>
                    }
                    @if (searchLayout() === 'centered') {
                      <ng-container [ngTemplateOutlet]="filtersContent" />
                    }
                    @if (searchLayout() === 'centered-sheet') {
                      <ng-content select="[catalogue-active-filters]" />
                    }
                  </div>
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
  readonly showPageHeader = input(true);
  readonly filterLabel = input('Filtres');
  readonly searchLabel = input('');
  readonly searchQuery = input('');
  readonly searchQueryChange = output<string>();
  /**
   * `end` : barre à droite, filtres à côté. `centered` : barre centrée, filtres dessous.
   * `centered-sheet` : barre centrée, filtres dans une feuille ouverte par le bouton Filtres.
   */
  readonly searchLayout = input<'end' | 'centered' | 'centered-sheet'>('end');
  /** Nombre de filtres actifs, affiché sur le bouton Filtres (`centered-sheet`). */
  readonly filterCount = input(0);
  readonly note = input('');
  readonly showToolbar = input(true);
  readonly emptyState = input<CatalogEmptyState | null>(null);
  readonly emptyStateAction = output<void>();
  readonly catalogState = input<'ready' | 'loading' | 'error'>('ready');
  readonly catalogMessage = input('');
  /**
   * `drawer` : la fiche s'ouvre dans un tiroir. `page` : la fiche remplace la liste dans la page ;
   * le tiroir ne sert alors qu'aux formulaires d'action.
   */
  readonly detailLayout = input<'drawer' | 'page'>('drawer');
  readonly detailOpen = input(false);
  readonly detailTitle = input('Détails');
  readonly detailDescription = input('');
  readonly actionOpen = input(false);
  readonly actionTitle = input('Ajouter');
  readonly actionDescription = input('');
  /** Avancement d'un formulaire en plusieurs étapes dans le tiroir d'action. */
  readonly actionStep = input<{ readonly current: number; readonly total: number } | null>(null);
  /** Largeur du tiroir : `standard` (560 px) pour une fiche, `wide` pour un éditeur. */
  readonly drawerSize = input<'standard' | 'wide'>('wide');
  /** Voile derrière le tiroir ; un clic dessus le ferme. */
  readonly drawerModal = input(true);
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
  protected readonly actionFooterActions = contentChild('actionFooterActions', {
    read: TemplateRef,
  });
  protected readonly actionContent = contentChild('actionContent', { read: TemplateRef });

  protected onDrawerClosed(): void {
    if (this.actionOpen()) this.actionClosed.emit();
    else this.detailClosed.emit();
  }
}
