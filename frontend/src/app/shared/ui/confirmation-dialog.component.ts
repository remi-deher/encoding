import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';

export interface ConfirmationDialogData {
  readonly title: string;
  readonly eyebrow?: string;
  readonly message?: string;
  readonly confirmLabel?: string;
  readonly headerColor?: string;
  readonly confirmColor?: string;
  readonly mode?: 'confirmation' | 'information';
  readonly closeLabel?: string;
  readonly items?: readonly { readonly label: string; readonly detail?: string }[];
  readonly emptyMessage?: string;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-confirmation-dialog',
  imports: [MatButtonModule, MatDialogActions, MatDialogContent, MatDialogTitle],
  template: `
    <section class="app-dialog confirmation-dialog">
      <header class="app-dialog-header">
        <span class="app-dialog-icon" [class.app-dialog-icon_danger]="isDanger" aria-hidden="true">
          {{ data.mode === 'information' ? 'i' : isDanger ? '!' : '?' }}
        </span>
        <div class="app-dialog-heading">
          <p class="app-dialog-eyebrow">
            {{ data.eyebrow ?? (data.mode === 'information' ? 'Information' : 'Confirmation') }}
          </p>
          <h2 mat-dialog-title>{{ data.title }}</h2>
        </div>
      </header>

      <mat-dialog-content class="app-dialog-body confirmation-dialog-content">
        @if (data.message) {
          <p class="dialog-message">{{ data.message }}</p>
        }
        @if (data.items?.length) {
          <ul class="information-list">
            @for (item of data.items; track $index) {
              <li>
                <strong>{{ item.label }}</strong>
                @if (item.detail) {
                  <span>{{ item.detail }}</span>
                }
              </li>
            }
          </ul>
        } @else if (data.emptyMessage) {
          <p class="empty-message">{{ data.emptyMessage }}</p>
        }
      </mat-dialog-content>

      <mat-dialog-actions class="app-dialog-footer" align="end">
        @if (data.mode === 'information') {
          <button
            matButton="filled"
            type="button"
            [style.--mat-button-filled-container-color]="data.confirmColor ?? null"
            [style.--mat-button-filled-label-text-color]="
              data.confirmColor ? 'var(--on-danger)' : null
            "
            (click)="dialogRef.close(true)"
          >
            {{ data.closeLabel ?? 'Fermer' }}
          </button>
        } @else {
          <button matButton="text" type="button" (click)="dialogRef.close(false)">Annuler</button>
          <button
            matButton="filled"
            type="button"
            [style.--mat-button-filled-container-color]="data.confirmColor ?? null"
            [style.--mat-button-filled-label-text-color]="
              data.confirmColor ? 'var(--on-danger)' : null
            "
            (click)="dialogRef.close(true)"
          >
            {{ data.confirmLabel ?? 'Confirmer' }}
          </button>
        }
      </mat-dialog-actions>
    </section>
  `,
  styles: `
    .confirmation-dialog-content {
      line-height: 1.6;
    }

    .dialog-message {
      margin: 0;
      color: var(--text-secondary);
    }

    .information-list {
      margin: var(--space-3) 0 0;
      padding: 0;
      display: grid;
      gap: var(--space-2);
      list-style: none;
    }

    .information-list li {
      padding: 10px var(--space-3);
      display: grid;
      gap: 3px;
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-md);
      background: var(--surface-muted);
    }

    .information-list strong {
      color: var(--text-primary);
    }

    .information-list span,
    .empty-message {
      color: var(--text-secondary);
    }

    .information-list span {
      overflow-wrap: anywhere;
    }

    .empty-message {
      margin: 0;
    }
  `,
})
export class ConfirmationDialogComponent {
  protected readonly data = inject<ConfirmationDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<ConfirmationDialogComponent, boolean>);
  protected readonly isDanger = Boolean(this.data.confirmColor || this.data.headerColor);

  constructor() {
    this.dialogRef.addPanelClass(['app-dialog-panel', 'confirmation-dialog-panel']);
    this.dialogRef.updateSize('480px');
  }
}
