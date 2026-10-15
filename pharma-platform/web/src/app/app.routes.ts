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
    path: 'demo/deviation',
    title: 'Deviation (metadata demo) · PharmaOS',
    loadComponent: () => import('./features/deviation/deviation-demo').then((m) => m.DeviationDemoPage),
  },
  { path: '**', redirectTo: 'uoms' },
];
