import { MatButtonModule } from '@angular/material/button';
import { NgTemplateOutlet } from '@angular/common';
import { Component, TemplateRef, input, output } from '@angular/core';
import { MatSidenavModule } from '@angular/material/sidenav';

@Component({
  selector: 'app-detail-drawer',
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
        mode="over"
        [opened]="opened()"
        [attr.aria-label]="title()"
        (closed)="closed.emit()"
      >
        <section class="drawer-panel">
          <header class="drawer-header">
            <div class="drawer-heading">
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
                (click)="drawer.close()"
              >
                <span aria-hidden="true">×</span>
              </button>
            </div>
          </header>
          <div class="drawer-body">
            <ng-container [ngTemplateOutlet]="content() ?? null" />
          </div>
        </section>
      </mat-drawer>
    </mat-drawer-container>
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
    }

    .detail-drawer-container {
      width: 100%;
    }

    .drawer-panel {
      height: 100dvh;
      display: flex;
      flex-direction: column;
      background: var(--surface);
    }

    .drawer-header {
      min-height: 72px;
      padding: var(--space-4) var(--space-5);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 20px;
      border-bottom: 1px solid var(--border-subtle);
    }

    .drawer-heading {
      flex: 1;
      min-width: 0;
      overflow-wrap: anywhere;
    }

    .drawer-header-controls {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      flex: 0 0 auto;
      margin-left: auto;
    }

    .drawer-heading h2 {
      margin: 0;
      color: var(--text-primary);
      font-size: var(--font-title-md);
      font-weight: 700;
    }

    .drawer-heading p {
      margin: 5px 0 0;
      color: var(--text-muted);
      font-size: var(--font-body);
    }

    .drawer-body {
      min-height: 0;
      padding: var(--space-5);
      flex: 1;
      overflow: auto;
    }

    @media (max-width: 700px) {
      .drawer-header {
        min-height: 64px;
        padding-inline: var(--space-4);
        flex-wrap: wrap;
        gap: var(--space-3);
      }
      .drawer-heading {
        flex-basis: 100%;
      }

      .drawer-body {
        padding: var(--space-4);
      }
    }
  `,
})
export class DetailDrawerComponent {
  /** `standard` : tiroir de 560 px pour une fiche ; `wide` : jusqu'à 1 000 px pour un éditeur. */
  readonly size = input<'standard' | 'wide'>('wide');
  /** Avec un voile : un clic à l'extérieur ferme le tiroir. À réserver aux fiches sans brouillon à perdre. */
  readonly modal = input(false);
  readonly mainScroll = input<'auto' | 'parent'>('auto');
  readonly opened = input(false);
  readonly title = input('Détails');
  readonly description = input('');
  readonly content = input<TemplateRef<unknown> | undefined>(undefined);
  readonly headerActions = input<TemplateRef<unknown> | undefined>(undefined);
  readonly closed = output<void>();
}
