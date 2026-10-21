import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import { NotificationService } from '../../../shared/services/notification.service';
import { NetworkSettingsService } from '../../../shared/services/network-settings.service';

@Component({
  selector: 'app-network-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSlideToggleModule,
    ConfigurationSectionComponent,
  ],
  template: `
    <app-configuration-section
      title="Reverse proxy"
      description="Réglage propre à ce nœud : adresses des proxies dont les en-têtes X-Forwarded-* sont crus."
    >
      <p class="hint">
        Derrière un reverse proxy, déclarez son adresse pour que les limites de requêtes, les
        journaux et le cookie sécurisé utilisent l’adresse et le schéma réels du visiteur. Une
        adresse déclarée par erreur permettrait à son appelant de falsifier ces informations.
      </p>
      <mat-slide-toggle
        [checked]="trustProxy()"
        [disabled]="busy()"
        (change)="trustProxy.set($event.checked)"
        >Faire confiance au reverse proxy</mat-slide-toggle
      >
      <mat-form-field appearance="outline" class="field">
        <mat-label>Adresses ou réseaux (une par ligne)</mat-label>
        <textarea
          matInput
          rows="4"
          [ngModel]="proxiesText()"
          (ngModelChange)="proxiesText.set($event)"
          [disabled]="busy()"
          placeholder="192.168.1.10&#10;172.18.0.0/16"
        ></textarea>
        @if (error()) {
          <mat-error>{{ error() }}</mat-error>
        }
      </mat-form-field>
      @if (detectionMessage()) {
        <p class="detection" role="status">{{ detectionMessage() }}</p>
      }
      <div class="actions">
        <button matButton="tonal" [disabled]="busy()" (click)="detect()">
          Détecter le proxy de cette connexion
        </button>
        <button matButton="filled" [disabled]="busy() || !dirty()" (click)="save()">
          Enregistrer
        </button>
      </div>
    </app-configuration-section>
  `,
  styles: `
    .hint,
    .detection {
      color: var(--text-secondary);
      line-height: 1.6;
      margin: 0 0 var(--space-4);
    }
    .detection {
      padding: var(--space-3) var(--space-4);
      border-radius: var(--radius-md);
      background: var(--surface-container-low);
    }
    .field {
      display: block;
      width: 100%;
      margin-top: var(--space-4);
    }
    .actions {
      display: flex;
      gap: var(--space-3);
      flex-wrap: wrap;
      justify-content: flex-end;
    }
  `,
})
export class NetworkTabComponent {
  private readonly service = inject(NetworkSettingsService);
  private readonly notifications = inject(NotificationService);
  private readonly destroyRef = inject(DestroyRef);
  private saved = { trustProxy: false, proxies: '' };
  protected readonly trustProxy = signal(false);
  protected readonly proxiesText = signal('');
  protected readonly busy = signal(true);
  protected readonly error = signal('');
  protected readonly detectionMessage = signal('');

  constructor() {
    this.service
      .get()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.apply(settings.trustProxy, settings.proxies.join('\n'));
          this.busy.set(false);
        },
        error: () => {
          this.notifications.show('Impossible de lire le réglage réseau.');
          this.busy.set(false);
        },
      });
  }

  protected dirty(): boolean {
    return (
      this.trustProxy() !== this.saved.trustProxy ||
      this.proxiesText().trim() !== this.saved.proxies
    );
  }

  protected save(): void {
    this.busy.set(true);
    this.error.set('');
    this.service
      .save({ trustProxy: this.trustProxy(), proxies: this.parse() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.apply(settings.trustProxy, settings.proxies.join('\n'));
          this.busy.set(false);
          this.notifications.show('Réglage réseau enregistré.');
        },
        error: (failure: HttpErrorResponse) => {
          this.error.set(failure.error?.errors?.proxies?.[0] ?? 'Enregistrement refusé.');
          this.busy.set(false);
        },
      });
  }

  protected detect(): void {
    this.busy.set(true);
    this.detectionMessage.set('');
    this.service
      .detect()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detection) => {
          this.busy.set(false);
          const address = detection.proxyAddress;
          if (!address || !detection.forwardedHeadersSeen) {
            this.detectionMessage.set(
              'Aucun en-tête X-Forwarded-* reçu : cette connexion ne passe pas par un reverse proxy, ou il ne les transmet pas.',
            );
          } else if (!detection.privateAddress) {
            this.detectionMessage.set(
              `La connexion arrive de ${address}, une adresse publique : ajoutez-la à la main si c’est bien votre proxy.`,
            );
          } else if (detection.alreadyTrusted) {
            this.detectionMessage.set(`Le proxy ${address} est déjà déclaré.`);
          } else {
            const lines = this.parse();
            this.proxiesText.set([...lines, address].join('\n'));
            this.trustProxy.set(true);
            this.detectionMessage.set(
              `Proxy détecté : ${address}. Il a été ajouté à la liste ; enregistrez pour l’appliquer.`,
            );
          }
        },
        error: () => {
          this.busy.set(false);
          this.notifications.show('Détection impossible.');
        },
      });
  }

  private parse(): string[] {
    return this.proxiesText()
      .split(/[\s,;]+/)
      .filter((item) => item.length > 0);
  }

  private apply(trustProxy: boolean, proxies: string): void {
    this.saved = { trustProxy, proxies };
    this.trustProxy.set(trustProxy);
    this.proxiesText.set(proxies);
  }
}
