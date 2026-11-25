import type { ConnectorProviderDto, TodayDto, TodayItemDto, TriageDecision, TriageResultDto, WorkItemDto } from './memoryApi';
import { t, type MessageKey } from '../i18n';

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
  // Texts are catalog keys, translated on every read so the demo follows the chosen language.
  const items: { item: WorkItemDto; priority: number; title?: MessageKey; detail?: MessageKey }[] = [
    { priority: 100, title: 'demo.renew', detail: 'demo.renewDetail', item: item({ kind: 'task', externalId: 'l|renew',
      title: '', provider: 'google', dueAt: at(-180) }) },
    { priority: 90, title: 'demo.offer', detail: 'demo.offerDetail', item: item({ kind: 'task', externalId: 'l|offer',
      title: '', dueAt: at(240), important: true }) },
    { priority: 85, title: 'demo.standup', item: item({ kind: 'event', externalId: 'standup', title: '',
      counterpart: 'Giulia Verdi', startsAt: at(45), endsAt: at(60), detail: 'Teams' }) },
    { priority: 80, title: 'demo.contract', detail: 'demo.contractDetail', item: item({ kind: 'mail', externalId: 'contract',
      title: '', counterpart: 'Marco Bianchi', startsAt: at(-50), important: true, unread: true }) },
    { priority: 75, detail: 'demo.chatDetail', item: item({ kind: 'chat', externalId: 'c|m', title: 'Luca Neri',
      counterpart: 'Luca Neri', startsAt: at(-12), unread: true }) }
  ];
  const shown = (x: (typeof items)[number]): WorkItemDto => ({
    ...x.item, title: x.title ? t(x.title) : x.item.title, detail: x.detail ? t(x.detail) : x.item.detail
  });
  return {
    async today(from: Date, to: Date): Promise<TodayDto> {
      return {
        from: from.toISOString(), to: to.toISOString(),
        items: items.map(x => ({ key: x.item.key, item: shown(x), priority: x.priority, triage: decisions.get(x.item.key) ?? null })),
        accounts: [
          { accountId: demoAccount, provider: 'microsoft', displayName: 'Demo Microsoft 365', status: 'ok' },
          { accountId: demoAccount, provider: 'google', displayName: 'Demo Google Workspace', status: 'ok' }
        ]
      };
    },
    async decide(key, decision, options = {}): Promise<TriageResultDto> {
      const target = items.find(x => x.item.key === key);
      if (!target) throw new Error(t('error.invalid_item'));
      const snoozedUntil = decision === 'later' ? at(options.snoozeMinutes ?? 120) : null;
      const appliedToSource = options.applyToSource === true && decision === 'done' && target.item.kind === 'task';
      decisions.set(key, { decision, decidedAt: clock().toISOString(), snoozedUntil, appliedToSource });
      return { key, decision, snoozedUntil, appliedToSource };
    }
  };
}
