import type { ConnectorProviderDto, TodayDto, TodayItemDto, TriageDecision, TriageResultDto, WorkItemDto } from './memoryApi';

/** What the daily deck needs: the signed API in cloud mode, synthetic data in the public demo. */
export type TodaySource = {
  today(from: Date, to: Date): Promise<TodayDto>;
  decide(key: string, decision: TriageDecision,
    options?: { snoozeMinutes?: number; applyToSource?: boolean }): Promise<TriageResultDto>;
};

const demoAccount = '00000000000000000000000000000d30';

export const demoProviders: ConnectorProviderDto[] = [
  { id: 'microsoft', displayName: 'Microsoft 365 (demo)', configured: false, capabilities: ['tasks.complete'] },
  { id: 'google', displayName: 'Google Workspace (demo)', configured: false, capabilities: ['tasks.complete'] }
];

/**
 * Synthetic, in-memory deck for the static demo (GitHub Pages): no network, no storage,
 * no real person. Lets anyone try the swipe flow; nothing is ever sent anywhere.
 */
export function createDemoToday(clock: () => Date = () => new Date()): TodaySource {
  const at = (minutes: number) => new Date(clock().getTime() + minutes * 60_000).toISOString();
  const item = (over: Partial<WorkItemDto> & Pick<WorkItemDto, 'kind' | 'externalId' | 'title'>): WorkItemDto => {
    const provider = over.provider ?? 'microsoft';
    return {
      provider, accountId: demoAccount, detail: null, counterpart: null, startsAt: null, endsAt: null,
      dueAt: null, important: false, unread: false, webLink: null, ...over,
      key: provider + ':' + demoAccount + ':' + over.kind + ':' + over.externalId
    };
  };
  const decisions = new Map<string, TodayItemDto['triage']>();
  const items: { item: WorkItemDto; priority: number }[] = [
    { priority: 100, item: item({ kind: 'task', externalId: 'l|renew', title: 'Rinnovare il dominio aziendale',
      provider: 'google', detail: 'Amministrazione', dueAt: at(-180) }) },
    { priority: 90, item: item({ kind: 'task', externalId: 'l|offer', title: 'Inviare l’offerta a ACME',
      detail: 'Clienti', dueAt: at(240), important: true }) },
    { priority: 85, item: item({ kind: 'event', externalId: 'standup', title: 'Standup di team',
      counterpart: 'Giulia Verdi', startsAt: at(45), endsAt: at(60), detail: 'Teams' }) },
    { priority: 80, item: item({ kind: 'mail', externalId: 'contract', title: 'Contratto da firmare entro venerdì',
      counterpart: 'Marco Bianchi', detail: 'Ciao, ti allego la versione finale con le modifiche concordate…',
      startsAt: at(-50), important: true, unread: true }) },
    { priority: 75, item: item({ kind: 'chat', externalId: 'c|m', title: 'Luca Neri',
      counterpart: 'Luca Neri', detail: 'Ci sentiamo alle 15 per la demo?', startsAt: at(-12), unread: true }) }
  ];
  return {
    async today(from: Date, to: Date): Promise<TodayDto> {
      return {
        from: from.toISOString(), to: to.toISOString(),
        items: items.map(x => ({ key: x.item.key, item: x.item, priority: x.priority, triage: decisions.get(x.item.key) ?? null })),
        accounts: [
          { accountId: demoAccount, provider: 'microsoft', displayName: 'Demo Microsoft 365', status: 'ok' },
          { accountId: demoAccount, provider: 'google', displayName: 'Demo Google Workspace', status: 'ok' }
        ]
      };
    },
    async decide(key, decision, options = {}): Promise<TriageResultDto> {
      const target = items.find(x => x.item.key === key);
      if (!target) throw new Error('Elemento non trovato.');
      const snoozedUntil = decision === 'later' ? at(options.snoozeMinutes ?? 120) : null;
      const appliedToSource = options.applyToSource === true && decision === 'done' && target.item.kind === 'task';
      decisions.set(key, { decision, decidedAt: clock().toISOString(), snoozedUntil, appliedToSource });
      return { key, decision, snoozedUntil, appliedToSource };
    }
  };
}
