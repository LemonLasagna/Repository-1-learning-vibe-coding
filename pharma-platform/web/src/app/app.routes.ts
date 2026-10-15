import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'uoms' },
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
  { path: '**', redirectTo: 'uoms' },
];
