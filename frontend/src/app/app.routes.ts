import { Routes } from '@angular/router';
import { HomePageComponent } from './features/home/home-page.component';
import { SectionPlaceholderComponent } from './features/section-placeholder/section-placeholder.component';

export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    component: HomePageComponent,
    data: { title: 'Vue d’ensemble', primaryNavigation: true },
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
    path: 'libraries',
    loadComponent: () =>
      import('./features/libraries/library-catalog-page.component').then(
        (module) => module.LibraryCatalogPageComponent,
      ),
    data: {
      title: 'Bibliothèques',
      primaryNavigation: true,
      description: 'Gérez les bibliothèques partagées et leurs accès par nœud.',
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
    loadComponent: () =>
      import('./features/flows/flow-editor-page.component').then(
        (module) => module.FlowEditorPageComponent,
    ),
    data: {
      title: 'Nouveau flow',
      description: 'Assemblez les composants dans l’ordre de traitement souhaité.',
    },
  },
  {
    path: 'flows/:flowId/edit',
    loadComponent: () =>
      import('./features/flows/flow-editor-page.component').then(
        (module) => module.FlowEditorPageComponent,
      ),
    data: {
      title: 'Modifier un flow',
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
    component: SectionPlaceholderComponent,
    data: {
      title: 'Paramètres',
      primaryNavigation: true,
      description: 'Réglez les options générales de votre installation.',
    },
  },
  { path: '**', redirectTo: '' },
];
