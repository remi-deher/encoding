import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';

@Component({
  selector: 'app-section-placeholder',
  imports: [MatCardModule, PageHeaderComponent],
  template: `
    <app-page-header [title]="title()" [description]="description()" />
    <mat-card appearance="outlined" class="placeholder-card">
      <mat-card-content>
        <span class="placeholder-kicker">PROCHAINE ÉTAPE</span>
        <h2>Cette section sera construite avec son gabarit dédié.</h2>
        <p>
          La navigation est en place. Nous définirons le besoin et les composants communs avant de
          créer cette vue.
        </p>
      </mat-card-content>
    </mat-card>
  `,
  styles: `
    .placeholder-card {
      border-color: var(--border-subtle);
      border-radius: 12px;
      background: var(--surface);
      box-shadow: none;
    }

    .placeholder-card mat-card-content {
      padding: 32px;
    }

    .placeholder-kicker {
      color: var(--accent-strong);
      font-size: 10px;
      font-weight: 750;
      letter-spacing: 0.09em;
    }

    h2 {
      margin: 12px 0 7px;
      color: var(--text-primary);
      font-size: 17px;
      letter-spacing: -0.02em;
    }

    p {
      max-width: 600px;
      margin: 0;
      color: var(--text-secondary);
      font-size: 12px;
      line-height: 1.6;
    }
  `,
})
export class SectionPlaceholderComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });
  protected readonly title = computed(
    () => (this.routeData()['title'] as string | undefined) ?? 'Section',
  );
  protected readonly description = computed(
    () => (this.routeData()['description'] as string | undefined) ?? '',
  );
}
