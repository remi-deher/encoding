import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, exhaustMap, of, timer } from 'rxjs';
import { ConfigurationSectionComponent } from '../../shared/ui/configuration-section.component';
import { UpdatesService, UpdateStatus } from '../../shared/services/updates.service';

@Component({
  selector: 'app-updates-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, ConfigurationSectionComponent],
  template: `
    <h1 class="visually-hidden">Mise à jour</h1>
    <app-configuration-section title="Version installée" [badge]="data()?.installedVersion ?? ''">
      <div role="status" aria-live="polite">
        @if (loading()) {
          <p>Vérification en cours…</p>
        }
        @if (error()) {
          <p>
            Impossible de charger les informations de version. Une vérification sera retentée
            automatiquement.
          </p>
        }
        @if (data(); as status) {
          @switch (status.status) {
            @case ('available') {
              <p>Une nouvelle version est disponible : {{ status.latestVersion }}.</p>
            }
            @case ('current') {
              <p>Ce nœud est à jour.</p>
            }
            @case ('development') {
              <p>Version de développement : recherche de mises à jour désactivée.</p>
            }
            @case ('unconfigured') {
              <p>Aucune source de mises à jour configurée.</p>
            }
            @default {
              <p>Vérification indisponible. Les notes embarquées restent consultables.</p>
            }
          }
          @if (status.checkedAt) {
            <p>Dernière vérification : {{ status.checkedAt | date: 'dd/MM/yyyy HH:mm' }}.</p>
          }
        }
      </div>
      <p>Pour installer une version, mettez à jour l’image Docker du nœud.</p>
    </app-configuration-section>
    @if (data(); as status) {
      @for (release of status.releases; track release.version) {
        <app-configuration-section
          [title]="'Version ' + release.version"
          [badge]="release.version === status.installedVersion ? 'Installée sur ce nœud' : ''"
        >
          <p>{{ release.publishedAt | date: 'dd/MM/yyyy' }}</p>
          <p class="release-notes">{{ release.notes }}</p>
        </app-configuration-section>
      } @empty {
        <p>Aucune note de version embarquée.</p>
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-4);
      min-width: 0;
    }
    .release-notes {
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      line-height: 1.6;
    }
  `,
})
export class UpdatesPageComponent {
  protected readonly data = signal<UpdateStatus | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal(false);
  private readonly service = inject(UpdatesService);

  constructor() {
    timer(0, 15 * 60_000)
      .pipe(
        exhaustMap(() => {
          this.loading.set(true);
          this.error.set(false);
          return this.service.getUpdates().pipe(catchError(() => of(null)));
        }),
        takeUntilDestroyed(),
      )
      .subscribe((status) => {
        this.loading.set(false);
        this.error.set(status === null);
        if (status) this.data.set(status);
      });
  }
}
