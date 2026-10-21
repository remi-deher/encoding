import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { safeReturnUrl } from '../../shared/models/auth';
import { NotificationService } from '../../shared/services/notification.service';

/** Connexion à l'application. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-login-page',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
  ],
  template: `
    <main class="auth-stage">
      <form
        class="auth-card"
        aria-labelledby="login-title"
        [formGroup]="form"
        (ngSubmit)="submit()"
      >
        @if (busy()) {
          <mat-progress-bar
            class="auth-progress"
            mode="indeterminate"
            aria-label="Connexion en cours"
          />
        }
        <div class="auth-brand" aria-hidden="true">
          <span class="auth-brand-mark">
            <svg
              viewBox="0 0 24 24"
              width="20"
              height="20"
              fill="none"
              stroke="currentColor"
              stroke-width="2.2"
            >
              <polygon points="6 4 20 12 6 20 6 4" />
            </svg>
          </span>
          <span class="auth-brand-name">Encoding</span>
        </div>
        <h1 id="login-title">Connexion</h1>
        <p class="auth-lead">Identifiez-vous pour gérer le cluster.</p>

        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Nom d’utilisateur</mat-label>
          <input
            matInput
            type="text"
            formControlName="username"
            autocomplete="username"
            maxlength="64"
            required
          />
        </mat-form-field>

        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Mot de passe</mat-label>
          <input
            matInput
            formControlName="password"
            autocomplete="current-password"
            required
            [type]="passwordVisible() ? 'text' : 'password'"
          />
          <button
            matSuffix
            type="button"
            class="auth-eye"
            [attr.aria-label]="
              passwordVisible() ? 'Masquer le mot de passe' : 'Afficher le mot de passe'
            "
            [attr.aria-pressed]="passwordVisible()"
            (click)="passwordVisible.set(!passwordVisible())"
          >
            <svg
              viewBox="0 0 24 24"
              width="20"
              height="20"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              aria-hidden="true"
            >
              <path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12z" />
              <circle cx="12" cy="12" r="3" />
              @if (passwordVisible()) {
                <path d="M4 4l16 16" />
              }
            </svg>
          </button>
        </mat-form-field>

        <button matButton="filled" type="submit" class="auth-submit" [disabled]="busy()">
          Se connecter
        </button>
      </form>
    </main>
  `,
  styles: `
    .auth-stage {
      min-height: 100%;
      display: grid;
      place-items: center;
      padding: var(--space-5) var(--space-4);
      background: radial-gradient(ellipse at 50% 0, var(--accent-soft), transparent 65%);
    }
    .auth-card {
      position: relative;
      overflow: hidden;
      display: grid;
      gap: var(--space-4);
      width: min(100%, 400px);
      padding: var(--space-6) 28px;
      border-radius: 28px;
      background: var(--surface-container);
      box-shadow:
        0 1px 2px rgb(0 0 0 / 8%),
        0 8px 28px rgb(0 0 0 / 10%);
    }
    .auth-progress {
      position: absolute;
      inset: 0 0 auto;
    }
    .auth-brand {
      display: inline-flex;
      align-items: center;
      gap: 10px;
      margin-bottom: var(--space-2);
    }
    .auth-brand-mark {
      width: 40px;
      height: 40px;
      border-radius: 12px;
      display: grid;
      place-items: center;
      background: linear-gradient(135deg, var(--accent) 0%, var(--accent-strong) 100%);
      color: var(--on-accent);
    }
    .auth-brand-name {
      font-weight: 800;
      font-size: var(--font-title-md);
      color: var(--text-primary);
      letter-spacing: -0.02em;
    }
    h1 {
      margin: 0;
      color: var(--text-primary);
      font-size: var(--font-headline);
      letter-spacing: -0.02em;
    }
    .auth-lead {
      margin: calc(-1 * var(--space-2)) 0 var(--space-2);
      color: var(--text-secondary);
      font-size: var(--font-body);
      line-height: 1.6;
    }
    mat-form-field {
      width: 100%;
    }
    .auth-eye {
      display: grid;
      place-items: center;
      width: 40px;
      height: 40px;
      margin-right: 4px;
      border: 0;
      border-radius: 50%;
      background: none;
      color: var(--text-muted);
      cursor: pointer;
    }
    .auth-eye:hover {
      background: var(--surface-container-high);
    }
    .auth-submit {
      width: 100%;
      min-height: 52px;
      font-weight: 700;
    }
  `,
})
export class LoginPageComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly notifications = inject(NotificationService);
  protected readonly busy = signal(false);
  protected readonly passwordVisible = signal(false);
  protected readonly form = new FormGroup({
    username: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });

  protected async submit(): Promise<void> {
    if (this.busy()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const { username, password } = this.form.getRawValue();
    this.busy.set(true);
    try {
      await this.auth.login(username.trim(), password);
      await this.router.navigateByUrl(
        safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')),
      );
    } catch (error) {
      this.notifications.show(
        AuthService.errorMessage(
          error,
          'La connexion a échoué. Vérifiez que le serveur est disponible.',
        ),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
