import { NgTemplateOutlet } from '@angular/common';
import { Component, TemplateRef, input, output } from '@angular/core';
import { MatSidenavModule } from '@angular/material/sidenav';

@Component({
  selector: 'app-detail-drawer',
  imports: [MatSidenavModule, NgTemplateOutlet],
  template: `
    <mat-drawer-container class="detail-drawer-container" [hasBackdrop]="false">
      <mat-drawer-content [style.overflow]="mainScroll() === 'parent' ? 'visible' : null">
        <ng-content select="[drawer-main]" />
      </mat-drawer-content>
      <mat-drawer
        #drawer
        class="detail-drawer-panel"
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
      padding: 16px 24px;
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
      gap: 8px;
      flex: 0 0 auto;
      margin-left: auto;
    }

    .drawer-heading h2 {
      margin: 0;
      color: var(--text-primary);
      font-size: 18px;
      font-weight: 700;
    }

    .drawer-heading p {
      margin: 5px 0 0;
      color: var(--text-muted);
      font-size: 14px;
    }

    .drawer-close {
      width: 36px;
      height: 36px;
      flex: 0 0 auto;
      display: grid;
      place-items: center;
      border: 1px solid var(--border-subtle);
      border-radius: 8px;
      background: var(--surface);
      color: var(--text-secondary);
      font: inherit;
      font-size: 22px;
      line-height: 1;
      cursor: pointer;
    }

    .drawer-close:hover {
      border-color: var(--accent);
      color: var(--accent-strong);
    }

    .drawer-close:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: 2px;
    }

    .drawer-body {
      min-height: 0;
      padding: 24px;
      flex: 1;
      overflow: auto;
    }

    @media (max-width: 700px) {
      .drawer-header {
        min-height: 64px;
        padding-inline: 16px;
        flex-wrap: wrap;
        gap: 12px;
      }
      .drawer-heading {
        flex-basis: 100%;
      }

      .drawer-body {
        padding: 16px;
      }
    }
  `,
})
export class DetailDrawerComponent {
  readonly mainScroll = input<'auto' | 'parent'>('auto');
  readonly opened = input(false);
  readonly title = input('Détails');
  readonly description = input('');
  readonly content = input<TemplateRef<unknown> | undefined>(undefined);
  readonly headerActions = input<TemplateRef<unknown> | undefined>(undefined);
  readonly closed = output<void>();
}
