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
  template: `
    <app-configuration-section
      title="Apparence"
      description="Choisissez le thème qui vous convient."
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
      </app-setting-group>
    </app-configuration-section>
  `,
})
export class AppearanceTabComponent {
  protected readonly theme = inject(ThemeService);
}
