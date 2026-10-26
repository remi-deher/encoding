import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { CategoryTabOption } from '../ui/category-tabs.component';
import { SubnavComponent } from '../ui/subnav.component';

/** Cadre commun de synthèse et d’orientation pour le rôle métier Piloter. */
@Component({
  selector: 'app-monitor-template',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [SubnavComponent],
  template: `
    <h1 class="visually-hidden">{{ title() }}</h1>
    <div
      #navigationRow
      class="monitor-navigation"
      [class.is-stuck]="navigationStuck()"
    >
      <app-subnav
        #viewNavigation
        class="monitor-subnav"
        presentation="capsule"
        panelId="monitor-panel"
        [label]="viewsLabel()"
        [options]="views()"
        [selected]="selectedView()"
        (selectedChange)="selectedViewChange.emit($event)"
      />
      @if (metadata()) {
        <span class="cluster-name">{{ metadata() }}</span>
      }
    </div>

    <section
      id="monitor-panel"
      role="tabpanel"
      [attr.aria-labelledby]="viewNavigation.tabId(selectedView())"
    >
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
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .cluster-name {
      margin: 0;
      color: var(--text-muted);
      font-size: var(--font-body);
      grid-column: 3;
      grid-row: 1;
      justify-self: end;
      min-width: 0;
      max-width: 100%;
      overflow-wrap: anywhere;
      text-align: end;
    }
    .monitor-navigation {
      position: sticky;
      top: calc(var(--shell-header-sticky-offset, 84px) + var(--space-2));
      z-index: 4;
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);
      align-items: center;
      min-width: 0;
      margin-bottom: var(--space-4);
      padding-block: var(--space-1);
      background: transparent;
      pointer-events: none;
    }
    .monitor-navigation > app-subnav {
      grid-column: 2;
      grid-row: 1;
      pointer-events: auto;
    }
    .monitor-navigation.is-stuck > app-subnav {
      box-shadow: var(--elevation-2);
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
    @media (max-width: 760px) {
      .monitor-navigation {
        grid-template-columns: minmax(0, 1fr);
        grid-template-rows: auto auto;
        gap: var(--space-1);
      }
      .monitor-navigation > app-subnav,
      .cluster-name {
        grid-column: 1;
      }
      .monitor-navigation > app-subnav {
        grid-row: 1;
      }
      .cluster-name {
        grid-row: 2;
        justify-self: center;
        font-size: var(--font-label);
      }
    }
  `,
})
export class MonitorTemplateComponent {
  private readonly navigationRow = viewChild<ElementRef<HTMLElement>>('navigationRow');
  protected readonly navigationStuck = signal(false);

  constructor() {
    effect((onCleanup) => {
      const row = this.navigationRow()?.nativeElement;
      if (!row || typeof window === 'undefined') return;

      const scrollRegion = row.closest<HTMLElement>('.page-transition-region') ?? window;
      const updateStuckState = () => {
        const stickyTop = Number.parseFloat(window.getComputedStyle(row).top);
        const stuck = row.getBoundingClientRect().top <= stickyTop + 1;
        if (stuck !== this.navigationStuck()) this.navigationStuck.set(stuck);
      };

      scrollRegion.addEventListener('scroll', updateStuckState, { passive: true });
      window.addEventListener('resize', updateStuckState, { passive: true });
      updateStuckState();

      onCleanup(() => {
        scrollRegion.removeEventListener('scroll', updateStuckState);
        window.removeEventListener('resize', updateStuckState);
      });
    });
  }

  readonly title = input.required<string>();
  readonly metadata = input('');
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
