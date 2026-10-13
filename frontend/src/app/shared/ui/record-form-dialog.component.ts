import { Component, inject } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogActions, MatDialogContent, MatDialogRef, MatDialogTitle, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

export interface RecordFormField {
  readonly id: string;
  readonly label: string;
  readonly type: 'text' | 'select';
  readonly required?: boolean;
  readonly maxLength?: number;
  readonly options?: readonly { readonly value: string; readonly label: string }[];
}

export interface RecordFormDialogData {
  readonly title: string;
  readonly description: string;
  readonly submitLabel: string;
  readonly fields: readonly RecordFormField[];
}

@Component({
  selector: 'app-record-form-dialog',
  imports: [MatButtonModule, MatDialogActions, MatDialogContent, MatDialogTitle, MatFormFieldModule, MatInputModule, MatSelectModule, ReactiveFormsModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p class="description">{{ data.description }}</p>
      @if (!reviewing) {
        <form [formGroup]="form" (ngSubmit)="review()">
          @for (field of data.fields; track field.id) {
            @if (field.type === 'select') {
              <mat-form-field appearance="outline">
                <mat-label>{{ field.label }}</mat-label>
                <mat-select [formControlName]="field.id">
                  @for (option of field.options ?? []; track option.value) {
                    <mat-option [value]="option.value">{{ option.label }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
            } @else {
              <mat-form-field appearance="outline">
                <mat-label>{{ field.label }}</mat-label>
                <input matInput [formControlName]="field.id" [attr.maxlength]="field.maxLength ?? null" />
              </mat-form-field>
            }
          }
        </form>
      } @else {
        <h3>Vérifier la définition</h3>
        <dl class="summary">
          @for (field of data.fields; track field.id) {
            <div><dt>{{ field.label }}</dt><dd>{{ displayValue(field.id) }}</dd></div>
          }
        </dl>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton="text" type="button" (click)="dialogRef.close()">Annuler</button>
      @if (reviewing) {
        <button matButton="text" type="button" (click)="reviewing = false">Modifier</button>
        <button matButton="filled" type="button" (click)="save()">{{ data.submitLabel }}</button>
      } @else {
        <button matButton="filled" type="button" [disabled]="form.invalid" (click)="review()">Vérifier</button>
      }
    </mat-dialog-actions>
  `,
  styles: `
    h2 { color: var(--text-primary); font-size: 18px; font-weight: 700; }
    mat-dialog-content { display: grid; gap: 8px; }
    .description { margin: 0 0 8px; color: var(--text-secondary); line-height: 1.5; }
    mat-form-field { width: 100%; }
    h3 { margin: 0; color: var(--text-primary); font-size: 14px; }
    .summary { display: grid; gap: 12px; margin: 0; }
    .summary div { display: grid; gap: 4px; }
    dt { color: var(--text-muted); font-size: 11px; font-weight: 700; text-transform: uppercase; }
    dd { margin: 0; color: var(--text-primary); }
    mat-dialog-actions { gap: 8px; padding: 12px 24px 20px; }
  `,
})
export class RecordFormDialogComponent {
  protected readonly data = inject<RecordFormDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<RecordFormDialogComponent, Record<string, string>>);
  protected readonly form = new FormGroup<Record<string, FormControl<string>>>({});
  protected reviewing = false;

  constructor() {
    for (const field of this.data.fields) {
      const validators = [];
      if (field.required) validators.push(Validators.required);
      if (field.maxLength) validators.push(Validators.maxLength(field.maxLength));
      this.form.addControl(field.id, new FormControl('', { nonNullable: true, validators }));
    }
  }

  protected save(): void {
    if (this.form.valid) this.dialogRef.close(this.form.getRawValue());
  }

  protected review(): void {
    if (this.form.valid) this.reviewing = true;
  }

  protected displayValue(fieldId: string): string {
    const field = this.data.fields.find((item) => item.id === fieldId);
    const value = this.form.controls[fieldId]?.value ?? '';
    return field?.options?.find((option) => option.value === value)?.label ?? value;
  }
}
