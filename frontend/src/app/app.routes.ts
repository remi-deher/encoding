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
    component: SectionPlaceholderComponent,
    data: {
      title: 'Tâches',
      primaryNavigation: true,
      description: 'Suivez les opérations en cours et consultez leur historique.',
    },
  },
  {
    path: 'libraries',
    component: SectionPlaceholderComponent,
    data: {
      title: 'Bibliothèques',
      primaryNavigation: true,
      description: 'Gérez les bibliothèques partagées et leurs accès par nœud.',
    },
  },
  {
    path: 'flows',
    component: SectionPlaceholderComponent,
    data: {
      title: 'Flows',
      primaryNavigation: true,
      description: 'Composez les étapes qui définissent le traitement de vos médias.',
    },
  },
  {
    path: 'nodes',
    component: SectionPlaceholderComponent,
    data: {
      title: 'Nœuds',
      primaryNavigation: true,
      description: 'Configurez les rôles, capacités et accès de chaque installation.',
    },
  },
  {
    path: 'plugins',
    component: SectionPlaceholderComponent,
    data: {
      title: 'Plugins',
      primaryNavigation: true,
      description: 'Ajoutez les intégrations et composants disponibles pour vos flows.',
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
