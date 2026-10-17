import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { Route, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AuthService } from '../auth/auth.service';

export interface NavigationItem {
  readonly label: string;
  readonly path: string;
  readonly glyph: string;
  readonly group?: string;
  readonly exact?: boolean;
  readonly adminOnly?: boolean;
  readonly visible?: boolean;
  readonly keywords?: readonly string[];
}

export interface NavigationGroup {
  readonly label: string;
  readonly items: readonly NavigationItem[];
}

interface RouteNavigationMetadata {
  readonly group: string;
  readonly label: string;
  readonly glyph: string;
  readonly order?: number;
  readonly exact?: boolean;
  readonly adminOnly?: boolean;
  readonly visible?: boolean;
  readonly keywords?: readonly string[];
}

const NAVIGATION_GROUP_ORDER = [
  'Vue générale',
  'Traitements',
  'Ressources',
  'Infrastructure',
  'Administration',
];

export function getPrimaryNavigationGroups(routes: Route[]): readonly NavigationGroup[] {
  const grouped = new Map<string, { item: NavigationItem; order: number }[]>();

  for (const route of routes) {
    const metadata = route.data?.['navigation'] as RouteNavigationMetadata | undefined;
    if (!metadata || route.redirectTo !== undefined || route.path === undefined) continue;

    const path = route.path ? `/${route.path}` : '/';
    const items = grouped.get(metadata.group) ?? [];
    items.push({
      item: {
        label: metadata.label,
        path,
        glyph: metadata.glyph,
        group: metadata.group,
        exact: metadata.exact,
        adminOnly: metadata.adminOnly,
        visible: metadata.visible,
        keywords: metadata.keywords,
      },
      order: metadata.order ?? 0,
    });
    grouped.set(metadata.group, items);
  }

  return [...grouped.entries()]
    .sort(([left], [right]) => {
      const leftOrder = NAVIGATION_GROUP_ORDER.indexOf(left);
      const rightOrder = NAVIGATION_GROUP_ORDER.indexOf(right);
      return (
        (leftOrder < 0 ? Number.MAX_SAFE_INTEGER : leftOrder) -
        (rightOrder < 0 ? Number.MAX_SAFE_INTEGER : rightOrder)
      );
    })
    .map(([label, items]) => ({
      label,
      items: items.sort((left, right) => left.order - right.order).map(({ item }) => item),
    }));
}

export function getSearchableNavigationItems(routes: Route[]): readonly NavigationItem[] {
  const baseItems = getPrimaryNavigationGroups(routes).flatMap((group) => group.items);
  const extraDestinations: readonly NavigationItem[] = [
    {
      label: 'Ce nœud (Machine locale)',
      path: '/nodes?tab=node',
      glyph: '▣',
      group: 'Infrastructure',
      keywords: ['local', 'worker', 'serveur', 'matériel', 'gpu', 'agent'],
    },
    {
      label: 'Cluster & Membres',
      path: '/nodes?tab=cluster',
      glyph: '▣',
      group: 'Infrastructure',
      keywords: ['cluster', 'membres', 'enrôlement', 'appairage', 'synchronisation'],
    },
    {
      label: 'Groupes de nœuds',
      path: '/nodes?tab=groups',
      glyph: '▣',
      group: 'Infrastructure',
      keywords: ['groupes', 'étiquettes', 'labels', 'critères'],
    },
  ];
  return [...baseItems, ...extraDestinations];
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-primary-navigation',
  imports: [RouterLink, RouterLinkActive, MatTooltipModule],
  templateUrl: './primary-navigation.component.html',
  styleUrl: './primary-navigation.component.scss',
})
export class PrimaryNavigationComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly collapsed = input(false);
  readonly activeExecutionCount = input(0);

  protected readonly items = computed(() =>
    getPrimaryNavigationGroups(this.router.config)
      .flatMap((group) => group.items)
      .filter((item) => item.visible !== false && (!item.adminOnly || this.auth.isAdmin())),
  );
}
