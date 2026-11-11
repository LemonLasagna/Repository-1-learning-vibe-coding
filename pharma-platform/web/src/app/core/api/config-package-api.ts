import { HttpClient } from '@angular/common/http';
import { inject, Injectable, InjectionToken } from '@angular/core';
import { map, Observable } from 'rxjs';
import { APP_CONFIG } from './api';

export type PackageStatus = 'DRAFT' | 'APPROVED' | 'IMPORTED' | 'DEPLOYED';

export const PACKAGE_STATUS_LABELS: Record<PackageStatus, string> = {
  DRAFT: 'Built, waiting for approval',
  APPROVED: 'Approved and signed',
  IMPORTED: 'Imported, ready to deploy',
  DEPLOYED: 'Deployed',
};

export interface PackageItemRef {
  artifactType: string;
  code: string;
  version: number;
}

export interface Deployment {
  id: string;
  targetEnvironment: string;
  deployedAt: string;
  deployedBy: string;
  result: string;
  log: string | null;
}

/** What the import's dry run decided for each item: ADD, ALREADY_RELEASED or REFUSED. */
export interface DryRunOutcome {
  code: string;
  version: number;
  outcome: string;
  problems?: string[];
}

export interface ImportLog {
  trustedAs: string;
  importedAt: string;
  items: DryRunOutcome[];
}

export interface ConfigPackage {
  id: string;
  code: string;
  name: string;
  versionLabel: string;
  sourceEnvironment: string;
  contentHash: string;
  status: PackageStatus;
  manifest: { format: string; platformVersion: string; items: PackageItemRef[] };
  approvedBy: string | null;
  approvedAt: string | null;
  approvalSignatureId: string | null;
  signingKeyVersion: number | null;
  exportedAt: string | null;
  importLog: ImportLog | null;
  rowVersion: number;
  updatedAt: string;
  /** Only on a single package: its items and its deployments. */
  items?: PackageItemRef[];
  deployments?: Deployment[];
}

export interface BuildInput {
  code: string;
  name: string;
  versionLabel: string;
  items: PackageItemRef[];
}

/** The two signed steps. Each is an electronic signature (EsigApi, entity meta.config_package); the step and the signature commit together. */
export type SignedPackageStep = 'approve' | 'deploy';

export const SIGNED_PACKAGE_STEPS: Record<SignedPackageStep, { meaning: string; button: string }> = {
  approve: { meaning: 'APPROVED', button: 'Approve and sign…' },
  deploy: { meaning: 'RELEASED', button: 'Deploy and sign…' },
};

/** What follows a signature, by its meaning. */
export const AFTER_PACKAGE_SIGNATURE: Record<string, string> = {
  APPROVED: 'The package is signed with this installation’s key and can be exported.',
  RELEASED: 'The package is deployed: new records start on its workflows, and records in progress keep theirs.',
};

export const PACKAGE_ENTITY = 'meta.config_package';

/** Saves text as a file in the browser. Replaceable, so tests can see what would be saved. */
export const DOWNLOAD = new InjectionToken<(filename: string, text: string) => void>('DOWNLOAD', {
  factory: () => (filename, text) => {
    const url = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    link.click();
    URL.revokeObjectURL(url);
  },
});

/** The reasons an import was refused, from the problem the server answers with. */
export interface ImportRefusal {
  problems: string[];
  items: DryRunOutcome[];
}

export function importRefusal(err: unknown): ImportRefusal | null {
  const e = err as { status?: number; error?: { code?: string; problems?: string[]; items?: DryRunOutcome[] } | null };
  if (e.status !== 422 || e.error?.code !== 'PACKAGE_REFUSED') return null;
  return { problems: e.error.problems ?? [], items: e.error.items ?? [] };
}

@Injectable({ providedIn: 'root' })
export class ConfigPackageApi {
  private readonly http = inject(HttpClient);
  private readonly root = `${inject(APP_CONFIG).apiBase}/api/config/v1`;
  private readonly base = `${this.root}/packages`;

  /** Which environment this installation is. In production workflows are read-only. */
  environment(): Observable<{ name: string; production: boolean }> {
    return this.http.get<{ name: string; production: boolean }>(`${this.root}/environment`);
  }

  list(): Observable<ConfigPackage[]> {
    return this.http.get<{ items: ConfigPackage[] }>(this.base).pipe(map((r) => r.items));
  }

  get(id: string): Observable<ConfigPackage> {
    return this.http.get<ConfigPackage>(`${this.base}/${id}`);
  }

  build(input: BuildInput): Observable<ConfigPackage> {
    return this.http.post<ConfigPackage>(this.base, input);
  }

  /** The package as a file. Kept as the server's text so the saved file is what the server wrote. */
  export(id: string): Observable<string> {
    return this.http.post(`${this.base}/${id}/export`, null, { responseType: 'text' });
  }

  /** Imports the text of an exported file; the server refuses it, with reasons, unless hash, signature and dry run all pass. */
  import(fileText: string): Observable<ConfigPackage> {
    return this.http.post<ConfigPackage>(`${this.base}/import`, JSON.parse(fileText) as unknown);
  }
}
