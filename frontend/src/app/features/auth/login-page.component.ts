import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { MINIMUM_PASSWORD_LENGTH, safeReturnUrl } from '../../shared/models/auth';
import { NotificationService } from '../../shared/services/notification.service';
import { RecordFormComponent, RecordFormData } from '../../shared/ui/record-form.component';

/** Connexion, ou création du premier administrateur selon `data.authPage`. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-login-page',
  imports: [RecordFormComponent],
  template: `
    <section class="auth-panel" [attr.aria-label]="form().title">
      <h1>{{ form().title }}</h1>
      <p>{{ form().description }}</p>
      <app-record-form [data]="form()" [busy]="busy()" (submitted)="submit($event)" />
    </section>
  `,
  styles: `
    .auth-panel {
      width: min(100%, 420px);
      margin: 6vh auto 0;
      padding: 28px;
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-lg);
      background: var(--surface);
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
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });
  protected readonly busy = signal(false);
  protected readonly setupMode = computed(() => this.routeData()['authPage'] === 'setup');
  protected readonly form = computed<RecordFormData>(() =>
    this.setupMode()
      ? {
          title: 'Créer le premier administrateur',
          description:
            'Ce cluster n’a pas encore de compte. Le compte créé ici gère les utilisateurs et les nœuds.',
          submitLabel: 'Créer le compte',
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
              autocomplete: 'new-password',
              hint: `Au moins ${MINIMUM_PASSWORD_LENGTH} caractères.`,
            },
            {
              id: 'nodeDisplayName',
              label: 'Nom de ce nœud',
              type: 'text',
              maxLength: 128,
              hint: 'Nom convivial du nœud qui accompagne ce serveur ; modifiable ensuite depuis la page Nœuds.',
            },
            {
              id: 'setupToken',
              label: 'Jeton d’initialisation',
              type: 'password',
              autocomplete: 'off',
              hint: 'Requis seulement si le serveur définit Auth__SetupToken.',
            },
          ],
        }
      : {
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
        },
  );

  protected async submit(value: Record<string, string>): Promise<void> {
    this.busy.set(true);
    try {
      if (this.setupMode())
        await this.auth.setup(
          value['username'].trim(),
          value['password'],
          value['setupToken'] ?? '',
          value['nodeDisplayName'] ?? '',
        );
      else await this.auth.login(value['username'].trim(), value['password']);
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
