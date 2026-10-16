import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'inbox' },
  {
    path: 'inbox',
    title: 'My work · PharmaOS',
    loadComponent: () => import('./features/inbox/inbox-page').then((m) => m.InboxPage),
  },
  {
    path: 'uoms',
    title: 'Units of measure · PharmaOS',
    loadComponent: () => import('./features/uom/uom-pages').then((m) => m.UomListPage),
  },
  {
    path: 'uoms/:id',
    title: 'Unit of measure · PharmaOS',
    loadComponent: () => import('./features/uom/uom-pages').then((m) => m.UomDetailPage),
  },
  {
    path: 'deviations',
    title: 'Deviations · PharmaOS',
    loadComponent: () => import('./features/deviation/deviation-pages').then((m) => m.DeviationListPage),
  },
  {
    path: 'deviations/new',
    title: 'New deviation · PharmaOS',
    loadComponent: () => import('./features/deviation/deviation-pages').then((m) => m.DeviationCreatePage),
  },
  {
    path: 'deviations/:id',
    title: 'Deviation · PharmaOS',
    loadComponent: () => import('./features/deviation/deviation-pages').then((m) => m.DeviationDetailPage),
  },
  {
    path: 'capas',
    title: 'CAPA · PharmaOS',
    loadComponent: () => import('./features/capa/capa-pages').then((m) => m.CapaListPage),
  },
  {
    path: 'capas/new',
    title: 'New CAPA · PharmaOS',
    loadComponent: () => import('./features/capa/capa-pages').then((m) => m.CapaCreatePage),
  },
  {
    path: 'capas/:id',
    title: 'CAPA · PharmaOS',
    loadComponent: () => import('./features/capa/capa-pages').then((m) => m.CapaDetailPage),
  },
  {
    path: 'changes',
    title: 'Change control · PharmaOS',
    loadComponent: () => import('./features/change/change-pages').then((m) => m.ChangeListPage),
  },
  {
    path: 'changes/new',
    title: 'New change · PharmaOS',
    loadComponent: () => import('./features/change/change-pages').then((m) => m.ChangeCreatePage),
  },
  {
    path: 'changes/:id',
    title: 'Change control · PharmaOS',
    loadComponent: () => import('./features/change/change-pages').then((m) => m.ChangeDetailPage),
  },
  {
    path: 'workflows',
    title: 'Workflows · PharmaOS',
    loadComponent: () => import('./features/workflow/workflow-pages').then((m) => m.WorkflowListPage),
  },
  {
    path: 'workflows/:id',
    title: 'Workflow draft · PharmaOS',
    loadComponent: () => import('./features/workflow/workflow-pages').then((m) => m.WorkflowDetailPage),
  },
  { path: '**', redirectTo: 'inbox' },
];
