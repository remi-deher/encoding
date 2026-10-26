import { FlowEditorAssistant } from '../../shared/services/flow-editor-assistant.service';
import { FlowAiAssistantPanelComponent } from '../../features/flows/editor/flow-ai-assistant-panel.component';
import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
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
import { ThemeToggleComponent } from '../theme/theme-toggle.component';
import { SearchPaletteDialogComponent } from './search-palette-dialog.component';
import { AiAssistantPageComponent } from '../../features/ai/ai-assistant-page.component';
import { PageSystemActionsComponent } from '../../shared/ui/page-system-actions.component';

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
    AiAssistantPageComponent,
    FlowAiAssistantPanelComponent,
    PageSystemActionsComponent,
  ],
  templateUrl: './app-shell.component.html',
  host: {
    '(document:keydown)': 'onGlobalSearchShortcut($event)',
    '(window:resize)': 'onWindowResize()',
  },
})
export class AppShellComponent {
  protected readonly flowAssistant = inject(FlowEditorAssistant);
  protected readonly generalAssistant = viewChild(AiAssistantPageComponent);
  private readonly mobileNavigation = viewChild<PrimaryNavigationComponent>('mobileNavigation');
  protected readonly flowWidget = viewChild<FlowAiAssistantPanelComponent>('flowWidget');
  protected async applyFlowProposal(
    flow: import('../../shared/models/flow-validation').FlowDocument,
  ): Promise<void> {
    const widget = this.flowWidget();
    if (await this.flowAssistant.apply(flow)) {
      widget?.acknowledgeProposal();
      this.closeAssistantPanel();
    }
  }

  private static readonly assistantLauncherStorageKey = 'encoding.assistant-launcher.position';
  private static readonly assistantLauncherGrid = 24;
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
  protected readonly navigationSheetOpen = signal(false);
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
    afterNextRender(() => this.onWindowResize());
    effect(() => {
      if (this.flowAssistant.session()) {
        this.assistantWidgetMounted.set(true);
        this.assistantPanelOpen.set(true);
        this.updateAssistantPanelGeometry();
      }
    });
    effect(() => {
      const compact = this.isCompactNavigation();
      const context = this.navigationEnd();
      this.navigationOpened.set(!compact && !context.authMode && !context.workspaceMode);
    });
    effect(() => {
      if (!this.isCompactNavigation()) this.navigationSheetOpen.set(false);
    });
    effect(() => {
      if (this.assistantRouteActive()) this.assistantPanelOpen.set(false);
    });
  }

  protected readonly workspaceMode = computed(() => this.navigationEnd().workspaceMode);
  protected readonly authMode = computed(() => this.navigationEnd().authMode);
  protected readonly assistantRouteActive = computed(() =>
    this.navigationEnd().url.startsWith('/ai/assistant'),
  );
  protected readonly showAssistantBubble = computed(
    () => Boolean(this.currentUser()) && !this.authMode() && !this.assistantRouteActive(),
  );
  protected readonly assistantWidgetMounted = signal(false);
  protected readonly generalAssistantMounted = signal(false);
  protected readonly assistantPanelOpen = signal(false);
  private readonly assistantPanelOpening = signal(false);
  protected readonly assistantPanelGeometry = signal<{
    left: number;
    top: number;
    originX: number;
    originY: number;
    scaleX: number;
    scaleY: number;
  } | null>(null);
  protected readonly assistantLauncherPosition = signal(this.readAssistantLauncherPosition());
  protected readonly assistantLauncherDragging = signal(false);
  private readonly assistantLauncher = viewChild('assistantLauncher', {
    read: ElementRef<HTMLButtonElement>,
  });
  private readonly assistantCloseButton = viewChild('assistantCloseButton', {
    read: ElementRef<HTMLButtonElement>,
  });
  private focusReturnElement: HTMLElement | null = null;
  private assistantDrag: {
    readonly pointerId: number;
    readonly pointerX: number;
    readonly pointerY: number;
    readonly left: number;
    readonly top: number;
    moved: boolean;
  } | null = null;
  private suppressAssistantClick = false;

  protected startAssistantLauncherDrag(event: PointerEvent): void {
    if (event.button !== 0 || !this.assistantLauncher()) return;
    const rect = this.assistantLauncher()!.nativeElement.getBoundingClientRect();
    this.assistantDrag = {
      pointerId: event.pointerId,
      pointerX: event.clientX,
      pointerY: event.clientY,
      left: rect.left,
      top: rect.top,
      moved: false,
    };
    this.assistantLauncher()!.nativeElement.setPointerCapture(event.pointerId);
  }

  protected moveAssistantLauncher(event: PointerEvent): void {
    const drag = this.assistantDrag;
    if (!drag || drag.pointerId !== event.pointerId) return;
    const deltaX = event.clientX - drag.pointerX;
    const deltaY = event.clientY - drag.pointerY;
    if (!drag.moved && Math.hypot(deltaX, deltaY) < 5) return;
    drag.moved = true;
    this.assistantLauncherDragging.set(true);
    event.preventDefault();
    this.setAssistantLauncherPosition(drag.left + deltaX, drag.top + deltaY, false);
  }

  protected finishAssistantLauncherDrag(event: PointerEvent): void {
    const drag = this.assistantDrag;
    if (!drag || drag.pointerId !== event.pointerId) return;
    this.assistantDrag = null;
    this.assistantLauncherDragging.set(false);
    if (drag.moved) {
      this.saveAssistantLauncherPosition();
      this.suppressAssistantClick = true;
      setTimeout(() => (this.suppressAssistantClick = false));
    }
    const button = this.assistantLauncher()?.nativeElement;
    if (button?.hasPointerCapture(event.pointerId)) button.releasePointerCapture(event.pointerId);
  }

  protected onAssistantLauncherClick(event: MouseEvent): void {
    if (this.suppressAssistantClick) {
      event.preventDefault();
      event.stopPropagation();
      this.suppressAssistantClick = false;
      return;
    }
    if (this.flowAssistant.hasEditor() && !this.assistantPanelOpen()) {
      if (this.flowAssistant.session()) {
        this.updateAssistantPanelGeometry();
        this.assistantPanelOpen.set(true);
      } else void this.flowAssistant.open();
      return;
    }
    this.toggleAssistantPanel();
  }

  protected onAssistantLauncherKeydown(event: KeyboardEvent): void {
    if (!event.altKey || !event.key.startsWith('Arrow')) return;
    const rect = this.assistantLauncher()?.nativeElement.getBoundingClientRect();
    if (!rect) return;
    const step = AppShellComponent.assistantLauncherGrid;
    const delta = {
      ArrowLeft: [-step, 0],
      ArrowRight: [step, 0],
      ArrowUp: [0, -step],
      ArrowDown: [0, step],
    }[event.key];
    if (!delta) return;
    event.preventDefault();
    this.setAssistantLauncherPosition(rect.left + delta[0], rect.top + delta[1], true);
  }

  protected onWindowResize(): void {
    const position = this.assistantLauncherPosition();
    if (position) this.setAssistantLauncherPosition(position.left, position.top, true);
    if (this.assistantPanelOpen()) this.updateAssistantPanelGeometry();
  }

  private setAssistantLauncherPosition(left: number, top: number, persist: boolean): void {
    const button = this.assistantLauncher()?.nativeElement;
    const margin = 16;
    const maxLeft = Math.max(margin, window.innerWidth - (button?.offsetWidth ?? 56) - margin);
    const bottomClearance =
      this.isCompactNavigation() && !this.authMode() && !this.workspaceMode() ? 84 : margin;
    const maxTop = Math.max(
      margin,
      window.innerHeight - (button?.offsetHeight ?? 56) - bottomClearance,
    );
    const grid = AppShellComponent.assistantLauncherGrid;
    const snap = (value: number, max: number) =>
      Math.min(max, Math.max(margin, Math.round((value - margin) / grid) * grid + margin));
    this.assistantLauncherPosition.set({ left: snap(left, maxLeft), top: snap(top, maxTop) });
    if (persist) this.saveAssistantLauncherPosition();
  }

  private saveAssistantLauncherPosition(): void {
    try {
      const position = this.assistantLauncherPosition();
      if (position)
        localStorage.setItem(
          AppShellComponent.assistantLauncherStorageKey,
          JSON.stringify(position),
        );
    } catch {
      // Position persistence is optional when storage is unavailable.
    }
  }

  private readAssistantLauncherPosition(): { left: number; top: number } | null {
    try {
      const value = localStorage.getItem(AppShellComponent.assistantLauncherStorageKey);
      if (!value) return null;
      const position: unknown = JSON.parse(value);
      if (
        typeof position === 'object' &&
        position !== null &&
        'left' in position &&
        'top' in position &&
        typeof position.left === 'number' &&
        Number.isFinite(position.left) &&
        typeof position.top === 'number' &&
        Number.isFinite(position.top)
      )
        return { left: position.left, top: position.top };
    } catch {
      // Fall back to the default bottom-right position.
    }
    return null;
  }

  protected toggleAssistantPanel(): void {
    if (this.assistantPanelOpening()) return;
    if (this.assistantPanelOpen()) {
      this.closeAssistantPanel();
      return;
    }
    if (!this.flowAssistant.session()) this.generalAssistantMounted.set(true);
    this.focusReturnElement =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    if (this.isCompactNavigation()) this.navigationOpened.set(false);
    this.updateAssistantPanelGeometry();
    if (!this.assistantWidgetMounted()) {
      this.assistantPanelOpening.set(true);
      this.assistantWidgetMounted.set(true);
      requestAnimationFrame(() => requestAnimationFrame(() => this.activateAssistantPanel()));
      return;
    }
    this.activateAssistantPanel();
  }

  private activateAssistantPanel(): void {
    this.assistantPanelOpening.set(false);
    this.assistantPanelOpen.set(true);
    setTimeout(() => this.assistantCloseButton()?.nativeElement.focus());
  }

  private updateAssistantPanelGeometry(): void {
    const button = this.assistantLauncher()?.nativeElement;
    if (!button) return;

    const viewportWidth = window.innerWidth;
    const viewportHeight = window.innerHeight;
    const panelWidth = Math.min(
      this.flowAssistant.session() ? 640 : 380,
      Math.max(1, viewportWidth - 32),
    );
    const panelHeight = Math.min(
      this.flowAssistant.session() ? 840 : 520,
      Math.max(1, viewportHeight - 112),
    );
    const margin = 16;
    if (this.isCompactNavigation()) {
      const dockClearance = 84;
      const left = Math.max(margin, viewportWidth - panelWidth - margin);
      const top = Math.max(margin, viewportHeight - panelHeight - dockClearance);
      const anchorSize = 48;
      this.assistantPanelGeometry.set({
        left,
        top,
        originX: Math.max(0, panelWidth - anchorSize),
        originY: Math.max(0, panelHeight - anchorSize),
        scaleX: Math.min(1, anchorSize / panelWidth),
        scaleY: Math.min(1, anchorSize / panelHeight),
      });
      return;
    }

    const launcher = button.getBoundingClientRect();
    const gap = 16;
    const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));
    const maxLeft = Math.max(margin, viewportWidth - panelWidth - margin);
    const maxTop = Math.max(margin, viewportHeight - panelHeight - margin);
    const left = clamp(launcher.right - panelWidth, margin, maxLeft);
    let top = launcher.top - panelHeight - gap;
    if (top < margin) {
      const below = launcher.bottom + gap;
      top = below <= maxTop ? below : clamp(top, margin, maxTop);
    }
    const originX = clamp(launcher.left + launcher.width / 2 - left, 0, panelWidth);
    const originY = clamp(launcher.top + launcher.height / 2 - top, 0, panelHeight);

    this.assistantPanelGeometry.set({
      left,
      top,
      originX,
      originY,
      scaleX: Math.min(1, launcher.width / panelWidth),
      scaleY: Math.min(1, launcher.height / panelHeight),
    });
  }

  protected closeAssistantPanel(): void {
    this.assistantPanelOpen.set(false);
    setTimeout(() => {
      if (this.focusReturnElement?.isConnected) {
        this.focusReturnElement.focus();
      } else if (this.isCompactNavigation()) {
        this.mobileNavigation()?.focusMoreButton();
      } else {
        this.assistantLauncher()?.nativeElement.focus();
      }
      this.focusReturnElement = null;
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
    if (this.isCompactNavigation()) {
      this.navigationOpened.set(false);
    }
  }

  protected onGlobalSearchShortcut(event: KeyboardEvent): void {
    if (event.defaultPrevented) return;
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
