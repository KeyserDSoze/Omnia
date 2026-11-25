import type { WebCloudConfig } from './config';
import type { ReturnTypeMemo } from '../domain/tags';
import type { SettingsDto } from './setupFlow';
import type { DayRecapDto } from './chatScript';

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

/** Chronological first-party ledger page (not a credential or auth grant). */
export type ConsentPageDto = {
  items: ConsentDto[];
  nextCursor: string | null;
};

export type RemoteMemory = {
  id: string;
  source: string;
  summary: string;
  tags: string[];
  occurredAt: string;
  hasEmbedding: boolean;
};

/** The cursor is an opaque position, not a tenant selector or credential. */
export type MemoryPageDto = {
  items: RemoteMemory[];
  nextCursor: string | null;
};

export type ErasureReceipt = {
  deletedMemories: number;
  deletedConsents: number;
  deletedConnectorAccounts?: number;
  deletedWorkState?: number;
  completedAt: string;
  scope: string;
  backupAndExternalPropagation: string;
};

export type ConnectorProviderId = 'microsoft' | 'google';
export type ConnectorProviderDto = {
  id: ConnectorProviderId;
  displayName: string;
  configured: boolean;
  capabilities: string[];
};
export type ConnectorAccountDto = {
  id: string;
  provider: ConnectorProviderId;
  displayName: string;
  email: string | null;
  connectedAt: string;
  scopes: string[];
};
export type ConnectorsDto = { providers: ConnectorProviderDto[]; accounts: ConnectorAccountDto[] };

export type WorkItemKind = 'event' | 'mail' | 'task' | 'chat';
export type WorkItemDto = {
  key: string;
  provider: ConnectorProviderId;
  accountId: string;
  kind: WorkItemKind;
  externalId: string;
  title: string;
  detail: string | null;
  counterpart: string | null;
  startsAt: string | null;
  endsAt: string | null;
  dueAt: string | null;
  important: boolean;
  unread: boolean;
  webLink: string | null;
};
export type TriageDecision = 'done' | 'not_done' | 'later' | 'dismissed';
export type TriageDto = {
  decision: TriageDecision;
  decidedAt: string;
  snoozedUntil: string | null;
  appliedToSource: boolean;
};
export type TodayItemDto = { key: string; item: WorkItemDto; priority: number; triage: TriageDto | null };
export type AccountStatusDto = {
  accountId: string;
  provider: ConnectorProviderId;
  displayName: string;
  status: 'ok' | 'reconnect_required' | 'unavailable';
};
/** Live provider signals (never stored by Tao) plus the user's own swipe decisions. */
export type TodayDto = { from: string; to: string; items: TodayItemDto[]; accounts: AccountStatusDto[] };
export type TriageResultDto = {
  key: string;
  decision: TriageDecision;
  snoozedUntil: string | null;
  appliedToSource: boolean;
};

export type AssistantInfoDto = { configured: boolean; model: string | null; host: string | null };
/** AI output: shown with an explicit label and never stored by Tao. */
export type BriefingDto = {
  summary: string;
  suggestions: { key: string; preAction: string }[];
  model: string;
  generatedAt: string;
  cardsSent: number;
};
export const ASSISTANT_SOURCE = 'assistant';
export const ASSISTANT_PURPOSE = 'assistant.briefing';
export const SPEECH_SOURCE = 'voice';
export const SPEECH_PURPOSE = 'speech.browser';

/** Settings composed by the guided setup plus what can be connected. */
export type SettingsResponseDto = {
  settings: SettingsDto | null;
  providers: ConnectorProviderDto[];
  accounts: ConnectorAccountDto[];
  /** AI router table (validate with routesFrom). */
  ai?: unknown;
};

/** Public, pre-login: which social identity providers Keycloak offers. */
export async function loginOptions(apiBaseUrl: string): Promise<ConnectorProviderId[]> {
  try {
    const response = await fetch(apiBaseUrl + '/api/login-options',
      { redirect: 'error', cache: 'no-store', credentials: 'omit' });
    if (!response.ok) return [];
    const body = await response.json() as { providers?: unknown };
    return Array.isArray(body.providers)
      ? body.providers.filter((p): p is ConnectorProviderId => p === 'microsoft' || p === 'google') : [];
  } catch {
    return [];
  }
}

/** Content-free API error codes mapped to user messages (provider text is never shown). */
const errorMessages: Record<string, string> = {
  connector_not_configured: 'Connettore non configurato sul server.',
  reconnect_required: 'Account da ricollegare: l’accesso al provider è scaduto o revocato.',
  unknown_state: 'Collegamento scaduto o già usato. Riprova.',
  expired_state: 'Collegamento scaduto. Riprova.',
  invalid_request: 'Risposta del provider non valida. Riprova.',
  authorization_failed: 'Il provider non ha concesso l’accesso.',
  invalid_identity: 'Il provider non ha restituito un’identità valida.',
  provider_error: 'Il provider non risponde. Riprova più tardi.',
  rate_limited: 'Il provider sta limitando le richieste. Riprova più tardi.',
  permission_missing: 'Permesso mancante sul provider.',
  apply_failed: 'Il provider non ha confermato l’azione: nulla è stato segnato come fatto.',
  apply_not_supported: 'Azione non disponibile per questo elemento.',
  invalid_item: 'Elemento non valido.',
  invalid_decision: 'Decisione non valida.',
  invalid_snooze: 'Intervallo di rinvio non valido.',
  invalid_window: 'Intervallo di date non valido.',
  assistant_not_configured: 'Assistente AI non configurato su questo server.',
  assistant_consent_required: 'Attiva prima l\u2019assistente: serve un consenso dedicato.',
  model_error: 'Il modello AI ha restituito un errore. Riprova pi\u00f9 tardi.',
  model_unreachable: 'Il modello AI non \u00e8 raggiungibile.',
  model_rate_limited: 'Il modello AI sta limitando le richieste. Riprova pi\u00f9 tardi.',
  invalid_model_output: 'Il modello AI ha risposto in un formato non valido. Riprova.',
  invalid_time_zone: 'Fuso orario non riconosciuto.',
  delegation_required: 'Non hai autorizzato Tao a fare questa azione su questo account: puoi cambiarlo nelle impostazioni.',
  invalid_settings: 'Impostazioni non valide: controlla i dati inseriti.',
  not_found: 'Elemento o account non trovato.'
};

async function errorCode(response: Response): Promise<string | undefined> {
  if (typeof response.json !== 'function') return undefined;
  try {
    const body = await response.json() as { error?: unknown };
    return typeof body?.error === 'string' && /^[a-z_]{1,64}$/.test(body.error) ? body.error : undefined;
  } catch {
    return undefined;
  }
}

export class CloudApiError extends Error {
  constructor(message: string, public readonly status: number, public readonly code?: string) { super(message); }
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

  private async request<T>(method: 'GET' | 'POST' | 'PUT' | 'DELETE', path: string, body?: unknown): Promise<T> {
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
      const code = await errorCode(response);
      this.assertCurrent(epoch);
      if (code && code in errorMessages && code !== 'not_found')
        throw new CloudApiError(errorMessages[code], response.status, code);
      if (response.status === 403) throw new CloudApiError('Consenso o permesso mancante.', 403, code);
      if (response.status === 409) throw new CloudApiError(
        'ID nota già usato per contenuto differente. Nota originale non sovrascritta.', 409, code);
      if (response.status === 429) throw new CloudApiError('Troppe richieste. Attendi e riprova.', 429, code);
      if (response.status === 404) throw new CloudApiError(errorMessages.not_found, 404, code);
      throw new CloudApiError('Errore del server (' + response.status + '). Nessuna conferma di salvataggio.', response.status, code);
    }
    if (response.status === 204) return undefined as T;
    const result = await response.json() as T;
    this.assertCurrent(epoch); // body consumption may itself be delayed
    return result;
  }

  listConsents(): Promise<ConsentDto[]> {
    return this.request('GET', '/api/v1/consents');
  }
  consentPage(before?: string): Promise<ConsentPageDto> {
    return this.request('GET', '/api/v1/consents/page?limit=25' +
      (before === undefined ? '' : '&before=' + encodeURIComponent(before)));
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
    // Backwards-compatible endpoint retained for older clients.
    return this.request('GET', '/api/v1/memories?limit=30');
  }
  page(before?: string): Promise<MemoryPageDto> {
    // The server bounds the page size and validates any opaque cursor.
    return this.request('GET', '/api/v1/memories/page?limit=10' +
      (before === undefined ? '' : '&before=' + encodeURIComponent(before)));
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

  connectors(): Promise<ConnectorsDto> {
    return this.request('GET', '/api/v1/connectors');
  }
  authorizeConnector(provider: ConnectorProviderId): Promise<{ authorizationUrl: string }> {
    if (provider !== 'microsoft' && provider !== 'google') throw new Error('Provider non supportato.');
    // Empty JSON body: every authenticated POST goes through the JSON body budget.
    return this.request('POST', '/api/v1/connectors/' + provider + '/authorize', {});
  }
  completeConnector(state: string, code: string): Promise<ConnectorAccountDto> {
    return this.request('POST', '/api/v1/connectors/complete', { state, code });
  }
  disconnectConnector(accountId: string): Promise<void> {
    if (!/^[a-f0-9-]{36}$/i.test(accountId)) throw new Error('Identificativo account non valido.');
    return this.request('DELETE', '/api/v1/connectors/accounts/' + encodeURIComponent(accountId));
  }
  today(from: Date, to: Date): Promise<TodayDto> {
    return this.request('GET', '/api/v1/today?from=' + encodeURIComponent(from.toISOString()) +
      '&to=' + encodeURIComponent(to.toISOString()));
  }
  decide(key: string, decision: TriageDecision,
    options: { snoozeMinutes?: number; applyToSource?: boolean } = {}): Promise<TriageResultDto> {
    return this.request('POST', '/api/v1/today/decisions', {
      key, decision, snoozeMinutes: options.snoozeMinutes ?? null, applyToSource: options.applyToSource === true
    });
  }
  assistant(): Promise<AssistantInfoDto> {
    return this.request('GET', '/api/v1/assistant');
  }
  grantAssistantBriefing(): Promise<ConsentDto> {
    return this.request('POST', '/api/v1/consents', {
      source: ASSISTANT_SOURCE,
      purpose: ASSISTANT_PURPOSE,
      policyVersion: 'assistant-v1',
      expiresAt: new Date(Date.now() + 30 * 24 * 3600 * 1000).toISOString(),
      accepted: true
    });
  }
  briefing(from: Date, to: Date, timeZone: string): Promise<BriefingDto> {
    return this.request('POST', '/api/v1/today/briefing',
      { from: from.toISOString(), to: to.toISOString(), timeZone });
  }
  grantBrowserSpeech(): Promise<ConsentDto> {
    return this.request('POST', '/api/v1/consents', {
      source: SPEECH_SOURCE,
      purpose: SPEECH_PURPOSE,
      policyVersion: 'speech-v1',
      expiresAt: new Date(Date.now() + 30 * 24 * 3600 * 1000).toISOString(),
      accepted: true
    });
  }
  recap(days: number, timeZone: string): Promise<{ days: DayRecapDto[] }> {
    return this.request('GET', '/api/v1/today/recap?days=' + encodeURIComponent(String(days)) +
      '&timeZone=' + encodeURIComponent(timeZone));
  }
  settings(): Promise<SettingsResponseDto> {
    return this.request('GET', '/api/v1/settings');
  }
  saveSettings(body: Omit<SettingsDto, 'onboardingCompletedAt'> & { completeOnboarding: boolean }): Promise<SettingsDto> {
    return this.request('PUT', '/api/v1/settings', body);
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
