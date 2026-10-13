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
  readonly message: string;
  readonly confirmLabel?: string;
}

@Component({
  selector: 'app-confirmation-dialog',
  imports: [MatDialogActions, MatDialogContent, MatDialogTitle],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>{{ data.message }}</mat-dialog-content>
    <mat-dialog-actions align="end">
      <button class="cancel-button" type="button" (click)="dialogRef.close(false)">Annuler</button>
      <button class="confirm-button" type="button" (click)="dialogRef.close(true)">
        {{ data.confirmLabel ?? 'Confirmer' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    h2 {
      color: var(--text-primary);
      font-size: 17px;
      font-weight: 700;
    }

    mat-dialog-content {
      color: var(--text-secondary);
      font-size: 13px;
      line-height: 1.6;
    }

    mat-dialog-actions {
      gap: 8px;
      padding: 12px 24px 20px;
    }

    button {
      min-height: 36px;
      padding: 0 13px;
      border: 1px solid var(--border-subtle);
      border-radius: 7px;
      background: var(--surface);
      color: var(--text-primary);
      font: inherit;
      font-weight: 650;
      cursor: pointer;
    }

    .confirm-button {
      border-color: var(--accent-strong);
      background: var(--accent-strong);
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
}
