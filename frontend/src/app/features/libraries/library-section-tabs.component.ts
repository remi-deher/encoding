import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { Router } from '@angular/router';
import { CategoryTabsComponent, CategoryTabOption } from '../../shared/ui/category-tabs.component';

const SECTIONS: readonly CategoryTabOption[] = [
  { value: 'media', label: 'Bibliothèques' },
  { value: 'scripts', label: 'Scripts' },
  { value: 'templates', label: 'Modèles de flow' },
  { value: 'packs', label: 'Packs de composants' },
  { value: 'encoding', label: 'Profils d’encodage' },
  { value: 'resources', label: 'Ressources' },
];

const SECTION_ROUTES: Readonly<Record<string, string>> = {
  media: '/libraries',
  scripts: '/libraries/scripts',
  templates: '/libraries/templates',
  packs: '/libraries/packs',
  encoding: '/libraries/encoding-profiles',
  resources: '/libraries/resources',
};

/** Navigation commune entre les catalogues de ressources réutilisables. */
@Component({
  selector: 'app-library-section-tabs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CategoryTabsComponent],
  template: `
    <app-category-tabs
      label="Ressources"
      [options]="sections"
      [selected]="selected()"
      (selectedChange)="navigateTo($event)"
    />
  `,
})
export class LibrarySectionTabsComponent {
  private readonly router = inject(Router);
  readonly selected = input.required<string>();
  protected readonly sections = SECTIONS;

  protected navigateTo(section: string): void {
    if (section === this.selected()) return;
    const route = SECTION_ROUTES[section];
    if (route) void this.router.navigateByUrl(route);
  }
}
