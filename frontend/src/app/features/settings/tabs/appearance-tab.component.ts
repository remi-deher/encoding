import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { ThemeService } from '../../../core/theme/theme.service';
import {
  SettingRowComponent,
  SettingGroupComponent,
} from '../../../shared/ui/settings/setting-row.component';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
@Component({
  selector: 'app-appearance-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ConfigurationSectionComponent,
    SettingRowComponent,
    SettingGroupComponent,
    MatButtonToggleModule,
  ],
  styles: `
    .palette-options {
      max-width: 100%;
      flex-wrap: wrap;
    }
  `,
  template: `
    <app-configuration-section
      title="Apparence"
      description="Personnalisez les couleurs et la luminosité de votre espace."
      icon="settings"
    >
      <app-setting-group>
        <app-setting-row
          label="Thème de l’interface"
          hint="S’applique immédiatement, dans ce navigateur uniquement."
        >
          <mat-button-toggle-group
            aria-label="Thème de l’interface"
            [value]="theme.current()"
            (change)="theme.set($event.value)"
            ><mat-button-toggle value="light">Clair</mat-button-toggle
            ><mat-button-toggle value="dark">Sombre</mat-button-toggle></mat-button-toggle-group
          >
        </app-setting-row>
        <app-setting-row
          label="Palette de couleurs"
          hint="Disponible en clair et en sombre, mémorisée dans ce navigateur."
          [stacked]="true"
        >
          <mat-button-toggle-group
            class="palette-options"
            aria-label="Palette de couleurs"
            [value]="theme.palette()"
            (change)="theme.setPalette($event.value)"
          >
            @for (option of theme.palettes; track option.value) {
              <mat-button-toggle [value]="option.value">{{ option.label }}</mat-button-toggle>
            }
          </mat-button-toggle-group>
        </app-setting-row>
      </app-setting-group>
    </app-configuration-section>
  `,
})
export class AppearanceTabComponent {
  protected readonly theme = inject(ThemeService);
}
