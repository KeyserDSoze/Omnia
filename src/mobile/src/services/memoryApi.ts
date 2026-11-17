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

export class CloudApiError extends Error {
  constructor(message: string, public readonly status: number) { super(message); }
}

export class MemoryApi {
  constructor(private readonly config: WebCloudConfig, private readonly token: () => Promise<string>) { }

  private async request<T>(method: 'GET' | 'POST' | 'DELETE', path: string, body?: unknown): Promise<T> {
    const bearer = await this.token();
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
      throw new CloudApiError('API non raggiungibile. Nota non sincronizzata, riprova.', 0);
    }
    if (!response.ok) {
      if (response.status === 401) throw new CloudApiError('Sessione non valida. Accedi di nuovo.', 401);
      if (response.status === 403) throw new CloudApiError('Consenso o permesso mancante.', 403);
      if (response.status === 429) throw new CloudApiError('Troppe richieste. Attendi e riprova.', 429);
      throw new CloudApiError('Errore del server (' + response.status + '). Nessuna conferma di salvataggio.', response.status);
    }
    if (response.status === 204) return undefined as T;
    return response.json() as Promise<T>;
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
