import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogRef, MatDialogTitle } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MINIMUM_PASSWORD_LENGTH } from '../../shared/models/auth';
import { NotificationService } from '../../shared/services/notification.service';
import { IconComponent } from '../../shared/ui/icon.component';
import { AuthService } from './auth.service';

const MAXIMUM_PASSWORD_LENGTH = 256;

type PasswordField = 'currentPassword' | 'newPassword' | 'confirmation';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-password-change-dialog',
  imports: [
    IconComponent,
    MatButtonModule,
    MatDialogTitle,
    MatFormFieldModule,
    MatInputModule,
    ReactiveFormsModule,
  ],
  template: `
    <section class="app-dialog">
      <header class="app-dialog-header">
        <span class="app-dialog-icon" aria-hidden="true">◆</span>
        <div class="app-dialog-heading">
          <p class="app-dialog-eyebrow">Sécurité du compte</p>
          <h2 mat-dialog-title>Changer le mot de passe</h2>
        </div>
      </header>
      <form class="password-form" [formGroup]="form" novalidate (ngSubmit)="save()">
        <div class="app-dialog-body">
          @if (formError(); as message) {
            <p class="form-error" role="alert">{{ message }}</p>
          }
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Mot de passe actuel</mat-label>
            <input
              matInput
              formControlName="currentPassword"
              [type]="visible().currentPassword ? 'text' : 'password'"
              autocomplete="current-password"
              required
            />
            <button
              matSuffix
              type="button"
              class="reveal"
              [attr.aria-label]="revealLabel('currentPassword', 'le mot de passe actuel')"
              [attr.aria-pressed]="visible().currentPassword"
              (click)="toggle('currentPassword')"
            >
              <app-icon [name]="visible().currentPassword ? 'eye-off' : 'eye'" />
            </button>
            @if (error('currentPassword'); as message) {
              <mat-error>{{ message }}</mat-error>
            }
          </mat-form-field>

          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Nouveau mot de passe</mat-label>
            <input
              matInput
              formControlName="newPassword"
              [type]="visible().newPassword ? 'text' : 'password'"
              autocomplete="new-password"
              required
            />
            <button
              matSuffix
              type="button"
              class="reveal"
              [attr.aria-label]="revealLabel('newPassword', 'le nouveau mot de passe')"
              [attr.aria-pressed]="visible().newPassword"
              (click)="toggle('newPassword')"
            >
              <app-icon [name]="visible().newPassword ? 'eye-off' : 'eye'" />
            </button>
            <mat-hint>Au moins {{ minimumLength }} caractères.</mat-hint>
            @if (error('newPassword'); as message) {
              <mat-error>{{ message }}</mat-error>
            }
          </mat-form-field>

          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Confirmer le nouveau mot de passe</mat-label>
            <input
              matInput
              formControlName="confirmation"
              [type]="visible().confirmation ? 'text' : 'password'"
              autocomplete="new-password"
              required
            />
            <button
              matSuffix
              type="button"
              class="reveal"
              [attr.aria-label]="revealLabel('confirmation', 'la confirmation')"
              [attr.aria-pressed]="visible().confirmation"
              (click)="toggle('confirmation')"
            >
              <app-icon [name]="visible().confirmation ? 'eye-off' : 'eye'" />
            </button>
            @if (error('confirmation'); as message) {
              <mat-error>{{ message }}</mat-error>
            }
          </mat-form-field>

          <p class="session-note">Vos autres sessions seront fermées. Celle-ci reste ouverte.</p>
        </div>
        <footer class="app-dialog-footer">
          <button matButton="text" type="button" [disabled]="busy()" (click)="dialogRef.close()">
            Annuler
          </button>
          <button matButton="filled" type="submit" [disabled]="busy()">
            {{ busy() ? 'Modification…' : 'Changer le mot de passe' }}
          </button>
        </footer>
      </form>
    </section>
  `,
  styles: `
    .password-form {
      display: flex;
      min-height: 0;
      flex-direction: column;
    }
    .app-dialog-body {
      display: grid;
      gap: var(--space-4);
      padding-top: var(--space-1);
    }
    mat-form-field {
      width: 100%;
      min-width: 0;
    }
    .reveal {
      display: inline-grid;
      place-items: center;
      width: 40px;
      height: 40px;
      margin-right: 4px;
      border: 0;
      border-radius: var(--radius-pill);
      background: transparent;
      color: var(--text-secondary);
      cursor: pointer;
    }
    .reveal:hover {
      background: var(--surface-container-highest, var(--surface-muted));
    }
    .reveal:focus-visible {
      outline: 2px solid var(--accent-strong);
    }
    .form-error {
      margin: 0;
      padding: var(--space-3);
      border-radius: var(--radius-md);
      background: var(--danger-soft);
      color: var(--danger-strong);
      line-height: 1.45;
    }
    .session-note {
      margin: 0;
      color: var(--text-muted);
      font-size: var(--font-label);
      line-height: 1.45;
    }
  `,
})
export class PasswordChangeDialogComponent {
  protected readonly dialogRef = inject(MatDialogRef<PasswordChangeDialogComponent>);
  private readonly notifications = inject(NotificationService);
  private readonly auth = inject(AuthService);
  protected readonly minimumLength = MINIMUM_PASSWORD_LENGTH;
  protected readonly busy = signal(false);
  protected readonly formError = signal('');
  protected readonly visible = signal<Record<PasswordField, boolean>>({
    currentPassword: false,
    newPassword: false,
    confirmation: false,
  });

  protected readonly form = new FormGroup({
    currentPassword: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    newPassword: new FormControl('', {
      nonNullable: true,
      validators: [
        Validators.required,
        Validators.minLength(MINIMUM_PASSWORD_LENGTH),
        Validators.maxLength(MAXIMUM_PASSWORD_LENGTH),
        (control: AbstractControl): ValidationErrors | null =>
          control.value && control.value === this.form?.controls.currentPassword.value
            ? { unchanged: true }
            : null,
      ],
    }),
    confirmation: new FormControl('', {
      nonNullable: true,
      validators: [
        Validators.required,
        (control: AbstractControl): ValidationErrors | null =>
          control.value && control.value !== this.form?.controls.newPassword.value
            ? { mismatch: true }
            : null,
      ],
    }),
  });

  constructor() {
    this.dialogRef.addPanelClass('app-dialog-panel');
    this.form.controls.currentPassword.valueChanges.subscribe(() =>
      this.form.controls.newPassword.updateValueAndValidity({ emitEvent: false }),
    );
    this.form.controls.newPassword.valueChanges.subscribe(() =>
      this.form.controls.confirmation.updateValueAndValidity({ emitEvent: false }),
    );
  }

  protected toggle(field: PasswordField): void {
    this.visible.update((state) => ({ ...state, [field]: !state[field] }));
  }

  protected revealLabel(field: PasswordField, subject: string): string {
    return `${this.visible()[field] ? 'Masquer' : 'Afficher'} ${subject}`;
  }

  protected error(field: PasswordField): string {
    const control = this.form.controls[field];
    if (!control.touched || control.valid) return '';
    if (control.hasError('server')) return String(control.getError('server'));
    if (control.hasError('required')) return 'Ce champ est obligatoire.';
    if (control.hasError('minlength'))
      return `Le mot de passe doit contenir au moins ${MINIMUM_PASSWORD_LENGTH} caractères.`;
    if (control.hasError('maxlength'))
      return `Le mot de passe ne peut pas dépasser ${MAXIMUM_PASSWORD_LENGTH} caractères.`;
    if (control.hasError('unchanged'))
      return 'Le nouveau mot de passe doit être différent de l’actuel.';
    if (control.hasError('mismatch')) return 'Les deux mots de passe ne correspondent pas.';
    return 'Vérifiez cette valeur.';
  }

  protected async save(): Promise<void> {
    this.formError.set('');
    this.form.markAllAsTouched();
    if (this.busy() || this.form.invalid) return;
    const { currentPassword, newPassword } = this.form.getRawValue();
    this.busy.set(true);
    this.form.disable({ emitEvent: false });
    try {
      await this.auth.changePassword(currentPassword, newPassword);
      this.notifications.show('Votre mot de passe a été modifié.');
      this.dialogRef.close(true);
    } catch (error) {
      this.form.enable({ emitEvent: false });
      this.showServerError(error);
    } finally {
      this.busy.set(false);
    }
  }

  /** Rattache les refus de l’API au champ concerné ; les autres échecs s’affichent en tête du formulaire. */
  private showServerError(error: unknown): void {
    const fields =
      (error instanceof HttpErrorResponse
        ? (error.error as { errors?: Record<string, readonly string[]> } | null)?.errors
        : undefined) ?? {};
    let attached = false;
    for (const field of ['currentPassword', 'newPassword'] as const) {
      const message = fields[field]?.[0];
      if (!message) continue;
      const control = this.form.controls[field];
      control.setErrors({ server: message });
      control.markAsTouched();
      attached = true;
    }
    if (attached) return;
    if (error instanceof HttpErrorResponse && error.status === 401)
      this.formError.set('Votre session a expiré. Reconnectez-vous puis réessayez.');
    else if (error instanceof HttpErrorResponse && error.status === 0)
      this.formError.set('Le serveur est injoignable. Vérifiez la connexion puis réessayez.');
    else
      this.formError.set(
        AuthService.errorMessage(error, 'Le mot de passe n’a pas pu être modifié.'),
      );
  }
}
