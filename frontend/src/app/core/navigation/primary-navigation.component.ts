import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive } from '@angular/router';

interface NavigationItem {
  readonly label: string;
  readonly path: string;
  readonly exact?: boolean;
}

@Component({
  selector: 'app-primary-navigation',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './primary-navigation.component.html',
  styleUrl: './primary-navigation.component.scss',
})
export class PrimaryNavigationComponent {
  private readonly router = inject(Router);

  protected readonly items: readonly NavigationItem[] = this.router.config.flatMap((route) => {
    if (!route.data?.['primaryNavigation']) {
      return [];
    }

    const label = route.data['title'];
    if (typeof label !== 'string') {
      return [];
    }

    return [
      {
        label,
        path: route.path ? `/${route.path}` : '/',
        exact: route.path === '',
      },
    ];
  });
}
