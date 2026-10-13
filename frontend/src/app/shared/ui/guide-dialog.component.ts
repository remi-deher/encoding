import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogActions, MatDialogContent, MatDialogTitle, MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';

export interface GuideDialogData {
  readonly title: string;
  readonly description: string;
  readonly steps: readonly string[];
  readonly note?: string;
}

@Component({
  selector: 'app-guide-dialog',
  imports: [MatButtonModule, MatDialogActions, MatDialogContent, MatDialogTitle],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p class="description">{{ data.description }}</p>
      <ol>
        @for (step of data.steps; track $index) { <li>{{ step }}</li> }
      </ol>
      @if (data.note) { <p class="note" role="note">{{ data.note }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button matButton="filled" type="button" (click)="dialogRef.close()">Compris</button></mat-dialog-actions>
  `,
  styles: `
    h2 { color: var(--text-primary); font-size: 18px; font-weight: 700; }
    mat-dialog-content { color: var(--text-secondary); line-height: 1.55; }
    .description { margin-top: 0; }
    ol { display: grid; gap: 10px; padding-left: 22px; }
    li { padding-left: 4px; }
    .note { padding: 12px; border-radius: 8px; background: var(--surface-muted); }
  `,
})
export class GuideDialogComponent {
  protected readonly data = inject<GuideDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<GuideDialogComponent>);
}
