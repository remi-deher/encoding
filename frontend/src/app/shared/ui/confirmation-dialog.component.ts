import { Component, inject } from '@angular/core';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';

export interface ConfirmationDialogData {
  readonly title: string;
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
  selector: 'app-confirmation-dialog',
  imports: [MatDialogActions, MatDialogContent, MatDialogTitle],
  template: `
    <div class="confirmation-dialog">
    <header class="confirmation-header" [class.confirmation-header_colored]="!!data.headerColor" [style.background-color]="data.headerColor ?? 'var(--surface-muted)'">
      <h2 mat-dialog-title>{{ data.title }}</h2>
    </header>
    <mat-dialog-content>
      @if (data.message) { <p class="dialog-message">{{ data.message }}</p> }
      @if (data.items?.length) {
        <ul class="information-list">
          @for (item of data.items; track item.label) {
            <li>
              <strong>{{ item.label }}</strong>
              @if (item.detail) { <span>{{ item.detail }}</span> }
            </li>
          }
        </ul>
      } @else if (data.emptyMessage) {
        <p class="empty-message">{{ data.emptyMessage }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      @if (data.mode === 'information') {
        <button class="confirm-button" type="button" [style.background-color]="data.confirmColor ?? 'var(--accent-strong)'" [style.border-color]="data.confirmColor ?? 'var(--accent-strong)'" (click)="dialogRef.close(true)">
          {{ data.closeLabel ?? 'Fermer' }}
        </button>
      } @else {
        <button class="cancel-button" type="button" (click)="dialogRef.close(false)">Annuler</button>
        <button class="confirm-button" type="button" [style.background-color]="data.confirmColor ?? 'var(--accent-strong)'" [style.border-color]="data.confirmColor ?? 'var(--accent-strong)'" (click)="dialogRef.close(true)">
          {{ data.confirmLabel ?? 'Confirmer' }}
        </button>
      }
    </mat-dialog-actions>
    </div>
  `,
  styles: `
    :host { display: block; max-width: 100%; }

    .confirmation-dialog { width: max-content; max-width: 100%; }

    .confirmation-header {
      margin: 0 0 var(--space-4);
      padding: var(--space-4) 20px;
      border-radius: var(--radius-lg);
      background: var(--surface-muted);
    }

    h2 {
      color: var(--text-primary);
      font-size: var(--font-title);
      font-weight: 700;
      line-height: 1.35;
      margin: 0;
      padding: 0;
      text-align: start;
      overflow-wrap: anywhere;
    }

    .confirmation-header_colored h2 {
      color: #fff;
    }

    mat-dialog-content {
      max-height: none;
      margin: 0;
      padding: 0 var(--space-2);
      overflow: visible;
      color: var(--text-secondary);
      font-size: var(--font-body);
      line-height: 1.6;
    }

    .dialog-message { margin: 0; }

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

    .information-list strong { color: var(--text-primary); }
    .information-list span, .empty-message { color: var(--text-secondary); }
    .information-list span { overflow-wrap: anywhere; }
    .empty-message { margin: 0; }

    mat-dialog-actions {
      gap: var(--space-2);
      padding: var(--space-3) var(--space-2) var(--space-1);
    }

    button {
      min-height: 36px;
      padding: 0 13px;
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-sm);
      background: var(--surface);
      color: var(--text-primary);
      font: inherit;
      font-weight: 650;
      cursor: pointer;
    }

    .confirm-button {
      color: #fff;
    }

    button:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: 2px;
    }
  `,
})
export class ConfirmationDialogComponent {
  protected readonly data = inject<ConfirmationDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<ConfirmationDialogComponent, boolean>);

  constructor() {
    this.dialogRef.addPanelClass('confirmation-dialog-panel');
    this.dialogRef.updateSize('max-content');
  }
}
