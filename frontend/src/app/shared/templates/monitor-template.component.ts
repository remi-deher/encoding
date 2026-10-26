import { ChangeDetectionStrategy, Component, TemplateRef, input, output } from '@angular/core';
import { CategoryTabOption, CategoryTabsComponent } from '../ui/category-tabs.component';
import { PageHeaderComponent } from '../ui/page-header.component';

/** Cadre commun de synthèse et d’orientation pour le rôle métier Piloter. */
@Component({
  selector: 'app-monitor-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CategoryTabsComponent, PageHeaderComponent],
  template: `
    <app-page-header
      [title]="title()"
      [actions]="actions()"
      [showSystemActions]="showSystemActions()"
    >
      <span page-metadata class="cluster-name" [hidden]="!metadata()">{{ metadata() }}</span>
    </app-page-header>

    <div class="overview-tabs-wrapper">
      <app-category-tabs
        [label]="viewsLabel()"
        [options]="views()"
        [selected]="selectedView()"
        (selectedChange)="selectedViewChange.emit($event)"
      />
    </div>

    @if (state() === 'loading') {
      <p class="monitor-feedback" role="status">{{ loadingMessage() }}</p>
    } @else if (state() === 'error') {
      <p class="monitor-feedback" role="alert">
        {{ errorMessage() }}
        @if (hasKnownData()) {
          {{ staleMessage() }}
        }
      </p>
    }

    @if (layout() === 'special') {
      <div class="monitor-special"><ng-content select="[monitor-special]" /></div>
    } @else {
      <div class="monitor-summary"><ng-content select="[monitor-summary]" /></div>
      <div
        class="monitor-grid monitor-grid_primary"
        [class.monitor-grid_single]="layout() === 'single'"
      >
        <div class="monitor-slot"><ng-content select="[monitor-primary]" /></div>
        <div class="monitor-slot"><ng-content select="[monitor-context]" /></div>
      </div>
      <div
        class="monitor-grid monitor-grid_supporting"
        [class.monitor-grid_single]="layout() === 'single'"
      >
        <div class="monitor-slot"><ng-content select="[monitor-activity]" /></div>
        <div class="monitor-slot"><ng-content select="[monitor-supporting]" /></div>
      </div>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .overview-tabs-wrapper {
      display: flex;
      align-items: center;
      padding-bottom: var(--space-4);
      margin-bottom: var(--space-5);
      border-bottom: 1px solid var(--border-subtle);
    }
    .cluster-name {
      margin: 0;
      color: var(--text-muted);
      font-size: var(--font-body);
    }
    .monitor-feedback {
      margin: 0 0 var(--space-4);
      color: var(--text-secondary);
      font-size: var(--font-label);
    }
    .monitor-summary:not(:empty) {
      margin-bottom: var(--space-6);
    }
    .monitor-grid {
      display: grid;
      grid-template-columns: minmax(0, 1.65fr) minmax(0, 1fr);
      gap: var(--space-6);
      align-items: start;
    }
    .monitor-grid_single {
      grid-template-columns: minmax(0, 1fr);
    }
    .monitor-grid_supporting {
      margin-top: var(--space-6);
    }
    .monitor-slot {
      min-width: 0;
    }
    .monitor-slot:empty {
      display: none;
    }
    .monitor-special {
      min-width: 0;
    }
    @media (max-width: 1080px) {
      .monitor-grid {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class MonitorTemplateComponent {
  readonly title = input.required<string>();
  readonly metadata = input('');
  readonly actions = input<TemplateRef<unknown> | undefined>();
  readonly showSystemActions = input(false);
  readonly viewsLabel = input('Vues d’ensemble');
  readonly views = input.required<readonly CategoryTabOption[]>();
  readonly selectedView = input.required<string>();
  readonly selectedViewChange = output<string>();
  readonly state = input<'ready' | 'loading' | 'error'>('ready');
  readonly hasKnownData = input(false);
  readonly loadingMessage = input('Chargement des données…');
  readonly errorMessage = input('Impossible d’actualiser les données.');
  readonly staleMessage = input(
    'Les dernières valeurs reçues sont conservées ; elles peuvent être obsolètes.',
  );
  readonly layout = input<'overview' | 'single' | 'special'>('overview');
}
