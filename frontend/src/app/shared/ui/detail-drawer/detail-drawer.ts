import { MatButtonModule } from '@angular/material/button';
import { DOCUMENT, NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  TemplateRef,
  effect,
  inject,
  input,
  output,
} from '@angular/core';
import { MatSidenavModule } from '@angular/material/sidenav';

@Component({
  selector: 'app-detail-drawer',
  host: { '[class.drawer-fill]': 'fill()' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatSidenavModule, NgTemplateOutlet],
  template: `
    <mat-drawer-container class="detail-drawer-container" [hasBackdrop]="modal()">
      <mat-drawer-content [style.overflow]="mainScroll() === 'parent' ? 'visible' : null">
        <ng-content select="[drawer-main]" />
      </mat-drawer-content>
      <mat-drawer
        #drawer
        class="detail-drawer-panel"
        [class.detail-drawer-panel_standard]="size() === 'standard'"
        position="end"
        [mode]="mode()"
        [opened]="opened()"
        [disableClose]="!dismissible()"
        [attr.role]="modal() ? 'dialog' : 'complementary'"
        [attr.aria-modal]="modal() ? 'true' : null"
        [attr.aria-label]="title()"
        (closed)="closed.emit()"
      >
        <section class="drawer-panel">
          <header class="drawer-header">
            <div class="drawer-heading">
              @if (eyebrow()) {
                <p class="drawer-eyebrow">{{ eyebrow() }}</p>
              }
              <h2>{{ title() }}</h2>
              @if (description()) {
                <p>{{ description() }}</p>
              }
            </div>
            <div class="drawer-header-controls">
              <ng-container [ngTemplateOutlet]="headerActions() ?? null" />
              <button
                mat-icon-button
                class="drawer-close"
                type="button"
                aria-label="Fermer le panneau"
                [disabled]="!dismissible()"
                (click)="drawer.close()"
              >
                <span aria-hidden="true">×</span>
              </button>
            </div>
          </header>
          <div class="drawer-body">
            <ng-container [ngTemplateOutlet]="content() ?? null" />
          </div>
          @if (footerActions(); as footerTemplate) {
            <footer class="drawer-footer">
              <ng-container [ngTemplateOutlet]="footerTemplate" />
            </footer>
          }
        </section>
      </mat-drawer>
    </mat-drawer-container>
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
    }

    :host(.drawer-fill) {
      height: 100%;
      min-height: 0;
    }
    :host(.drawer-fill) .detail-drawer-container,
    :host(.drawer-fill) mat-drawer-content {
      height: 100%;
    }
    :host(.drawer-fill) .detail-drawer-container.mat-drawer-container {
      position: relative;
      overflow: hidden;
      z-index: auto;
    }
    :host(.drawer-fill) .detail-drawer-panel.mat-drawer {
      position: absolute;
      width: min(560px, 100%);
      max-width: 100%;
    }
    :host(.drawer-fill) .drawer-panel {
      height: 100%;
    }
    .detail-drawer-container {
      width: 100%;
    }

    .detail-drawer-panel {
      width: min(1000px, 82vw);
      max-width: 100vw;
      border-start-start-radius: var(--radius-2xl);
      border-end-start-radius: var(--radius-2xl);
      overflow: hidden;
    }

    .detail-drawer-panel_standard {
      width: min(560px, 100vw);
    }

    .drawer-panel {
      height: 100%;
      min-height: 100%;
      max-height: 100%;
      display: flex;
      flex-direction: column;
      box-sizing: border-box;
      background: var(--surface-container-lowest);
    }

    .drawer-header {
      flex: 0 0 auto;
      min-height: 64px;
      padding: var(--space-4) var(--space-5);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      background: var(--surface-container-low);
    }

    .drawer-heading {
      flex: 1;
      min-width: 0;
      overflow-wrap: break-word;
    }

    .drawer-heading h2 {
      margin: 0;
      color: var(--text-primary);
      font-size: clamp(18px, 2.2vw, 24px);
      font-weight: 700;
      letter-spacing: -0.02em;
      line-height: 1.25;
      word-break: normal;
      overflow-wrap: break-word;
    }

    .drawer-heading p {
      margin: 5px 0 0;
      color: var(--text-muted);
      font-size: var(--font-body);
      line-height: 1.45;
    }

    .drawer-heading .drawer-eyebrow {
      margin: 0 0 5px;
      color: var(--accent-strong);
      font-size: var(--font-label);
      font-weight: 700;
      letter-spacing: 0.08em;
      text-transform: uppercase;
    }

    .drawer-header-controls {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      flex: 0 0 auto;
      margin-inline-start: auto;
    }

    .drawer-close {
      flex: 0 0 auto;
      color: var(--text-secondary);
    }

    .drawer-body {
      min-height: 0;
      padding: var(--space-5);
      flex: 1 1 auto;
      overflow: auto;
      overscroll-behavior: contain;
    }

    .drawer-footer {
      padding: var(--space-3) var(--space-5);
      display: flex;
      align-items: center;
      justify-content: flex-end;
      gap: var(--space-2);
      background: var(--surface-container-low);
    }

    @media (max-width: 700px) {
      .detail-drawer-panel,
      .detail-drawer-panel_standard {
        width: 100vw;
        max-width: 100vw;
      }

      .drawer-panel {
        height: 100dvh;
      }

      .drawer-header {
        min-height: 72px;
        padding: var(--space-3) var(--space-4);
        gap: var(--space-3);
      }

      .drawer-header-controls {
        align-self: flex-start;
      }

      .drawer-body {
        padding: var(--space-4);
      }

      .drawer-footer {
        padding: var(--space-3) var(--space-4);
        padding-bottom: max(var(--space-3), env(safe-area-inset-bottom));
      }
    }
  `,
})
export class DetailDrawerComponent {
  private readonly doc = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);

  /** `standard` : tiroir de 560 px pour une fiche ; `wide` : jusqu'à 1 000 px pour un éditeur. */
  readonly fill = input(false);
  readonly mode = input<'side' | 'over'>('over');
  readonly size = input<'standard' | 'wide'>('wide');
  /** Avec un voile, un clic extérieur ferme le tiroir si celui-ci est dismissible. */
  readonly modal = input(false);
  /** Désactiver Escape et la fermeture par voile pendant une saisie à protéger. */
  readonly dismissible = input(true);
  readonly mainScroll = input<'auto' | 'parent'>('auto');
  readonly opened = input(false);
  readonly eyebrow = input('');
  readonly title = input('Détails');
  readonly description = input('');
  readonly content = input<TemplateRef<unknown> | undefined>(undefined);
  /** Pied fixe : à fournir pour les actions d'un formulaire (Annuler / Enregistrer). */
  readonly footerActions = input<TemplateRef<unknown> | undefined>(undefined);
  readonly headerActions = input<TemplateRef<unknown> | undefined>(undefined);
  readonly closed = output<void>();

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.doc.body?.classList.remove('detail-drawer-open');
      const sidebar = this.doc.querySelector('.application-sidebar');
      if (sidebar) {
        sidebar.removeAttribute('inert');
      }
    });

    effect(() => {
      const isModalOpen = this.modal() && this.opened();
      this.doc.body?.classList.toggle('detail-drawer-open', isModalOpen);
      const sidebar = this.doc.querySelector('.application-sidebar');
      if (sidebar) {
        sidebar.toggleAttribute('inert', isModalOpen);
      }
    });
  }
}
