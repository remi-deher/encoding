import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import {
  ChangeDetectionStrategy,
  Component,
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
import { MatTooltipModule } from '@angular/material/tooltip';
import { filter, map, of, switchMap } from 'rxjs';
import { PrimaryNavigationComponent } from '../navigation/primary-navigation.component';
import { AuthService } from '../auth/auth.service';
import { ThemeToggleComponent } from '../theme/theme-toggle.component';
import { SearchPaletteDialogComponent } from './search-palette-dialog.component';
import { AssistantHostComponent } from './assistant-host.component';
import { PageSystemActionsComponent } from '../../shared/ui/page-system-actions.component';

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatDialogModule,
    MatTooltipModule,
    PrimaryNavigationComponent,
    RouterLink,
    RouterOutlet,
    ThemeToggleComponent,
    AssistantHostComponent,
    PageSystemActionsComponent,
  ],
  templateUrl: './app-shell.component.html',
  host: {
    '(document:keydown)': 'onGlobalSearchShortcut($event)',
  },
})
export class AppShellComponent {
  private readonly mobileNavigation = viewChild<PrimaryNavigationComponent>('mobileNavigation');
  protected readonly assistantHost = viewChild<AssistantHostComponent>('assistantHost');
  protected focusMobileMenu(): void {
    this.mobileNavigation()?.focusMoreButton();
  }
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
  protected readonly isTabletNavigation = toSignal(
    inject(BreakpointObserver)
      .observe('(min-width: 761px) and (max-width: 1100px)')
      .pipe(map((state) => state.matches)),
    { initialValue: false },
  );
  protected readonly navigationSheetOpen = signal(false);
  protected readonly navigationCollapsed = signal(
    typeof localStorage !== 'undefined' ? localStorage.getItem('nav_collapsed') === 'true' : false,
  );

  protected readonly railNavigation = computed(
    () => this.isTabletNavigation() || this.navigationCollapsed(),
  );

  protected toggleNavigationCollapsed(): void {
    if (!this.isCompactNavigation() && !this.isTabletNavigation()) {
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

  protected readonly workspaceMode = computed(() => this.navigationEnd().workspaceMode);
  protected readonly authMode = computed(() => this.navigationEnd().authMode);
  constructor() {
    effect(() => {
      if (!this.isCompactNavigation()) this.navigationSheetOpen.set(false);
    });
  }

  private readonly auth = inject(AuthService);
  protected readonly currentUser = this.auth.user;
  // Aucun appel à l’API de gestion sans session.
  private readonly pollingDisabled = computed(() => this.authMode() || !this.auth.user());
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
    this.navigationSheetOpen.set(false);
  }

  protected onGlobalSearchShortcut(event: KeyboardEvent): void {
    if (event.defaultPrevented) return;
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
      workspaceMode: route.data['layout'] === 'workspace',
      authMode: route.data['authPage'] !== undefined,
    };
  }
}
