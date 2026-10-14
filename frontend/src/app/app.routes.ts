import { unsavedChangesGuard } from './shared/guards/unsaved-changes-guard';
import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/auth/auth.guard';

const applicationRoutes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () =>
      import('./features/home/home-page.component').then((module) => module.HomePageComponent),
    data: { title: 'Vue d’ensemble', primaryNavigation: true },
  },
  {
    path: 'worker',
    loadComponent: () =>
      import('./features/worker/worker-console-page.component').then(
        (module) => module.WorkerConsolePageComponent,
      ),
    data: {
      title: 'Console worker',
      workerMode: true,
      description: 'Suivez les tâches et l’état de cette installation.',
    },
  },
  {
    path: 'tasks',
    loadComponent: () =>
      import('./features/tasks/task-monitoring-page.component').then(
        (module) => module.TaskMonitoringPageComponent,
      ),
    data: {
      title: 'Tâches',
      primaryNavigation: true,
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
      primaryNavigation: false,
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
      primaryNavigation: false,
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
      primaryNavigation: false,
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
      primaryNavigation: false,
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
      primaryNavigation: true,
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
      primaryNavigation: true,
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
      primaryNavigation: true,
      description: 'Consultez les rôles, capacités et états des installations du cluster.',
    },
  },
  {
    path: 'plugins',
    loadComponent: () =>
      import('./features/plugins/plugin-catalog-page.component').then(
        (module) => module.PluginCatalogPageComponent,
      ),
    data: {
      title: 'Plugins',
      primaryNavigation: true,
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
      primaryNavigation: true,
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
      import('./features/auth/login-page.component').then((module) => module.LoginPageComponent),
    data: { title: 'Initialisation', authPage: 'setup' },
  },
];

/** Toute route de gestion exige une session ; la console worker locale (`workerMode`) reste publique. */
export const routes: Routes = [
  ...publicRoutes,
  ...applicationRoutes.map((route) =>
    route.data?.['workerMode']
      ? route
      : { ...route, canActivate: [authGuard, ...(route.canActivate ?? [])] },
  ),
  { path: '**', redirectTo: '' },
];
