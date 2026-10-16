import { Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AuthService } from '../auth/auth.service';

export interface NavigationItem {
  readonly label: string;
  readonly path: string;
  readonly glyph: string;
  readonly exact?: boolean;
  readonly adminOnly?: boolean;
  readonly keywords?: readonly string[];
}

export interface NavigationGroup {
  readonly label: string;
  readonly items: readonly NavigationItem[];
}

const NAVIGATION_GROUPS: readonly NavigationGroup[] = [
  {
    label: 'Espace',
    items: [{ label: 'Vue d’ensemble', path: '/', glyph: '▦', exact: true, keywords: ['accueil'] }],
  },
  {
    label: 'Traitement',
    items: [
      { label: 'Tâches', path: '/tasks', glyph: '≡', keywords: ['exécutions', 'file'] },
      { label: 'Flows', path: '/flows', glyph: '⋈', keywords: ['traitements', 'workflows'] },
    ],
  },
  {
    label: 'Ressources',
    items: [
      {
        label: 'Librairies',
        path: '/libraries',
        glyph: '◇',
        keywords: ['médias', 'bibliothèques'],
      },
      {
        label: 'Profils d’encodage',
        path: '/libraries/encoding-profiles',
        glyph: '◫',
        keywords: ['qualité', 'encodeur'],
      },
      { label: 'Modèles', path: '/libraries/templates', glyph: '▤', keywords: ['templates'] },
      { label: 'Scripts', path: '/libraries/scripts', glyph: '≋', keywords: ['code'] },
      { label: 'Packs de composants', path: '/libraries/packs', glyph: '⊞', keywords: ['plugins'] },
    ],
  },
  {
    label: 'Infrastructure',
    items: [
      { label: 'Nœuds', path: '/nodes', glyph: '▣', keywords: ['workers', 'serveurs'] },
      {
        label: 'Groupes de nœuds',
        path: '/node-groups',
        glyph: '♧',
        keywords: ['workers', 'groupes'],
      },
      { label: 'Plugins', path: '/plugins', glyph: '✣', keywords: ['intégrations'] },
    ],
  },
  {
    label: 'Administration',
    items: [{ label: 'Paramètres', path: '/settings', glyph: '⚙', adminOnly: true }],
  },
];

export const PRIMARY_NAVIGATION_GROUPS = NAVIGATION_GROUPS;

@Component({
  selector: 'app-primary-navigation',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './primary-navigation.component.html',
  styleUrl: './primary-navigation.component.scss',
})
export class PrimaryNavigationComponent {
  private readonly auth = inject(AuthService);

  protected readonly groups = computed(() =>
    NAVIGATION_GROUPS.map((group) => ({
      ...group,
      items: group.items.filter((item) => !item.adminOnly || this.auth.isAdmin()),
    })).filter((group) => group.items.length > 0),
  );

  protected readonly items = computed(() => this.groups().flatMap((group) => group.items));
}
