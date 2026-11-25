import type { ConnectorProviderId } from './memoryApi';
import { t } from '../i18n';

/**
 * Guided setup: a fixed script written by us (no AI). Every answer is a choice or a short text;
 * the result is the settings document saved on the server, which tells the agent which accounts
 * exist and what it may do on behalf of the user.
 */
export type Usage = 'work' | 'personal';
export type AgentModeId = 'read_only' | 'act_with_confirmation';

export type DraftAccount = {
  id: string;
  provider: ConnectorProviderId;
  usage: Usage;
  label: string;
  connectedAccountId: string | null;
  delegations: string[];
};

export type WorkToolsDto = {
  outlook: boolean; teams: boolean; microsoftTodo: boolean;
  gmail: boolean; googleCalendar: boolean; googleTasks: boolean;
  crm: string | null;
};

export type SettingsDto = {
  displayName: string | null;
  workStart: string | null;
  workEnd: string | null;
  tools: WorkToolsDto;
  accounts: DraftAccount[];
  agentMode: AgentModeId;
  onboardingCompletedAt?: string | null;
};

export type StepId = 'name' | 'microsoft' | 'microsoft-tools' | 'google' | 'google-tools' | 'more' |
  'extra-usage' | 'extra-label' | 'crm' | 'start' | 'end' | 'agent' | 'summary' | 'done';

export type Choice = { id: string; label: string };
export type Step = {
  id: StepId;
  question: string;
  input: 'choice' | 'multi' | 'text' | 'none';
  choices: Choice[];
  placeholder?: string;
};

export type SetupState = {
  step: StepId;
  draft: SettingsDto;
  /** Provider of the extra account being added in the "more accounts" loop. */
  extra: { provider: ConnectorProviderId; usage: Usage | null } | null;
};

export const MAX_ACCOUNTS = 10;

const READ_BY_TOOL: Record<string, string[]> = {
  outlook: ['calendar.read', 'mail.read'], teams: ['chats.read'], microsoftTodo: ['tasks.read'],
  gmail: ['mail.read'], googleCalendar: ['calendar.read'], googleTasks: ['tasks.read']
};
const ALL_READ: Record<ConnectorProviderId, string[]> = {
  microsoft: ['calendar.read', 'mail.read', 'tasks.read', 'chats.read'],
  google: ['calendar.read', 'mail.read', 'tasks.read']
};

export function newId(random: (bytes: Uint8Array<ArrayBuffer>) => Uint8Array<ArrayBuffer> =
  bytes => globalThis.crypto.getRandomValues(bytes)): string {
  // crypto.randomUUID needs a secure context; getRandomValues also works on plain-HTTP LAN installs.
  const b = random(new Uint8Array(16));
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  const hex = Array.from(b, x => x.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export function emptyDraft(displayName: string | null): SettingsDto {
  return {
    displayName,
    workStart: null,
    workEnd: null,
    tools: { outlook: false, teams: false, microsoftTodo: false, gmail: false, googleCalendar: false,
      googleTasks: false, crm: null },
    accounts: [],
    agentMode: 'read_only'
  };
}

export function startSetup(displayName: string | null): SetupState {
  return { step: 'name', draft: emptyDraft(displayName), extra: null };
}

const providerName = (provider: ConnectorProviderId) => provider === 'microsoft' ? 'Microsoft' : 'Google';

export function currentStep(state: SetupState): Step {
  const { draft } = state;
  const variable = { id: 'variable', label: t('setup.variable') };
  switch (state.step) {
    case 'name':
      return { id: 'name', input: 'text', choices: draft.displayName ? [{ id: draft.displayName, label: draft.displayName }] : [],
        question: t('setup.name'), placeholder: t('setup.namePlaceholder') };
    case 'microsoft':
      return { id: 'microsoft', input: 'choice', question: t('setup.microsoft'),
        choices: [{ id: 'work', label: t('setup.microsoftWork') }, { id: 'personal', label: t('setup.microsoftPersonal') },
          { id: 'no', label: t('setup.no') }] };
    case 'microsoft-tools':
      return { id: 'microsoft-tools', input: 'multi', question: t('setup.microsoftTools'),
        choices: [{ id: 'outlook', label: t('setup.toolOutlook') }, { id: 'teams', label: t('setup.toolTeams') },
          { id: 'microsoftTodo', label: t('setup.toolTodo') }] };
    case 'google':
      return { id: 'google', input: 'choice', question: t('setup.google'),
        choices: [{ id: 'work', label: t('setup.googleWork') }, { id: 'personal', label: t('setup.googlePersonal') },
          { id: 'no', label: t('setup.no') }] };
    case 'google-tools':
      return { id: 'google-tools', input: 'multi', question: t('setup.googleTools'),
        choices: [{ id: 'gmail', label: 'Gmail' }, { id: 'googleCalendar', label: 'Google Calendar' },
          { id: 'googleTasks', label: 'Google Tasks' }] };
    case 'more':
      return { id: 'more', input: 'choice',
        question: draft.accounts.length === 0 ? t('setup.moreFirst')
          : t('setup.moreSoFar', { list: draft.accounts.map(a => a.label).join(', ') }),
        choices: [{ id: 'microsoft', label: t('setup.moreMicrosoft') }, { id: 'google', label: t('setup.moreGoogle') },
          { id: 'no', label: t('setup.moreNo') }] };
    case 'extra-usage':
      return { id: 'extra-usage', input: 'choice',
        question: t('setup.extraUsage', { provider: providerName(state.extra!.provider) }),
        choices: [{ id: 'work', label: t('setup.usageWork') }, { id: 'personal', label: t('setup.usagePersonal') }] };
    case 'extra-label':
      return { id: 'extra-label', input: 'text', choices: [],
        question: t('setup.extraLabel'), placeholder: t('setup.extraLabelPlaceholder') };
    case 'crm':
      return { id: 'crm', input: 'choice', question: t('setup.crm'),
        choices: [{ id: 'dynamics365', label: 'Dynamics 365' }, { id: 'salesforce', label: 'Salesforce' },
          { id: 'hubspot', label: 'HubSpot' }, { id: 'other', label: t('setup.crmOther') }, { id: 'none', label: t('setup.no') }] };
    case 'start':
      return { id: 'start', input: 'choice', question: t('setup.start'),
        choices: ['07:00', '08:00', '09:00', '10:00'].map(time => ({ id: time, label: time })).concat([variable]) };
    case 'end':
      return { id: 'end', input: 'choice', question: t('setup.end'),
        choices: ['16:00', '17:00', '18:00', '19:00', '20:00'].filter(time => !draft.workStart || time > draft.workStart)
          .map(time => ({ id: time, label: time })).concat([variable]) };
    case 'agent':
      return { id: 'agent', input: 'choice', question: t('setup.agent'),
        choices: [{ id: 'read_only', label: t('setup.agentRead') }, { id: 'act_with_confirmation', label: t('setup.agentAct') }] };
    case 'summary':
      return { id: 'summary', input: 'choice', question: [t('setup.summary'), ...summary(draft)].join('\n'),
        choices: [{ id: 'save', label: t('setup.save') }, { id: 'restart', label: t('setup.restart') }] };
    case 'done':
      return { id: 'done', input: 'none', choices: [], question: t('setup.done') };
  }
}

/** Applies one answer and returns the next state. Unknown choices throw: the script is closed. */
export function answer(state: SetupState, value: string | string[], id: () => string = () => newId()): SetupState {
  const step = currentStep(state);
  const single = Array.isArray(value) ? '' : value.trim();
  const picks = Array.isArray(value) ? value : [];
  if (step.input === 'choice' && !step.choices.some(c => c.id === single)) throw new Error(t('chat.invalidChoice'));
  if (step.input === 'multi' && (picks.length === 0 || picks.some(p => !step.choices.some(c => c.id === p))))
    throw new Error(t('chat.invalidChoice'));
  const draft = { ...state.draft, tools: { ...state.draft.tools }, accounts: [...state.draft.accounts] };
  const go = (step: StepId, extra = state.extra): SetupState => ({ step, draft, extra });
  const addAccount = (provider: ConnectorProviderId, usage: Usage, label: string) =>
    draft.accounts.push({ id: id(), provider, usage, label, connectedAccountId: null, delegations: [] });

  switch (state.step) {
    case 'name':
      draft.displayName = single.slice(0, 60) || draft.displayName;
      return go('microsoft');
    case 'microsoft':
      if (single === 'no') return go('google');
      addAccount('microsoft', single as Usage, t(single === 'work' ? 'account.microsoftWork' : 'account.microsoftPersonal'));
      return go('microsoft-tools');
    case 'microsoft-tools':
      draft.tools.outlook = picks.includes('outlook');
      draft.tools.teams = picks.includes('teams');
      draft.tools.microsoftTodo = picks.includes('microsoftTodo');
      return go('google');
    case 'google':
      if (single === 'no') return go('more');
      addAccount('google', single as Usage, t(single === 'work' ? 'account.googleWork' : 'account.googlePersonal'));
      return go('google-tools');
    case 'google-tools':
      draft.tools.gmail = picks.includes('gmail');
      draft.tools.googleCalendar = picks.includes('googleCalendar');
      draft.tools.googleTasks = picks.includes('googleTasks');
      return go('more');
    case 'more':
      if (single === 'no' || draft.accounts.length >= MAX_ACCOUNTS) return go('crm', null);
      return go('extra-usage', { provider: single as ConnectorProviderId, usage: null });
    case 'extra-usage':
      return go('extra-label', { provider: state.extra!.provider, usage: single as Usage });
    case 'extra-label': {
      const provider = state.extra!.provider;
      const count = draft.accounts.filter(a => a.provider === provider).length + 1;
      addAccount(provider, state.extra!.usage ?? 'work', single.slice(0, 60) || `${providerName(provider)} (${count})`);
      return go(draft.accounts.length >= MAX_ACCOUNTS ? 'crm' : 'more', null);
    }
    case 'crm':
      draft.tools.crm = single === 'none' ? null : single;
      return go('start');
    case 'start':
      draft.workStart = single === 'variable' ? null : single;
      return go('end');
    case 'end':
      draft.workEnd = single === 'variable' ? null : single;
      return go('agent');
    case 'agent':
      draft.agentMode = single as AgentModeId;
      return go('summary');
    case 'summary':
      if (single === 'restart') return startSetup(draft.displayName);
      return { step: 'done', draft: withDelegations(draft), extra: null };
    case 'done':
      return state;
  }
}

/**
 * What the agent may read for each account follows the tools the user said they use; writes
 * (completing tasks) are added only in "act with confirmation" mode.
 */
export function withDelegations(draft: SettingsDto): SettingsDto {
  const tools = draft.tools;
  return {
    ...draft,
    accounts: draft.accounts.map(account => {
      const declared = Object.entries(READ_BY_TOOL)
        .filter(([tool]) => tools[tool as keyof WorkToolsDto] === true &&
          (account.provider === 'microsoft') === ['outlook', 'teams', 'microsoftTodo'].includes(tool))
        .flatMap(([, caps]) => caps);
      const read = declared.length > 0 ? declared : ALL_READ[account.provider];
      const write = draft.agentMode === 'act_with_confirmation' && read.includes('tasks.read') ? ['tasks.complete'] : [];
      return { ...account, delegations: Array.from(new Set([...read, ...write])) };
    })
  };
}

export function summary(draft: SettingsDto): string[] {
  const tools = draft.tools;
  const used = [
    tools.outlook && 'Outlook', tools.teams && 'Teams', tools.microsoftTodo && 'To Do', tools.gmail && 'Gmail',
    tools.googleCalendar && 'Google Calendar', tools.googleTasks && 'Google Tasks'
  ].filter(Boolean);
  return [
    t('summary.name', { value: draft.displayName ?? t('summary.notGiven') }),
    t('summary.accounts', { value: draft.accounts.length
      ? draft.accounts.map(a => `${a.label} (${t(a.usage === 'work' ? 'summary.work' : 'summary.personal')})`).join(', ')
      : t('summary.none') }),
    t('summary.tools', { value: used.length ? used.join(', ') : t('summary.noTools') }) +
      (tools.crm ? t('summary.crm', { crm: tools.crm }) : ''),
    t('summary.hours', { value: (draft.workStart ?? t('summary.variable')) + (draft.workEnd ? `–${draft.workEnd}` : '') }),
    t('summary.can', { value: t(draft.agentMode === 'read_only' ? 'summary.readOnly' : 'summary.act') })
  ];
}

/** Body of PUT /api/v1/settings. */
export function toSaveBody(draft: SettingsDto, completeOnboarding: boolean) {
  const { onboardingCompletedAt: _ignored, ...settings } = draft;
  return { ...settings, completeOnboarding };
}
