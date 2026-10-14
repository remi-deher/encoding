import { Component, computed, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive } from '@angular/router';
import { AuthService } from '../auth/auth.service';

interface NavigationItem {
  readonly label: string;
  readonly path: string;
  readonly exact?: boolean;
  readonly adminOnly?: boolean;
}

@Component({
  selector: 'app-primary-navigation',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './primary-navigation.component.html',
  styleUrl: './primary-navigation.component.scss',
})
export class PrimaryNavigationComponent {
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

  private readonly allItems: readonly NavigationItem[] = this.router.config.flatMap((route) => {
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
        adminOnly: route.data['adminOnly'] === true,
      },
    ];
  });

  protected readonly items = computed(() =>
    this.allItems.filter((item) => !item.adminOnly || this.auth.isAdmin()),
  );
}
