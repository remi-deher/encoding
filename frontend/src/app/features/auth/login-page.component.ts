import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { safeReturnUrl } from '../../shared/models/auth';
import { NotificationService } from '../../shared/services/notification.service';
import { RecordFormComponent, RecordFormData } from '../../shared/ui/record-form.component';

/** Connexion à l'application. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-login-page',
  imports: [RecordFormComponent],
  template: `
    <section class="auth-panel" [attr.aria-label]="form.title">
      <div class="auth-brand" aria-hidden="true">
        <span class="auth-brand-mark">
          <svg
            viewBox="0 0 24 24"
            width="18"
            height="18"
            fill="none"
            stroke="currentColor"
            stroke-width="2.2"
          >
            <polygon points="6 4 20 12 6 20 6 4" />
          </svg>
        </span>
        <span class="auth-brand-name">Encoding</span>
      </div>
      <h1>{{ form.title }}</h1>
      <p>{{ form.description }}</p>
      <app-record-form [data]="form" [busy]="busy()" (submitted)="submit($event)" />
    </section>
  `,
  styles: `
    .auth-panel {
      width: min(100%, 440px);
      margin: 6vh auto 0;
      padding: var(--space-6);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-2xl, 24px);
      background: var(--surface-container-low);
      box-shadow: var(--shadow-soft);
    }
    .auth-brand {
      display: inline-flex;
      align-items: center;
      gap: 10px;
      margin-bottom: var(--space-4);
    }
    .auth-brand-mark {
      width: 34px;
      height: 34px;
      border-radius: 10px;
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
      margin: 0 0 6px;
      color: var(--text-primary);
      font-size: var(--font-title-lg);
      letter-spacing: -0.02em;
    }
    p {
      margin: 0 0 18px;
      color: var(--text-secondary);
      font-size: var(--font-body);
      line-height: 1.6;
    }
  `,
})
export class LoginPageComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly notifications = inject(NotificationService);
  protected readonly busy = signal(false);
  protected readonly form: RecordFormData = {
    title: 'Connexion',
    description: 'Identifiez-vous pour gérer le cluster.',
    submitLabel: 'Se connecter',
    review: false,
    cancellable: false,
    fields: [
      {
        id: 'username',
        label: 'Nom d’utilisateur',
        type: 'text',
        required: true,
        maxLength: 64,
        autocomplete: 'username',
      },
      {
        id: 'password',
        label: 'Mot de passe',
        type: 'password',
        required: true,
        autocomplete: 'current-password',
      },
    ],
  };

  protected async submit(value: Record<string, string>): Promise<void> {
    this.busy.set(true);
    try {
      await this.auth.login(value['username'].trim(), value['password']);
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
