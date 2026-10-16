import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import { PageTitle } from '../../shared/services/page-title';
import { ClusterOverview, INITIAL_CLUSTER_OVERVIEW } from '../../shared/services/cluster-overview';
import {
  Component,
  computed,
  ElementRef,
  effect,
  HostListener,
  inject,
  signal,
  ViewChild,
} from '@angular/core';
import { toSignal, toObservable } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { BreakpointObserver } from '@angular/cdk/layout';
import { MatButtonModule } from '@angular/material/button';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
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
  imports: [
    MatButtonModule,
    MatSidenavModule,
    MatToolbarModule,
    PrimaryNavigationComponent,
    RouterLink,
    RouterOutlet,
    ThemeToggleComponent,
    UserMenuComponent,
  ],
  templateUrl: './app-shell.component.html',
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
  protected readonly searchOpen = signal(false);
  @ViewChild('searchInput') private searchInput?: ElementRef<HTMLInputElement>;
  @ViewChild('firstSearchResult') private firstSearchResult?: ElementRef<HTMLAnchorElement>;

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

  protected onSearchInput(event: Event): void {
    this.searchQuery.set((event.target as HTMLInputElement).value);
    this.searchOpen.set(true);
  }

  protected openSearch(): void {
    this.searchOpen.set(true);
  }

  protected onSearchKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      this.searchOpen.set(false);
      (event.target as HTMLInputElement).blur();
      return;
    }

    if (event.key === 'ArrowDown' && this.searchResults().length) {
      event.preventDefault();
      queueMicrotask(() => this.firstSearchResult?.nativeElement.focus());
      return;
    }

    if (event.key === 'Enter') {
      const firstResult = this.searchResults()[0];
      if (firstResult) {
        event.preventDefault();
        this.clearSearch();
        void this.router.navigateByUrl(firstResult.path);
      }
    }
  }

  protected closeSearch(): void {
    this.searchOpen.set(false);
  }

  protected onSearchBlur(event: FocusEvent): void {
    const container = (event.currentTarget as HTMLElement).closest('.global-search');
    if (event.relatedTarget instanceof Node && container?.contains(event.relatedTarget)) return;
    this.closeSearch();
  }

  protected clearSearch(): void {
    this.searchQuery.set('');
    this.searchOpen.set(false);
  }

  @HostListener('document:keydown', ['$event'])
  protected onGlobalSearchShortcut(event: KeyboardEvent): void {
    if (
      event.key.toLocaleLowerCase() === 'k' &&
      (event.metaKey || event.ctrlKey) &&
      !this.authMode() &&
      !this.workspaceMode()
    ) {
      event.preventDefault();
      this.searchOpen.set(true);
      this.searchInput?.nativeElement.focus();
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
