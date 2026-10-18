import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import {
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

export interface CreateApiTokenDialogResult {
  readonly name: string;
  readonly role: 'admin' | 'viewer';
  readonly expiresInDays: number | null;
}

@Component({
  selector: 'app-create-api-token-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatDialogActions,
    MatDialogContent,
    MatDialogTitle,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    ReactiveFormsModule,
  ],
  template: `
    <section class="app-dialog">
      <h2 mat-dialog-title class="app-dialog-header">Générer un jeton d’API</h2>

      <mat-dialog-content class="app-dialog-body">
        <form [formGroup]="form" class="token-form">
          <mat-form-field appearance="outline">
            <mat-label>Nom ou description du jeton</mat-label>
            <input
              matInput
              formControlName="name"
              placeholder="ex. Script backup, Prometheus, Claude Desktop"
              maxlength="64"
              required
              autocomplete="off"
            />
            @if (form.controls.name.hasError('required')) {
              <mat-error>Le nom du jeton est requis.</mat-error>
            }
          </mat-form-field>

          <mat-form-field appearance="outline">
            <mat-label>Rôle et permissions</mat-label>
            <mat-select formControlName="role">
              <mat-option value="admin">Administrateur (Lecture et écriture)</mat-option>
              <mat-option value="viewer">Lecture seule (Consultation)</mat-option>
            </mat-select>
          </mat-form-field>

          <mat-form-field appearance="outline">
            <mat-label>Durée de validité</mat-label>
            <mat-select formControlName="duration">
              <mat-option value="never">Illimité (n’expire jamais)</mat-option>
              <mat-option value="30">30 jours</mat-option>
              <mat-option value="90">90 jours</mat-option>
              <mat-option value="365">1 an</mat-option>
            </mat-select>
          </mat-form-field>
        </form>
      </mat-dialog-content>

      <mat-dialog-actions class="app-dialog-footer" align="end">
        <button matButton="text" type="button" (click)="cancel()">Annuler</button>
        <button
          matButton="filled"
          type="button"
          [disabled]="form.invalid || submitting()"
          (click)="submit()"
        >
          {{ submitting() ? 'Génération…' : 'Générer le jeton' }}
        </button>
      </mat-dialog-actions>
    </section>
  `,
  styles: `
    .token-form {
      display: grid;
      gap: var(--space-3);
      padding-top: var(--space-2);
      min-width: 380px;
    }
  `,
})
export class CreateApiTokenDialogComponent {
  protected readonly dialogRef = inject(
    MatDialogRef<CreateApiTokenDialogComponent, CreateApiTokenDialogResult | null>,
  );

  protected readonly submitting = signal(false);

  protected readonly form = new FormGroup({
    name: new FormControl<string>('', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(64)],
    }),
    role: new FormControl<'admin' | 'viewer'>('admin', { nonNullable: true }),
    duration: new FormControl<string>('never', { nonNullable: true }),
  });

  protected cancel(): void {
    this.dialogRef.close(null);
  }

  protected submit(): void {
    if (this.form.invalid) return;
    this.submitting.set(true);

    const val = this.form.getRawValue();
    const days = val.duration === 'never' ? null : parseInt(val.duration, 10);

    this.dialogRef.close({
      name: val.name.trim(),
      role: val.role,
      expiresInDays: days,
    });
  }
}
