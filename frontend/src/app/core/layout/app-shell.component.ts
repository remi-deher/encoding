import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import { PageTitle } from '../../shared/services/page-title';
import { ClusterOverview, INITIAL_CLUSTER_OVERVIEW } from '../../shared/services/cluster-overview';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  effect,
  inject,
  signal,
  viewChild,
  viewChildren,
} from '@angular/core';
import { toSignal, toObservable } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { BreakpointObserver } from '@angular/cdk/layout';
import { MatButtonModule } from '@angular/material/button';
import { MatSidenavModule } from '@angular/material/sidenav';
import { filter, map, of, switchMap } from 'rxjs';
import {
  getPrimaryNavigationGroups,
  PrimaryNavigationComponent,
} from '../navigation/primary-navigation.component';
import { AuthService } from '../auth/auth.service';
import { UserMenuComponent } from '../auth/user-menu.component';
import { ThemeToggleComponent } from '../theme/theme-toggle.component';

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatSidenavModule,
    PrimaryNavigationComponent,
    RouterLink,
    RouterOutlet,
    ThemeToggleComponent,
    UserMenuComponent,
  ],
  templateUrl: './app-shell.component.html',
  host: {
    '(document:keydown)': 'onGlobalSearchShortcut($event)',
  },
})
export class AppShellComponent {
  private readonly router = inject(Router);
  private readonly navigationEnd = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map(() => this.getCurrentPageContext()),
    ),
    { initialValue: this.getCurrentPageContext() },
  );

  private readonly compactNavigation = toSignal(
    inject(BreakpointObserver)
      .observe('(max-width: 760px)')
      .pipe(map((state) => state.matches)),
    { initialValue: false },
  );
  protected readonly isCompactNavigation = this.compactNavigation;
  protected readonly navigationMode = computed(() =>
    this.isCompactNavigation() ? 'over' : 'side',
  );
  protected readonly navigationOpened = signal(true);
  protected readonly navigationCollapsed = signal(
    typeof localStorage !== 'undefined' ? localStorage.getItem('nav_collapsed') === 'true' : false,
  );

  protected toggleNavigationCollapsed(): void {
    if (this.isCompactNavigation()) {
      this.navigationOpened.set(!this.navigationOpened());
    } else {
      this.navigationCollapsed.update((v) => {
        const next = !v;
        try {
          localStorage.setItem('nav_collapsed', String(next));
        } catch {
          // ignore
        }
        return next;
      });
    }
  }

  constructor() {
    effect(() => {
      const compact = this.isCompactNavigation();
      const context = this.navigationEnd();
      this.navigationOpened.set(!compact && !context.authMode && !context.workspaceMode);
    });
  }

  private readonly dynamicTitle = inject(PageTitle);
  protected readonly pageTitle = computed(() => {
    const context = this.navigationEnd();
    const override = this.dynamicTitle.current();
    return override?.url === context.url ? override.title : context.title;
  });
  protected readonly workspaceMode = computed(() => this.navigationEnd().workspaceMode);
  protected readonly authMode = computed(() => this.navigationEnd().authMode);
  protected readonly searchQuery = signal('');
  protected readonly paletteOpen = signal(false);
  private readonly paletteSearchInput =
    viewChild<ElementRef<HTMLInputElement>>('paletteSearchInput');
  private readonly firstSearchResult =
    viewChild<ElementRef<HTMLAnchorElement>>('firstSearchResult');
  private readonly searchResultElements =
    viewChildren<ElementRef<HTMLAnchorElement>>('firstSearchResult');

  private readonly auth = inject(AuthService);
  protected readonly currentUser = this.auth.user;
  // Aucun appel à l’API de gestion sans session.
  private readonly pollingDisabled = computed(() => this.authMode() || !this.auth.user());
  private readonly overview = inject(ClusterOverview);
  private readonly clusterState = toSignal(
    toObservable(this.pollingDisabled).pipe(
      switchMap((disabled) => (disabled ? of(INITIAL_CLUSTER_OVERVIEW) : this.overview.state$)),
    ),
    { initialValue: INITIAL_CLUSTER_OVERVIEW },
  );
  protected readonly clusterHealthy = computed(() => {
    const state = this.clusterState();
    const nodes = state.snapshot?.nodes ?? [];
    if (!nodes.length) return true;
    return nodes.some((n) => n.status === 'online');
  });
  protected readonly clusterLabel = computed(() => {
    const state = this.clusterState();
    if (state.status === 'loading') return 'Chargement…';
    if (state.status === 'error') return 'Données indisponibles';
    const nodes = state.snapshot?.nodes ?? [];
    return nodes.filter((n) => n.status === 'online').length + ' / ' + nodes.length + ' en ligne';
  });

  private readonly tracking = inject(ExecutionTracking);
  private readonly executionState = toSignal(
    toObservable(this.pollingDisabled).pipe(
      switchMap((disabled) => (disabled ? of(INITIAL_EXECUTION_TRACKING) : this.tracking.state$)),
    ),
    { initialValue: INITIAL_EXECUTION_TRACKING },
  );
  protected readonly activeExecutionCount = computed(
    () => this.executionState().data?.activeCount ?? 0,
  );
  protected readonly searchResults = computed(() => {
    const query = this.normalizeSearch(this.searchQuery().trim());
    if (!query) return [];

    return getPrimaryNavigationGroups(this.router.config)
      .flatMap((group) => group.items)
      .filter((item) => !item.adminOnly || this.auth.isAdmin())
      .filter((item) =>
        this.normalizeSearch([item.label, ...(item.keywords ?? [])].join(' ')).includes(query),
      )
      .slice(0, 6);
  });

  protected openSearchPalette(): void {
    this.paletteOpen.set(true);
    setTimeout(() => {
      this.paletteSearchInput()?.nativeElement.focus();
    }, 50);
  }

  protected closeSearchPalette(): void {
    this.paletteOpen.set(false);
    this.searchQuery.set('');
  }

  protected onSearchInput(event: Event): void {
    this.searchQuery.set((event.target as HTMLInputElement).value);
  }

  protected onPaletteKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      this.closeSearchPalette();
      return;
    }

    if (event.key === 'ArrowDown' && this.searchResults().length) {
      event.preventDefault();
      queueMicrotask(() => this.firstSearchResult()?.nativeElement.focus());
      return;
    }

    if (event.key === 'Enter') {
      const firstResult = this.searchResults()[0];
      if (firstResult) {
        event.preventDefault();
        this.closeSearchPalette();
        void this.router.navigateByUrl(firstResult.path);
      }
    }
  }

  protected onSearchResultKeydown(event: KeyboardEvent, index: number): void {
    const items = this.searchResultElements();
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      const next = items[index + 1] ?? items[0];
      next?.nativeElement.focus();
      return;
    }
    if (event.key === 'ArrowUp') {
      event.preventDefault();
      if (index === 0) {
        this.paletteSearchInput()?.nativeElement.focus();
      } else {
        items[index - 1]?.nativeElement.focus();
      }
      return;
    }
    if (event.key === 'Escape') {
      event.preventDefault();
      this.closeSearchPalette();
      this.paletteSearchInput()?.nativeElement.focus();
    }
  }

  protected onNavigationClick(): void {
    if (this.isCompactNavigation()) {
      this.navigationOpened.set(false);
    }
  }

  protected onGlobalSearchShortcut(event: KeyboardEvent): void {
    if (
      event.key.toLocaleLowerCase() === 'k' &&
      (event.metaKey || event.ctrlKey) &&
      !this.authMode() &&
      !this.workspaceMode()
    ) {
      event.preventDefault();
      if (this.paletteOpen()) {
        this.closeSearchPalette();
      } else {
        this.openSearchPalette();
      }
    }
  }

  private normalizeSearch(value: string): string {
    return value
      .normalize('NFD')
      .replace(/[\u0300-\u036f]/g, '')
      .toLocaleLowerCase('fr');
  }

  private getCurrentPageContext(): {
    title: string;
    url: string;
    workspaceMode: boolean;
    authMode: boolean;
  } {
    let route = this.router.routerState.snapshot.root;

    while (route.firstChild) {
      route = route.firstChild;
    }

    return {
      url: this.router.url,
      title: (route.data['title'] as string | undefined) ?? 'Vue d’ensemble',
      workspaceMode: route.data['layout'] === 'workspace',
      authMode: route.data['authPage'] !== undefined,
    };
  }
}
