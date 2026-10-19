import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { ThemeService } from '../../../core/theme/theme.service';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
@Component({
  selector: 'app-appearance-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ConfigurationSectionComponent, MatButtonToggleModule],
  template: `
    <app-configuration-section
      title="Apparence"
      description="Choisissez le thème qui vous convient."
    >
      <div class="setting-row">
        <div>
          <h3>Thème de l’interface</h3>
          <p>S’applique immédiatement, dans ce navigateur uniquement.</p>
        </div>
        <mat-button-toggle-group
          aria-label="Thème de l’interface"
          [value]="theme.current()"
          (change)="theme.set($event.value)"
          ><mat-button-toggle value="light">Clair</mat-button-toggle
          ><mat-button-toggle value="dark">Sombre</mat-button-toggle></mat-button-toggle-group
        >
      </div>
    </app-configuration-section>
  `,
  styles: `
    .setting-row {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: var(--space-4);
      flex-wrap: wrap;
    }
    h3 {
      margin: 0;
      font-size: var(--font-body);
    }
    p {
      margin: var(--space-1) 0 0;
      color: var(--text-secondary);
      line-height: 1.6;
    }
  `,
})
export class AppearanceTabComponent {
  protected readonly theme = inject(ThemeService);
}
