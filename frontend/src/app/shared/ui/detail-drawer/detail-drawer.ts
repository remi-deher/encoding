import { NgTemplateOutlet } from '@angular/common';
import { Component, TemplateRef, effect, inject, input, output } from '@angular/core';
import { MatDialog, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';

interface DetailDrawerData {
  readonly title: string;
  readonly description: string;
  readonly content: TemplateRef<unknown>;
}

@Component({
  selector: 'app-detail-drawer',
  template: '<ng-content select="[drawer-main]" />',
})
export class DetailDrawerComponent {
  readonly opened = input(false);
  readonly title = input('Détails');
  readonly description = input('');
  readonly content = input<TemplateRef<unknown> | undefined>(undefined);
  readonly closed = output<void>();

  private readonly dialog = inject(MatDialog);
  private dialogRef: MatDialogRef<DetailDrawerPanelComponent> | undefined;

  constructor() {
    effect(() => {
      const opened = this.opened();
      const content = this.content();

      if (!opened) {
        this.dialogRef?.close();
        return;
      }

      if (!content || this.dialogRef) {
        return;
      }

      const dialogRef = this.dialog.open(DetailDrawerPanelComponent, {
        data: {
          title: this.title(),
          description: this.description(),
          content,
        } satisfies DetailDrawerData,
        width: '80vw',
        maxWidth: '100vw',
        height: '100dvh',
        maxHeight: '100dvh',
        position: { top: '0', right: '0' },
        panelClass: 'detail-drawer-panel',
        backdropClass: 'detail-drawer-backdrop',
        autoFocus: 'first-tabbable',
        restoreFocus: true,
      });

      this.dialogRef = dialogRef;
      dialogRef.afterClosed().subscribe(() => {
        if (this.dialogRef === dialogRef) {
          this.dialogRef = undefined;
        }
        this.closed.emit();
      });
    });
  }
}

@Component({
  imports: [NgTemplateOutlet],
  template: `
    <section class="drawer-panel">
      <header class="drawer-header">
        <div class="drawer-heading">
          <h2 id="detail-drawer-title">{{ data.title }}</h2>
          @if (data.description) {
            <p>{{ data.description }}</p>
          }
        </div>
        <button class="drawer-close" type="button" aria-label="Fermer le panneau" (click)="close()">
          <span aria-hidden="true">×</span>
        </button>
      </header>
      <div class="drawer-body">
        <ng-container [ngTemplateOutlet]="data.content" />
      </div>
    </section>
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
      height: 100%;
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
      min-width: 0;
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
      font-size: 12px;
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
      }

      .drawer-body {
        padding: 16px;
      }
    }
  `,
})
class DetailDrawerPanelComponent {
  protected readonly data = inject<DetailDrawerData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<DetailDrawerPanelComponent>);

  protected close(): void {
    this.dialogRef.close();
  }
}
