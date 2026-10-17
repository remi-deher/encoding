import { unsavedChangesGuard } from './shared/guards/unsaved-changes-guard';
import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/auth/auth.guard';

const applicationRoutes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () =>
      import('./features/home/home-page.component').then((module) => module.HomePageComponent),
    data: {
      title: 'Vue d’ensemble',
      navigation: {
        group: 'Vue générale',
        label: 'Vue d’ensemble',
        glyph: '▦',
        exact: true,
        keywords: ['accueil', 'dashboard'],
      },
    },
  },
  {
    path: 'worker',
    pathMatch: 'full',
    redirectTo: 'nodes',
  },
  {
    path: 'tasks',
    loadComponent: () =>
      import('./features/tasks/task-monitoring-page.component').then(
        (module) => module.TaskMonitoringPageComponent,
      ),
    data: {
      title: 'Tâches',
      navigation: {
        group: 'Traitements',
        label: 'Tâches',
        glyph: '≡',
        order: 2,
        keywords: ['exécutions', 'file'],
      },
      description: 'Suivez les exécutions en attente, en cours, annulées, en erreur ou réussies.',
    },
  },
  {
    path: 'libraries/encoding-profiles',
    loadComponent: () =>
      import('./features/libraries/encoding-profile-library-page.component').then(
        (module) => module.EncodingProfileLibraryPageComponent,
      ),
    data: {
      title: 'Librairies',
      navigation: {
        group: 'Ressources',
        label: 'Profils d’encodage',
        glyph: '◫',
        order: 5,
        visible: false,
        keywords: ['qualité', 'encodeur'],
      },
      description: 'Gérez les profils de qualité et leurs réglages par encodeur.',
    },
  },
  {
    path: 'libraries/scripts',
    loadComponent: () =>
      import('./features/libraries/script-library-page.component').then(
        (module) => module.ScriptLibraryPageComponent,
      ),
    data: {
      title: 'Librairies',
      navigation: {
        group: 'Ressources',
        label: 'Scripts',
        glyph: '≋',
        order: 3,
        visible: false,
        keywords: ['code'],
      },
      description: 'Gérez les scripts réutilisables dans vos flows.',
    },
  },
  {
    path: 'libraries/packs',
    loadComponent: () =>
      import('./features/libraries/component-pack-library-page.component').then(
        (module) => module.ComponentPackLibraryPageComponent,
      ),
    data: {
      title: 'Librairies',
      navigation: {
        group: 'Ressources',
        label: 'Packs de composants',
        glyph: '⊞',
        order: 4,
        visible: false,
        keywords: ['plugins'],
      },
      description: 'Parcourez les packs de composants réutilisables dans vos flows.',
    },
  },
  {
    path: 'libraries/templates',
    loadComponent: () =>
      import('./features/libraries/flow-template-library-page.component').then(
        (module) => module.FlowTemplateLibraryPageComponent,
      ),
    data: {
      title: 'Librairies',
      navigation: {
        group: 'Ressources',
        label: 'Modèles',
        glyph: '▤',
        order: 2,
        visible: false,
        keywords: ['templates'],
      },
      description: 'Parcourez les modèles de flow réutilisables.',
    },
  },
  {
    path: 'libraries',
    loadComponent: () =>
      import('./features/libraries/library-catalog-page.component').then(
        (module) => module.LibraryCatalogPageComponent,
      ),
    data: {
      title: 'Librairies',
      navigation: {
        group: 'Ressources',
        label: 'Librairies',
        glyph: '◇',
        order: 1,
        keywords: ['médias', 'bibliothèques'],
      },
      description: 'Gérez les ressources partagées du cluster.',
    },
  },
  {
    path: 'flows',
    loadComponent: () =>
      import('./features/flows/flow-catalog-page.component').then(
        (module) => module.FlowCatalogPageComponent,
      ),
    data: {
      title: 'Flows',
      navigation: {
        group: 'Traitements',
        label: 'Flows',
        glyph: '⋈',
        order: 1,
        keywords: ['traitements', 'workflows'],
      },
      description: 'Composez et gérez les traitements définis pour vos médias.',
    },
  },
  {
    path: 'flows/new',
    canDeactivate: [unsavedChangesGuard],
    loadComponent: () =>
      import('./features/flows/flow-editor-page.component').then(
        (module) => module.FlowEditorPageComponent,
      ),
    data: {
      title: 'Nouveau flow',
      layout: 'workspace',
      description: 'Assemblez les composants dans l’ordre de traitement souhaité.',
    },
  },
  {
    path: 'flows/:flowId/edit',
    canDeactivate: [unsavedChangesGuard],
    loadComponent: () =>
      import('./features/flows/flow-editor-page.component').then(
        (module) => module.FlowEditorPageComponent,
      ),
    data: {
      title: 'Modifier un flow',
      layout: 'workspace',
      description: 'Modifiez les composants et leur ordre de traitement.',
    },
  },
  {
    path: 'nodes',
    loadComponent: () =>
      import('./features/nodes/node-catalog-page.component').then(
        (module) => module.NodeCatalogPageComponent,
      ),
    data: {
      title: 'Nœuds',
      navigation: {
        group: 'Infrastructure',
        label: 'Nœuds',
        glyph: '▣',
        order: 1,
        keywords: ['workers', 'serveurs'],
      },
      description: 'Consultez les rôles, capacités et états des installations du cluster.',
    },
  },
  {
    path: 'node-groups',
    redirectTo: () => '/nodes?tab=groups',
  },
  {
    path: 'plugins',
    loadComponent: () =>
      import('./features/plugins/plugin-catalog-page.component').then(
        (module) => module.PluginCatalogPageComponent,
      ),
    data: {
      title: 'Plugins',
      navigation: {
        group: 'Ressources',
        label: 'Plugins',
        glyph: '✣',
        order: 2,
        keywords: ['intégrations'],
      },
      description: 'Consultez les intégrations et composants disponibles pour vos flows.',
    },
  },
  {
    path: 'settings',
    loadComponent: () =>
      import('./features/settings/user-management-page.component').then(
        (module) => module.UserManagementPageComponent,
      ),
    data: {
      title: 'Paramètres',
      navigation: {
        group: 'Administration',
        label: 'Paramètres',
        glyph: '⚙',
        order: 1,
        adminOnly: true,
      },
      adminOnly: true,
      description: 'Gérez les comptes utilisateurs et leurs rôles.',
    },
  },
];

const publicRoutes: Routes = [
  {
    path: 'login',
    canActivate: [guestGuard],
    loadComponent: () =>
      import('./features/auth/login-page.component').then((module) => module.LoginPageComponent),
    data: { title: 'Connexion', authPage: 'login' },
  },
  {
    path: 'setup',
    canActivate: [guestGuard],
    loadComponent: () =>
      import('./features/setup/setup-wizard-page.component').then(
        (module) => module.SetupWizardPageComponent,
      ),
    data: { title: 'Initialisation', authPage: 'setup' },
  },
];

/** Toute route de gestion exige une session ; l'ancienne console redirige vers la page Nœuds protégée. */
export const routes: Routes = [
  ...publicRoutes,
  ...applicationRoutes.map((route) =>
    route.redirectTo !== undefined
      ? route
      : { ...route, canActivate: [authGuard, ...(route.canActivate ?? [])] },
  ),
  { path: '**', redirectTo: '' },
];
