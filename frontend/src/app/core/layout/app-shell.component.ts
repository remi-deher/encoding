import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import { PageTitle } from '../../shared/services/page-title';
import { ClusterOverview, INITIAL_CLUSTER_OVERVIEW } from '../../shared/services/cluster-overview';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { toSignal, toObservable } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { BreakpointObserver } from '@angular/cdk/layout';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatTooltipModule } from '@angular/material/tooltip';
import { filter, map, of, switchMap } from 'rxjs';
import { PrimaryNavigationComponent } from '../navigation/primary-navigation.component';
import { AuthService } from '../auth/auth.service';
import { UserMenuComponent } from '../auth/user-menu.component';
import { ThemeToggleComponent } from '../theme/theme-toggle.component';
import { SearchPaletteDialogComponent } from './search-palette-dialog.component';
import { AiAssistantPageComponent } from '../../features/ai/ai-assistant-page.component';

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatDialogModule,
    MatSidenavModule,
    MatTooltipModule,
    PrimaryNavigationComponent,
    RouterLink,
    RouterOutlet,
    ThemeToggleComponent,
    UserMenuComponent,
    AiAssistantPageComponent,
  ],
  templateUrl: './app-shell.component.html',
  host: {
    '(document:keydown)': 'onGlobalSearchShortcut($event)',
  },
})
export class AppShellComponent {
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private paletteDialogRef: MatDialogRef<SearchPaletteDialogComponent> | null = null;

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
    effect(() => {
      if (this.assistantRouteActive()) this.assistantPanelOpen.set(false);
    });
  }

  private readonly dynamicTitle = inject(PageTitle);
  protected readonly pageTitle = computed(() => {
    const context = this.navigationEnd();
    const override = this.dynamicTitle.current();
    return override?.url === context.url ? override.title : context.title;
  });
  protected readonly sectionGroup = computed(() => this.navigationEnd().group);
  protected readonly subSectionLabel = computed(() => this.navigationEnd().subLabel);
  protected readonly workspaceMode = computed(() => this.navigationEnd().workspaceMode);
  protected readonly authMode = computed(() => this.navigationEnd().authMode);
  protected readonly assistantRouteActive = computed(() =>
    this.navigationEnd().url.startsWith('/ai/assistant'),
  );
  protected readonly showAssistantBubble = computed(
    () =>
      Boolean(this.currentUser()) &&
      !this.authMode() &&
      !this.workspaceMode() &&
      !this.assistantRouteActive(),
  );
  protected readonly assistantWidgetMounted = signal(false);
  protected readonly assistantPanelOpen = signal(false);
  private readonly assistantLauncher =
    viewChild<ElementRef<HTMLButtonElement>>('assistantLauncher');
  private readonly assistantCloseButton =
    viewChild<ElementRef<HTMLButtonElement>>('assistantCloseButton');
  private focusReturnElement: HTMLElement | null = null;

  protected toggleAssistantPanel(): void {
    if (this.assistantPanelOpen()) {
      this.closeAssistantPanel();
      return;
    }
    this.focusReturnElement =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    if (!this.assistantWidgetMounted()) this.assistantWidgetMounted.set(true);
    this.assistantPanelOpen.set(true);
    if (this.isCompactNavigation()) this.navigationOpened.set(false);
    setTimeout(() => this.assistantCloseButton()?.nativeElement.focus());
  }

  protected closeAssistantPanel(): void {
    this.assistantPanelOpen.set(false);
    setTimeout(() => {
      const target = this.focusReturnElement?.isConnected
        ? this.focusReturnElement
        : this.assistantLauncher()?.nativeElement;
      target?.focus();
      this.focusReturnElement = null;
    });
  }

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
    if (state.snapshot?.identity.standalone) return 'Nœud autonome';
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

  protected openSearchPalette(): void {
    if (this.paletteDialogRef) {
      this.paletteDialogRef.close();
      return;
    }
    this.paletteDialogRef = this.dialog.open(SearchPaletteDialogComponent, {
      width: 'min(560px, 94vw)',
      maxWidth: '560px',
      position: { top: '10vh' },
      panelClass: 'search-palette-dialog-panel',
      autoFocus: 'first-tabbable',
      restoreFocus: true,
    });
    this.paletteDialogRef.afterClosed().subscribe(() => {
      this.paletteDialogRef = null;
    });
  }

  protected onNavigationClick(): void {
    if (this.isCompactNavigation()) {
      this.navigationOpened.set(false);
    }
  }

  protected onGlobalSearchShortcut(event: KeyboardEvent): void {
    if (event.key === 'Escape' && this.assistantPanelOpen()) {
      event.preventDefault();
      this.closeAssistantPanel();
      return;
    }
    if (
      event.key.toLocaleLowerCase() === 'k' &&
      (event.metaKey || event.ctrlKey) &&
      !this.authMode()
    ) {
      event.preventDefault();
      this.openSearchPalette();
    }
  }

  private getCurrentPageContext(): {
    title: string;
    group: string;
    subLabel: string;
    url: string;
    workspaceMode: boolean;
    authMode: boolean;
  } {
    let route = this.router.routerState.snapshot.root;

    while (route.firstChild) {
      route = route.firstChild;
    }

    const nav = route.data['navigation'] as
      { group?: string; label?: string; visible?: boolean } | undefined;
    const title = (route.data['title'] as string | undefined) ?? 'Vue d’ensemble';
    const subLabel =
      nav && nav.visible === false && nav.label && nav.label !== title ? nav.label : '';

    return {
      url: this.router.url,
      title,
      group: nav?.group ?? '',
      subLabel,
      workspaceMode: route.data['layout'] === 'workspace',
      authMode: route.data['authPage'] !== undefined,
    };
  }
}
