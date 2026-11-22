import type { WebCloudConfig } from './config';
import type { ReturnTypeMemo } from '../domain/tags';

export type ConsentDto = {
  id: string;
  source: string;
  purpose: string;
  policyVersion: string;
  grantedAt: string;
  expiresAt: string;
  revokedAt: string | null;
  active: boolean;
};

export type RemoteMemory = {
  id: string;
  source: string;
  summary: string;
  tags: string[];
  occurredAt: string;
  hasEmbedding: boolean;
};

export type ErasureReceipt = {
  deletedMemories: number;
  deletedConsents: number;
  completedAt: string;
  scope: string;
  backupAndExternalPropagation: string;
};

export class CloudApiError extends Error {
  constructor(message: string, public readonly status: number) { super(message); }
}

/** A prior login's HTTP result cannot be delivered into a newer session. */
export class StaleCloudSessionError extends CloudApiError {
  constructor() { super('Risposta di una sessione non più attiva.', 401); }
}

export type CloudSessionBoundary = {
  /** The active browser session generation, incremented on logout/401. */
  currentEpoch: () => number;
  /** Synchronously clear private UI state and invalidate previous reads. */
  unauthorized: () => void;
};

export class MemoryApi {
  constructor(
    private readonly config: WebCloudConfig,
    private readonly token: () => Promise<string>,
    private readonly session?: CloudSessionBoundary
  ) { }

  private assertCurrent(epoch: number): void {
    if (this.session && this.session.currentEpoch() !== epoch)
      throw new StaleCloudSessionError();
  }

  private notifyUnauthorized(epoch: number): void {
    // A late 401 from an earlier signed-in account must NEVER logout a new one.
    if (this.session && this.session.currentEpoch() === epoch)
      this.session.unauthorized();
  }

  private async bearerFor(epoch: number): Promise<string> {
    let bearer: string;
    try {
      bearer = await this.token();
    } catch (error) {
      // Local OIDC token has already expired or cannot be refreshed.
      // Fail closed without issuing an API request.
      this.notifyUnauthorized(epoch);
      throw error;
    }
    this.assertCurrent(epoch);
    return bearer;
  }

  private async request<T>(method: 'GET' | 'POST' | 'DELETE', path: string, body?: unknown): Promise<T> {
    const epoch = this.session?.currentEpoch() ?? 0;
    const bearer = await this.bearerFor(epoch);
    let response: Response;
    try {
      response = await fetch(this.config.apiBaseUrl + path, {
        method,
        redirect: 'error',
        cache: 'no-store',
        credentials: 'omit',
        headers: {
          Authorization: 'Bearer ' + bearer,
          Accept: 'application/json',
          ...(body === undefined ? {} : { 'Content-Type': 'application/json' })
        },
        ...(body === undefined ? {} : { body: JSON.stringify(body) })
      });
    } catch {
      this.assertCurrent(epoch);
      throw new CloudApiError('API non raggiungibile. Nota non sincronizzata, riprova.', 0);
    }
    // Do not even deserialize an old account's HTTP result after logout.
    this.assertCurrent(epoch);
    if (!response.ok) {
      if (response.status === 401) {
        this.notifyUnauthorized(epoch);
        throw new CloudApiError('Sessione non valida. Accedi di nuovo.', 401);
      }
      if (response.status === 403) throw new CloudApiError('Consenso o permesso mancante.', 403);
      if (response.status === 429) throw new CloudApiError('Troppe richieste. Attendi e riprova.', 429);
      throw new CloudApiError('Errore del server (' + response.status + '). Nessuna conferma di salvataggio.', response.status);
    }
    if (response.status === 204) return undefined as T;
    const result = await response.json() as T;
    this.assertCurrent(epoch); // body consumption may itself be delayed
    return result;
  }

  listConsents(): Promise<ConsentDto[]> {
    return this.request('GET', '/api/v1/consents');
  }
  grantManualCapture(): Promise<ConsentDto> {
    return this.request('POST', '/api/v1/consents', {
      source: 'manual',
      purpose: 'memory.capture',
      policyVersion: 'privacy-v1',
      expiresAt: new Date(Date.now() + 30 * 24 * 3600 * 1000).toISOString(),
      accepted: true
    });
  }
  revokeConsent(id: string): Promise<void> {
    if (!/^[a-f0-9-]{36}$/i.test(id)) throw new Error('Identificativo consenso non valido.');
    return this.request('DELETE', '/api/v1/consents/' + encodeURIComponent(id));
  }
  recent(): Promise<RemoteMemory[]> {
    return this.request('GET', '/api/v1/memories?limit=30');
  }

  async exportPersonalData(): Promise<Blob> {
    const epoch = this.session?.currentEpoch() ?? 0;
    const bearer = await this.bearerFor(epoch);
    let response: Response;
    try {
      response = await fetch(this.config.apiBaseUrl + '/api/v1/privacy/export', {
        method: 'GET',
        redirect: 'error',
        cache: 'no-store',
        credentials: 'omit',
        headers: { Authorization: 'Bearer ' + bearer, Accept: 'application/json' }
      });
    } catch {
      this.assertCurrent(epoch);
      throw new CloudApiError('Esportazione non disponibile: verifica connessione e accesso.', 0);
    }
    this.assertCurrent(epoch);
    if (!response.ok) {
      if (response.status === 401) {
        this.notifyUnauthorized(epoch);
        throw new CloudApiError('Sessione non valida. Accedi di nuovo.', 401);
      }
      if (response.status === 429) throw new CloudApiError('Troppe richieste. Riprova.', 429);
      throw new CloudApiError('Esportazione non riuscita (' + response.status + ').', response.status);
    }
    const archive = await response.blob();
    this.assertCurrent(epoch); // never release a late private archive to the UI
    return archive;
  }

  erasePersonalData(confirmation: string): Promise<ErasureReceipt> {
    return this.request('POST', '/api/v1/privacy/erase', { confirmation });
  }

  save(entry: ReturnTypeMemo): Promise<RemoteMemory> {
    return this.request('POST', '/api/v1/memories', {
      source: entry.source,
      externalId: entry.externalId,
      contentType: entry.contentType,
      content: entry.content,
      occurredAt: entry.occurredAt
    });
  }
}
