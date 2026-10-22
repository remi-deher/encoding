import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import {
  SettingRowComponent,
  SettingGroupComponent,
} from '../../../shared/ui/settings/setting-row.component';
import { SettingsNoticeComponent } from '../../../shared/ui/settings/settings-notice.component';
import { SettingsSaveBarComponent } from '../../../shared/ui/settings/settings-save-bar.component';
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
    SettingRowComponent,
    SettingGroupComponent,
    SettingsNoticeComponent,
    SettingsSaveBarComponent,
  ],
  template: `
    <app-configuration-section
      title="Reverse proxy"
      description="Réglage propre à ce nœud."
      icon="server"
    >
      <app-settings-notice tone="warning">
        Une adresse déclarée par erreur permettrait à son appelant de falsifier l’adresse et le
        schéma vus par l’application.
      </app-settings-notice>
      <app-setting-group>
        <app-setting-row
          label="Faire confiance au reverse proxy"
          hint="Utilise ses en-têtes X-Forwarded-* pour les limites de requêtes, les journaux et le cookie sécurisé."
        >
          <mat-slide-toggle
            aria-label="Faire confiance au reverse proxy"
            [checked]="trustProxy()"
            [disabled]="busy()"
            (change)="trustProxy.set($event.checked)"
          />
        </app-setting-row>
        <app-setting-row
          label="Adresses ou réseaux du proxy"
          hint="Une par ligne, par exemple 192.168.1.10 ou 172.18.0.0/16."
          [stacked]="true"
        >
          <mat-form-field appearance="outline" class="field" subscriptSizing="dynamic">
            <mat-label>Adresses ou réseaux</mat-label>
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
            <app-settings-notice tone="info">{{ detectionMessage() }}</app-settings-notice>
          }
          <button matButton="tonal" [disabled]="busy()" (click)="detect()">
            Détecter le proxy de cette connexion
          </button>
        </app-setting-row>
      </app-setting-group>
    </app-configuration-section>
    <app-settings-save-bar
      [visible]="dirty()"
      [busy]="busy()"
      (saved)="save()"
      (cancelled)="cancel()"
    />
  `,

  styles: `
    .field {
      display: block;
      width: 100%;
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

  protected cancel(): void {
    this.apply(this.saved.trustProxy, this.saved.proxies);
    this.error.set('');
    this.detectionMessage.set('');
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
