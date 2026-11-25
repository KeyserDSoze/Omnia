import { describe, expect, it } from 'vitest';
import {
  answer, currentStep, newId, startSetup, summary, toSaveBody, withDelegations, type SetupState
} from '../../src/mobile/src/services/setupFlow';
import { greeting, hints, intentOf, noteText, openingLines } from '../../src/mobile/src/services/chatScript';

let n = 0;
const ids = () => `00000000-0000-4000-8000-${String(++n).padStart(12, '0')}`;
const run = (steps: (string | string[])[], name: string | null = 'Ale') =>
  steps.reduce<SetupState>((state, value) => answer(state, value, ids), startSetup(name));

describe('scripted setup (no AI)', () => {
  it('turns answers about Microsoft, Teams, Google and an extra account into settings', () => {
    const state = run([
      '',                          // keep the name from the login
      'work', ['outlook', 'teams', 'microsoftTodo'],
      'personal', ['gmail'],
      'microsoft', 'work', 'Cliente Rossi',
      'no',
      'hubspot', '08:00', '18:00', 'act_with_confirmation', 'save'
    ]);
    expect(state.step).toBe('done');
    const body = toSaveBody(state.draft, true);
    expect(body).toMatchObject({
      displayName: 'Ale', workStart: '08:00', workEnd: '18:00', agentMode: 'act_with_confirmation',
      completeOnboarding: true,
      tools: { outlook: true, teams: true, microsoftTodo: true, gmail: true, googleCalendar: false, googleTasks: false, crm: 'hubspot' }
    });
    expect(body.accounts.map(a => [a.provider, a.usage, a.label, a.delegations])).toEqual([
      ['microsoft', 'work', 'Microsoft 365 di lavoro', ['calendar.read', 'mail.read', 'chats.read', 'tasks.read', 'tasks.complete']],
      ['google', 'personal', 'Gmail personale', ['mail.read']],
      ['microsoft', 'work', 'Cliente Rossi', ['calendar.read', 'mail.read', 'chats.read', 'tasks.read', 'tasks.complete']]
    ]);
    expect(body.accounts.every(a => a.connectedAccountId === null)).toBe(true);
  });

  it('keeps writes off in read-only mode and reads everything for an account without declared tools', () => {
    const state = run(['Marco', 'no', 'no', 'google', 'personal', '', 'no', 'none', 'variable', 'variable', 'read_only', 'save']);
    expect(state.draft.displayName).toBe('Marco');
    expect(state.draft.accounts).toEqual([expect.objectContaining({
      provider: 'google', usage: 'personal', label: 'Google (1)',
      delegations: ['calendar.read', 'mail.read', 'tasks.read']
    })]);
    expect([state.draft.workStart, state.draft.workEnd, state.draft.tools.crm]).toEqual([null, null, null]);
  });

  it('only accepts the scripted choices and at least one tool', () => {
    const atMicrosoft = run(['']);
    expect(() => answer(atMicrosoft, 'forse')).toThrow();
    const atTools = answer(atMicrosoft, 'work', ids);
    expect(() => answer(atTools, [])).toThrow();
    expect(() => answer(atTools, ['slack'])).toThrow();
  });

  it('offers only end times after the start and restarts from scratch on request', () => {
    const atEnd = run(['', 'no', 'no', 'no', 'none', '10:00']);
    expect(currentStep(atEnd).choices.map(c => c.id)).toEqual(['16:00', '17:00', '18:00', '19:00', '20:00', 'variable']);
    const restarted = run(['', 'no', 'no', 'no', 'none', 'variable', 'variable', 'read_only', 'restart']);
    expect(restarted.step).toBe('name');
    expect(restarted.draft.displayName).toBe('Ale');
  });

  it('stops asking for more accounts at the limit', () => {
    let state = run(['', 'work', ['outlook'], 'no']);
    for (let i = 0; i < 9; i++) state = ['microsoft', 'work', `T${i}`].reduce((s, v) => answer(s, v, ids), state);
    expect(state.draft.accounts).toHaveLength(10);
    expect(state.step).toBe('crm');
  });

  it('summarizes in Italian and builds RFC 4122 v4 ids without a secure context', () => {
    const state = run(['', 'personal', ['outlook'], 'no', 'no', 'none', '09:00', '17:00', 'read_only']);
    expect(currentStep(state).id).toBe('summary');
    expect(summary(withDelegations(state.draft))).toEqual([
      '• Nome: Ale', '• Account: Outlook personale (personale)', '• Strumenti: Outlook',
      '• Orari: 09:00–17:00', '• Tao può: solo leggere'
    ]);
    expect(newId(bytes => bytes.fill(255))).toBe('ffffffff-ffff-4fff-bfff-ffffffffffff');
  });
});

describe('deterministic chat script', () => {
  it('maps keywords to actions and keeps notes first', () => {
    expect(intentOf('Cosa devo fare oggi?')).toBe('today');
    expect(intentOf('fammi il piano')).toBe('briefing');
    expect(intentOf('voglio collegare un altro account')).toBe('setup');
    expect(intentOf('nota: chiamare l\u2019account manager')).toBe('note');
    expect(intentOf('esporta i miei dati')).toBe('memory');
    expect(intentOf('che tempo fa?')).toBe('unknown');
    expect(noteText('Ricordami che domani c\u2019è la demo')).toBe('domani c\u2019è la demo');
  });

  it('opens with setup until onboarding is done, then with hints from previous days', () => {
    expect(openingLines(null, [])[0]).toContain('configuriamo insieme');
    expect(hints(null, true).map(h => h.id)).toEqual(['setup', 'memory']);
    const settings = { ...withDelegations(run(['', 'work', ['outlook'], 'no', 'no', 'none', 'variable', 'variable', 'read_only', 'save']).draft),
      onboardingCompletedAt: '2026-11-24T08:00:00Z' };
    const recap = [
      { day: '2026-11-23', done: 0, notDone: 1, later: 2, dismissed: 0 },
      { day: '2026-11-24', done: 5, notDone: 0, later: 1, dismissed: 0 },
      { day: '2026-11-25', done: 0, notDone: 0, later: 0, dismissed: 0 }
    ];
    expect(openingLines(settings, recap)).toEqual([
      'Ieri hai chiuso 5 carte.',
      'Nei giorni scorsi hai rimandato 3 carte: torneranno in coda.',
      '1 carta è rimasta "non fatta": vale la pena rivederle.',
      '1 account non è ancora collegato.',
      'Da dove partiamo oggi?'
    ]);
    expect(hints(settings, false).map(h => h.id)).toEqual(['today', 'setup', 'note', 'settings']);
    expect(greeting('Ale', null, new Date(2026, 10, 25, 9))).toBe('Buongiorno, Ale!');
  });
});
