import { useEffect, useRef, useState, type ReactNode } from 'react';
import {
  Animated, Easing, Platform, Pressable, ScrollView, StyleSheet, Text, TextInput, View,
  type LayoutChangeEvent, type ScrollViewInstance, type StyleProp, type TextStyle, type ViewStyle
} from 'react-native';
import {
  CloudApiError, SPEECH_PURPOSE, SPEECH_SOURCE, type ConnectorProviderDto, type ConnectorProviderId,
  type MemoryApi
} from '../services/memoryApi';
import {
  answer, currentStep, startSetup, toSaveBody, type DraftAccount, type SettingsDto, type SetupState
} from '../services/setupFlow';
import {
  defaultIntegrations, greeting, hints, integrationLines, intentOf, matchChoice, matchChoices, noteText, openingLines,
  typingMs, welcomeLines, type ChatAction, type DayRecapDto, type IntegrationEntryDto
} from '../services/chatScript';
import { connectInPopup, openConnectPopup } from '../services/connectFlow';
import { defaultRoutes, routesFrom, type AiRouter, type AiRoutesDto, type Listening } from '../services/aiRouter';
import { SpeechError, speechErrorMessage } from '../services/browserSpeech';
import type { TodaySource } from '../services/demoToday';
import { demoProviders } from '../services/demoToday';
import { localeInfo, useLocale, useT } from '../i18n';
import { TodayPanel } from './TodayPanel';
import { BriefingPanel } from './BriefingPanel';
import { TaoAvatar, type AvatarState } from './TaoAvatar';
import { GoogleMark, MicrosoftMark } from './BrandMarks';
import { LanguagePicker } from './LanguagePicker';
import { Emerge, SmokeText } from './Emerge';
import { smokeMs, smokeUnits } from './effects';
import { serif, web, type Theme } from './theme';

type Line = { id: number; from: 'tao' | 'user'; text: string; start: number; ms: number; hero?: boolean };
type Widget = { id: number; from: 'tao'; widget: 'today' | 'briefing' };
type Message = Line | Widget;
const isTaoLine = (m: Message): m is Line => !('widget' in m) && m.from === 'tao';

export type ChatMode = 'demo' | 'checking' | 'signed-out' | 'signed-in' | 'invalid';
/** Voice shared with the settings; the AI router decides which engine runs. */
export type VoiceControl = { router: AiRouter; on: boolean; setOn: (on: boolean) => void };

const useNativeDriver = Platform.OS !== 'web';
const providerLabel: Record<ConnectorProviderId, string> = { microsoft: 'Microsoft', google: 'Google' };

/**
 * The whole app is this chat: the Tao symbol talks (typewriter, and the voice when enabled),
 * asks to sign in first, then runs the scripted setup (no AI) and the day's deck.
 */
export function ChatHome({ theme: t, mode, invalidReason, api, demo, profileName, socialProviders, onLogin,
  onOpenMemory, onOpenSettings, setupRequest, voice, onRoutes, still, compact }: {
  theme: Theme;
  mode: ChatMode;
  invalidReason?: string;
  api: MemoryApi | null;
  demo: TodaySource | null;
  profileName: string | null;
  socialProviders: ConnectorProviderId[];
  onLogin: (idp?: ConnectorProviderId) => void;
  onOpenMemory: (prefill?: string) => void;
  onOpenSettings: () => void;
  /** Incremented by the settings screen to re-run the setup from the chat. */
  setupRequest: number;
  voice: VoiceControl;
  onRoutes: (routes: AiRoutesDto) => void;
  /** Reduced motion: no typewriter and no animations. */
  still: boolean;
  compact: boolean;
}) {
  const tr = useT();
  const locale = useLocale();
  const [messages, setMessages] = useState<Message[]>([]);
  const [settings, setSettings] = useState<SettingsDto | null>(null);
  const [providers, setProviders] = useState<ConnectorProviderDto[]>(demo ? demoProviders : []);
  const [assistant, setAssistant] = useState(false);
  const [setup, setSetup] = useState<SetupState | null>(null);
  const [picks, setPicks] = useState<string[]>([]);
  const [queue, setQueue] = useState<DraftAccount[] | null>(null);
  const [input, setInput] = useState('');
  const [focused, setFocused] = useState(false);
  const [busy, setBusy] = useState(false);
  const [booting, setBooting] = useState(false);
  const [demoIn, setDemoIn] = useState(false);
  const [askSpeech, setAskSpeech] = useState<null | 'dictation' | 'voice'>(null);
  const [demoSpeech, setDemoSpeech] = useState(false);
  const [languageOpen, setLanguageOpen] = useState(false);
  const [typing, setTyping] = useState(false);
  const [speaking, setSpeaking] = useState(0);
  const [listening, setListening] = useState(false);
  const [preActions, setPreActions] = useState<Record<string, string>>({});
  const nextId = useRef(0);
  const live = useRef(true);
  const typingUntil = useRef(0);
  const typingTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const spokenUpTo = useRef(-1);
  const voiceWasOn = useRef(voice.on);
  const session = useRef<Listening | null>(null);
  const scroller = useRef<ScrollViewInstance>(null);
  const scrollTarget = useRef<'end' | number>('end');
  const widgetTops = useRef(new Map<number, number>());
  const submitRef = useRef<(text: string) => void>(() => { });

  /** Tao's lines are typed one after the other; the user's appear at once. */
  function say(text: string, from: 'tao' | 'user' = 'tao', hero = false) {
    const id = nextId.current++;
    const now = Date.now();
    scrollTarget.current = 'end';
    if (from === 'user') {
      setMessages(list => [...list, { id, from, text, start: now, ms: 0 }]);
      return;
    }
    const start = Math.max(now, typingUntil.current);
    // The greeting condenses out of smoke on the web; every other line is typed.
    const smoke = hero && Platform.OS === 'web';
    const ms = still ? 0 : smoke ? smokeMs(smokeUnits(text).length) : typingMs(text);
    // The next line starts while the last letters of the greeting are still settling.
    typingUntil.current = start + (smoke ? Math.round(ms * 0.6) : ms + 140);
    setMessages(list => [...list, { id, from, text, start, ms, hero }]);
    setTyping(true);
    clearTimeout(typingTimer.current);
    typingTimer.current = setTimeout(() => setTyping(false), typingUntil.current - now);
  }

  function show(widget: 'today' | 'briefing') {
    const id = nextId.current++;
    scrollTarget.current = id;
    setMessages(list => [...list.filter(m => !('widget' in m) || m.widget !== widget), { id, from: 'tao', widget }]);
  }

  useEffect(() => {
    live.current = true;
    setMessages([]);
    setSetup(null);
    setQueue(null);
    setPicks([]);
    setInput('');
    setDemoIn(false);
    setAskSpeech(null);
    setSpeaking(0);
    typingUntil.current = 0;
    widgetTops.current.clear();
    if (mode !== 'signed-in') onRoutes(defaultRoutes);
    if (mode === 'demo' || mode === 'signed-out') {
      const [hello, lead] = welcomeLines(mode);
      say(hello, 'tao', true);
      say(lead);
    } else if (mode === 'invalid') {
      say(tr('welcome.invalid'), 'tao', true);
      if (invalidReason) say(invalidReason);
    } else if (mode === 'signed-in' && api) {
      setBooting(true);
      void (async () => {
        try {
          const state = await api.settings();
          const routes = routesFrom(state.ai);
          // Before the setup there is no day to recap: one request only.
          let recap: DayRecapDto[] = [];
          if (state.settings?.onboardingCompletedAt) {
            const zone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
            recap = (await api.recap(3, zone).catch(() => ({ days: [] as DayRecapDto[] }))).days;
          }
          if (!live.current) return;
          onRoutes(routes);
          setSettings(state.settings);
          setProviders(state.providers);
          setAssistant(routes.briefing === 'server');
          say(greeting(profileName, state.settings, new Date()), 'tao', true);
          for (const line of openingLines(state.settings, recap)) say(line);
        } catch (e) {
          if (live.current) say(e instanceof CloudApiError ? e.message : tr('chat.settingsFailed'));
        } finally {
          if (live.current) setBooting(false);
        }
      })();
    }
    return () => {
      live.current = false;
      clearTimeout(typingTimer.current);
      session.current?.abort();
      session.current = null;
      voice.router.textToSpeech?.cancel();
    };
  }, [mode, api, locale]);

  // Tao speaks each new line while the voice is on; turning it on reads the latest line.
  useEffect(() => {
    const tts = voice.router.textToSpeech;
    if (voice.on && !voiceWasOn.current) {
      const latest = [...messages].reverse().find(isTaoLine);
      spokenUpTo.current = latest ? latest.id - 1 : nextId.current - 1;
    }
    if (!voice.on && voiceWasOn.current) {
      tts?.cancel();
      setSpeaking(0);
    }
    voiceWasOn.current = voice.on;
    if (!voice.on || !tts) {
      spokenUpTo.current = Math.max(spokenUpTo.current, nextId.current - 1);
      return;
    }
    for (const m of messages) {
      if (!isTaoLine(m) || m.id <= spokenUpTo.current) continue;
      spokenUpTo.current = m.id;
      tts.speak(m.text, {
        onStart: () => setSpeaking(n => n + 1),
        onEnd: () => setSpeaking(n => Math.max(0, n - 1))
      });
    }
  }, [messages, voice.on, voice.router]);

  useEffect(() => {
    if (setupRequest > 0) beginSetup();
  }, [setupRequest]);

  function beginSetup() {
    const state = startSetup(settings?.displayName ?? profileName);
    setSetup(state);
    setPicks([]);
    setQueue(null);
    say(currentStep(state).question);
  }

  function reply(value: string | string[], shown: string) {
    if (!setup) return;
    let next: SetupState;
    try { next = answer(setup, value); }
    catch { say(tr('chat.invalidChoice')); return; }
    say(shown, 'user');
    setPicks([]);
    if (next.step === 'done') {
      setSetup(null);
      void save(next.draft);
      return;
    }
    setSetup(next);
    say(currentStep(next).question);
  }

  async function save(draft: SettingsDto) {
    if (!api) {
      setSettings({ ...draft, onboardingCompletedAt: new Date().toISOString() });
      say(tr('chat.demoSaved'));
      return;
    }
    setBusy(true);
    try {
      const saved = await api.saveSettings(toSaveBody(draft, true));
      if (!live.current) return;
      setSettings(saved);
      say(tr('chat.saved'));
      startConnectQueue(saved);
    } catch (e) {
      if (live.current) say(e instanceof CloudApiError ? e.message : tr('chat.saveFailed'));
    } finally {
      if (live.current) setBusy(false);
    }
  }

  function startConnectQueue(current: SettingsDto) {
    const pending = current.accounts.filter(a => !a.connectedAccountId);
    const usable = pending.filter(a => providers.some(pr => pr.id === a.provider && pr.configured));
    if (pending.length > 0 && usable.length === 0) {
      say(tr('chat.connectUnavailable'));
      return;
    }
    if (usable.length === 0) { say(tr('chat.allSet')); return; }
    setQueue(usable);
    say(tr('chat.connectAsk', { label: usable[0].label, provider: providerLabel[usable[0].provider] }));
  }

  async function connectFirst() {
    if (!api || !queue?.length || !settings || Platform.OS !== 'web') return;
    const target = queue[0];
    const popup = openConnectPopup();
    if (!popup) { say(tr('chat.popupBlocked')); return; }
    say(tr('chat.connectNow'), 'user');
    setBusy(true);
    try {
      const account = await connectInPopup(api, target.provider, popup, new AbortController().signal);
      if (!live.current) return;
      if (!account) { say(tr('chat.connectCancelled')); return; }
      const linked = await api.saveSettings(toSaveBody({
        ...settings,
        accounts: settings.accounts.map(a => a.id === target.id ? { ...a, connectedAccountId: account.id } : a)
      }, false));
      if (!live.current) return;
      setSettings(linked);
      say(tr('chat.connected', { label: target.label, account: account.displayName }));
      nextInQueue();
    } catch (e) {
      if (live.current) say(e instanceof CloudApiError ? e.message : tr('chat.connectFailed'));
    } finally {
      if (live.current) setBusy(false);
    }
  }

  function nextInQueue(skipped = false) {
    if (skipped) say(tr('chat.connectLater'), 'user');
    const rest = (queue ?? []).slice(1);
    setQueue(rest.length > 0 ? rest : null);
    if (rest.length === 0) say(tr('chat.connectDone'));
    else say(tr('chat.connectNext', { label: rest[0].label, provider: providerLabel[rest[0].provider] }));
  }

  function act(action: ChatAction, label?: string) {
    if (label) say(label, 'user');
    switch (action) {
      case 'today': say(tr('chat.todayIntro')); show('today'); break;
      case 'briefing':
        if (!api) { say(tr('chat.briefingDemo')); break; }
        show('briefing'); break;
      case 'setup':
        if (settings?.onboardingCompletedAt && settings.accounts.some(a => !a.connectedAccountId) && api)
          startConnectQueue(settings);
        else beginSetup();
        break;
      case 'note': onOpenMemory(''); break;
      case 'memory': onOpenMemory(); break;
      case 'settings': onOpenSettings(); break;
      case 'integrations': {
        const sayAll = (entries: readonly IntegrationEntryDto[]) => integrationLines(entries).forEach(line => say(line));
        // The server catalog when signed in; the built-in list otherwise (demo, offline, older server).
        if (api) void api.integrationCatalog().then(r => sayAll(r.entries), () => sayAll(defaultIntegrations));
        else sayAll(defaultIntegrations);
        break;
      }
      case 'unknown':
        say(tr('chat.unknown'));
        break;
    }
  }

  /** Typed or dictated text; inside the setup only the scripted answers count. */
  function submitText(raw: string) {
    const text = raw.trim();
    if (!text) return;
    setInput('');
    if (setup) {
      const step = currentStep(setup);
      if (step.input === 'text') { reply(text, text); return; }
      const single = step.input === 'choice' ? matchChoice(step.choices, text) : null;
      if (single) { reply(single, step.choices.find(c => c.id === single)!.label); return; }
      const many = step.input === 'multi' ? matchChoices(step.choices, text) : [];
      if (many.length > 0) {
        reply(many, step.choices.filter(c => many.includes(c.id)).map(c => c.label).join(', '));
        return;
      }
      say(text, 'user');
      say(tr('chat.pickAnswer'));
      return;
    }
    say(text, 'user');
    const intent = intentOf(text);
    if (intent === 'note') { onOpenMemory(noteText(text)); return; }
    act(intent);
  }
  submitRef.current = submitText;

  function demoLogin(idp?: ConnectorProviderId) {
    say(idp ? tr('login.continueWith', { provider: providerLabel[idp] }) : tr('login.signIn'), 'user');
    setDemoIn(true);
    say(tr('chat.demoSignedIn'));
    say(greeting(null, null, new Date()), 'tao', true);
    say(tr('chat.demoTry'));
  }

  async function toggleVoice() {
    const tts = voice.router.textToSpeech;
    if (!tts) return;
    if (voice.on) { voice.setOn(false); return; }
    const lang = localeInfo(locale).tag;
    const kind = await tts.availability(lang);
    if (!live.current) return;
    if (kind === 'none') { say(tr('voice.none')); return; }
    if (kind === 'local') { await tts.ready(false, lang); voice.setOn(true); return; }
    // Only an online voice: Tao's text would leave the device.
    if (mode === 'signed-out') { say(tr('voice.signInForOnline')); return; }
    const allowed = await speechAllowed();
    if (!live.current) return;
    if (allowed === 'yes') { await tts.ready(true, lang); voice.setOn(true); }
    else if (allowed === 'no') askForSpeech('voice');
  }

  async function toggleMic() {
    if (session.current) { session.current.stop(); return; }
    if (!voice.router.speechToText || busy) return;
    const allowed = await speechAllowed();
    if (!live.current) return;
    if (allowed === 'yes') listen();
    else if (allowed === 'no') askForSpeech('dictation');
  }

  /** Checked every time: a consent revoked elsewhere stops the browser's speech services at once. */
  async function speechAllowed(): Promise<'yes' | 'no' | 'error'> {
    if (!api) return demoSpeech ? 'yes' : 'no';
    setBusy(true);
    try {
      const grants = await api.listConsents();
      return grants.some(g => g.source === SPEECH_SOURCE && g.purpose === SPEECH_PURPOSE && g.active) ? 'yes' : 'no';
    } catch (e) {
      if (live.current) say(e instanceof CloudApiError ? e.message : tr('speech.checkFailed'));
      return 'error';
    } finally {
      if (live.current) setBusy(false);
    }
  }

  function askForSpeech(then: 'dictation' | 'voice') {
    setAskSpeech(then);
    say(tr('speech.notice'));
  }

  async function acceptSpeech() {
    const then = askSpeech;
    setAskSpeech(null);
    say(tr('speech.accept'), 'user');
    if (api) {
      setBusy(true);
      try {
        await api.grantBrowserSpeech();
      } catch (e) {
        if (live.current) say(e instanceof CloudApiError ? e.message : tr('speech.saveFailed'));
        return;
      } finally {
        if (live.current) setBusy(false);
      }
      if (!live.current) return;
      say(tr('speech.active'));
    } else {
      setDemoSpeech(true);
      say(tr('speech.activeDemo'));
    }
    if (then === 'dictation') listen();
    if (then === 'voice' && await voice.router.textToSpeech?.ready(true, localeInfo(locale).tag) && live.current) voice.setOn(true);
  }

  function declineSpeech() {
    setAskSpeech(null);
    say(tr('speech.decline'), 'user');
    say(tr('speech.declined'));
  }

  function listen() {
    const stt = voice.router.speechToText;
    if (!stt || session.current) return;
    // Tao stops talking, otherwise the microphone would hear it.
    voice.router.textToSpeech?.cancel();
    const current = stt.listen({ lang: localeInfo(locale).tag, onInterim: text => { if (live.current) setInput(text); } });
    session.current = current;
    setListening(true);
    current.result.then(text => {
      if (!live.current) return;
      setInput('');
      if (text) submitRef.current(text);
      else say(tr('speech.notUnderstood'));
    }, (e: unknown) => {
      if (!live.current) return;
      setInput('');
      const message = speechErrorMessage(e instanceof SpeechError ? e.code : '');
      if (message) say(message);
    }).finally(() => {
      if (session.current === current) session.current = null;
      if (live.current) setListening(false);
    });
  }

  function follow() {
    const target = scrollTarget.current;
    if (target === 'end') { scroller.current?.scrollToEnd({ animated: !still }); return; }
    const top = widgetTops.current.get(target);
    if (top !== undefined) scroller.current?.scrollTo({ y: Math.max(0, top - 12), animated: !still });
  }

  const step = setup ? currentStep(setup) : null;
  const welcome = mode === 'signed-out' || mode === 'checking' || mode === 'invalid' || (mode === 'demo' && !demoIn);
  const lastId = messages.length > 0 ? messages[messages.length - 1].id : -1;
  const pause = () => Math.max(0, typingUntil.current - Date.now());
  const hintList = mode === 'demo'
    ? [{ id: 'today' as const, label: tr('hint.todayDemo') }, { id: 'setup' as const, label: tr('hint.setup') },
      { id: 'memory' as const, label: tr('hint.memory') }]
    : mode === 'signed-in' ? hints(settings, assistant) : [];
  const tts = voice.router.textToSpeech;
  const avatarState: AvatarState = listening ? 'listening' : speaking > 0 || typing ? 'speaking'
    : busy || booting || mode === 'checking' ? 'thinking' : 'idle';
  const status = listening ? tr('status.listening') : speaking > 0 ? tr('status.speaking')
    : tts ? (voice.on ? tr('status.voiceOn') : tr('status.voiceOff')) : '';
  const loginProviders = (['microsoft', 'google'] as const)
    .filter(id => mode === 'demo' || socialProviders.includes(id));
  const avatar = (size: number) => (
    <TaoAvatar size={size} state={avatarState} theme={t} still={still} testID="tao-avatar"
      label={tts ? (voice.on ? tr('avatar.mute') : tr('avatar.listen')) : 'Tao'}
      pressed={tts ? voice.on : undefined} onPress={tts ? () => { void toggleVoice(); } : undefined} />
  );

  function lineView(m: Line, centered: boolean) {
    if (m.from === 'user') return (
      <Appear key={m.id} still={still} delay={0} style={styles.userRow}>
        <View testID="chat-user" style={[styles.userBubble, { backgroundColor: t.user }, web({ backgroundImage: t.userGradient })]}>
          <Text style={[styles.userText, { color: t.accentText }]}>{m.text}</Text>
        </View>
      </Appear>
    );
    const size = m.hero ? (centered ? (compact ? styles.heroCompact : styles.hero) : styles.greeting)
      : centered ? styles.lead : styles.line;
    const textStyle = [size, {
      color: centered && !m.hero ? t.subtle : t.text, fontFamily: serif, textAlign: centered ? 'center' as const : 'left' as const
    }];
    if (m.hero && Platform.OS === 'web') return (
      <View key={m.id} testID="chat-tao" accessibilityLiveRegion="polite"
        style={centered ? styles.centered : styles.taoLine}>
        <SmokeText text={m.text} delay={m.start - Date.now()} still={still} glow={t.glow} style={textStyle} />
      </View>
    );
    return (
      <Appear key={m.id} still={still} delay={m.start - Date.now()}>
        <View testID="chat-tao" accessibilityLiveRegion="polite" style={centered ? styles.centered : styles.taoLine}>
          <Typewriter text={m.text} start={m.start} ms={m.ms} style={textStyle} />
        </View>
      </Appear>
    );
  }

  if (welcome) return (
    <View testID="chat-home" style={styles.fill}>
      <ScrollView showsVerticalScrollIndicator={false}
        contentContainerStyle={[styles.welcome, { paddingHorizontal: compact ? 22 : 32 }]}>
        {avatar(compact ? 112 : 144)}
        <View style={styles.welcomeText}>
          {messages.map(m => 'widget' in m ? null : lineView(m, true))}
        </View>
        {(mode === 'signed-out' || mode === 'demo') && messages.length > 0 && (
          <View key={'login-' + lastId} style={styles.loginWrap}>
            <LoginButtons t={t} providers={[...loginProviders]} still={still} delay={pause()}
              onLogin={idp => mode === 'demo' ? demoLogin(idp) : onLogin(idp)} />
          </View>
        )}
        {messages.length > 0 && (
          <View style={styles.languageWrap}>
            <Pressable testID="language-open" accessibilityRole="button" aria-expanded={languageOpen}
              accessibilityLabel={tr('language.label')} onPress={() => setLanguageOpen(open => !open)}>
              <Text style={[styles.linkText, { color: t.subtle }]}>{'\u{1F310} ' + localeInfo(locale).name}</Text>
            </Pressable>
            {languageOpen && <LanguagePicker theme={t} onPick={() => setLanguageOpen(false)} />}
          </View>
        )}
      </ScrollView>
    </View>
  );

  return (
    <View testID="chat-home" style={styles.fill}>
      <View style={styles.stage}>
        {avatar(compact ? 58 : 76)}
        <Text testID="tao-status" numberOfLines={1} style={[styles.status, { color: t.subtle }]}>{status}</Text>
      </View>
      <ScrollView ref={scroller} style={styles.fill} onContentSizeChange={follow}
        contentContainerStyle={[styles.column, { paddingHorizontal: compact ? 16 : 24 }]}>
        {messages.map(m => 'widget' in m ? (
          <Appear key={m.id} still={still} delay={pause()} style={styles.widget} onLayout={e => {
            widgetTops.current.set(m.id, e.nativeEvent.layout.y);
            if (scrollTarget.current === m.id) follow();
          }}>
            {m.widget === 'today' && (api || demo) && (
              <TodayPanel api={api ?? demo!} providers={providers} p={t} demo={!api} suggestions={preActions} />
            )}
            {m.widget === 'briefing' && api && <BriefingPanel api={api} p={t} onSuggestions={setPreActions} />}
          </Appear>
        ) : lineView(m, false))}

        {step && step.input !== 'none' && (
          <Appear key={'step-' + lastId} still={still} delay={pause()}>
            <View testID="setup-step" style={styles.chips}>
              {step.choices.map(choice => {
                const selected = picks.includes(choice.id);
                return <Chip key={choice.id} testID={'setup-choice-' + choice.id} t={t} selected={selected}
                  label={(selected ? '✓ ' : '') + choice.label}
                  onPress={() => step.input === 'multi'
                    ? setPicks(list => selected ? list.filter(x => x !== choice.id) : [...list, choice.id])
                    : reply(choice.id, choice.label)} />;
              })}
              {step.input === 'multi' && (
                <Chip testID="setup-confirm" label={tr('chat.confirm')} t={t} primary disabled={picks.length === 0}
                  onPress={() => reply(picks, step.choices.filter(c => picks.includes(c.id)).map(c => c.label).join(', '))} />
              )}
            </View>
          </Appear>
        )}

        {askSpeech && !busy && (
          <Appear key={'speech-' + lastId} still={still} delay={pause()}>
            <View style={styles.chips}>
              <Chip testID="voice-consent-accept" label={tr('speech.accept')} t={t} primary
                onPress={() => { void acceptSpeech(); }} />
              <Chip testID="voice-consent-decline" label={tr('speech.decline')} t={t} onPress={declineSpeech} />
            </View>
          </Appear>
        )}

        {queue && queue.length > 0 && !busy && (
          <Appear key={'queue-' + lastId} still={still} delay={pause()}>
            <View style={styles.chips}>
              <Chip testID="connect-now" label={tr('chat.connectNow')} t={t} primary onPress={() => { void connectFirst(); }} />
              <Chip testID="connect-later" label={tr('chat.connectLater')} t={t} onPress={() => nextInQueue(true)} />
            </View>
          </Appear>
        )}

        {!setup && !queue && !askSpeech && hintList.length > 0 && (
          <Appear key={'hints-' + lastId} still={still} delay={pause()}>
            <View style={styles.chips}>
              {hintList.map(h => (
                <Chip key={h.id} t={t} label={h.label} primary={h.id === 'setup' && !settings?.onboardingCompletedAt}
                  testID={h.id === 'setup' && (mode === 'demo' || !settings?.onboardingCompletedAt) ? 'setup-start' : 'chat-hint-' + h.id}
                  onPress={() => act(h.id, h.label)} />
              ))}
            </View>
          </Appear>
        )}
      </ScrollView>

      <View style={[styles.dock, { paddingHorizontal: compact ? 12 : 24 }]}>
        <View style={[styles.composer, { backgroundColor: t.surface, borderColor: focused || listening ? t.accent : t.border },
          web({ backdropFilter: 'blur(18px)', WebkitBackdropFilter: 'blur(18px)' })]}>
          {voice.router.speechToText && (
            <Pressable testID="chat-mic" accessibilityRole="button" aria-pressed={listening}
              accessibilityLabel={listening ? tr('composer.micStop') : tr('composer.micStart')} disabled={busy && !listening}
              onPress={() => { void toggleMic(); }}
              style={[styles.round, { backgroundColor: listening ? t.accent : t.card2 }]}>
              <MicGlyph color={listening ? t.accentText : t.text} />
            </Pressable>
          )}
          <TextInput testID="chat-input" value={input} onChangeText={setInput} onSubmitEditing={() => submitText(input)}
            onFocus={() => setFocused(true)} onBlur={() => setFocused(false)} editable={!busy && !listening}
            placeholder={listening ? tr('composer.listening') : step?.input === 'text' ? step.placeholder
              : voice.router.speechToText ? tr('composer.voice') : tr('composer.text')}
            placeholderTextColor={t.subtle} style={[styles.input, { color: t.text }, web({ outlineStyle: 'none' })]} />
          <Pressable testID="chat-send" accessibilityRole="button" accessibilityLabel={tr('composer.send')} disabled={busy}
            onPress={() => submitText(input)} style={[styles.round, { backgroundColor: t.accent, opacity: busy ? 0.5 : 1 }]}>
            <Text style={[styles.sendGlyph, { color: t.accentText }]}>↑</Text>
          </Pressable>
        </View>
      </View>
    </View>
  );
}

/** Fades and lifts a message in when its turn comes. */
function Appear({ delay, still, style, onLayout, children }: {
  delay: number;
  still: boolean;
  style?: StyleProp<ViewStyle>;
  onLayout?: (event: LayoutChangeEvent) => void;
  children: ReactNode;
}) {
  const shown = useRef(new Animated.Value(still ? 1 : 0)).current;
  useEffect(() => {
    if (still) { shown.setValue(1); return; }
    const animation = Animated.timing(shown, {
      toValue: 1, duration: 360, delay: Math.max(0, delay), easing: Easing.out(Easing.cubic), useNativeDriver
    });
    animation.start();
    return () => animation.stop();
  }, []);
  return (
    <Animated.View onLayout={onLayout} style={[style, {
      opacity: shown, transform: [{ translateY: shown.interpolate({ inputRange: [0, 1], outputRange: [10, 0] }) }]
    }]}>
      {children}
    </Animated.View>
  );
}

function Typewriter({ text, start, ms, style }: { text: string; start: number; ms: number; style: StyleProp<TextStyle> }) {
  const [shown, setShown] = useState(() => ms <= 0 || Date.now() >= start + ms ? text.length : 0);
  useEffect(() => {
    if (shown >= text.length) return;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const tick = () => {
      const progress = (Date.now() - start) / ms;
      const count = progress <= 0 ? 0 : Math.min(text.length, Math.ceil(progress * text.length));
      setShown(count);
      if (count < text.length) timer = setTimeout(tick, 30);
    };
    tick();
    return () => clearTimeout(timer);
  }, [text, start, ms]);
  // The rest of the line is already laid out but transparent: no reflow, full text for screen readers.
  return <Text style={style}>{text.slice(0, shown)}<Text style={styles.ghost}>{text.slice(shown)}</Text></Text>;
}

const hovered = (state: unknown) => (state as { hovered?: boolean }).hovered === true;

function LoginButtons({ t, providers, still, delay, onLogin }: {
  t: Theme;
  providers: ConnectorProviderId[];
  still: boolean;
  /** Milliseconds until Tao has finished speaking. */
  delay: number;
  onLogin: (idp?: ConnectorProviderId) => void;
}) {
  const tr = useT();
  const dark = t.mode === 'dark';
  const face = { backgroundColor: dark ? '#131314' : '#FFFFFF', borderColor: dark ? '#8E918F' : '#747775' };
  const ink = { color: dark ? '#E3E3E3' : '#1F1F1F' };
  // Each button dissolves in a beat after the previous one, then keeps pulsing.
  const step = 220;
  return (
    <View style={styles.login}>
      {providers.map((id, i) => (
        <Emerge key={id} theme={t} still={still} delay={delay + i * step} pulse={999}>
          <Pressable testID={'chat-login-' + id} accessibilityRole="button" onPress={() => onLogin(id)}
            style={state => [styles.brandButton, face, hovered(state) ? styles.lift : null]}>
            {id === 'microsoft' ? <MicrosoftMark /> : <GoogleMark />}
            <Text style={[styles.brandText, ink]}>{tr('login.continueWith', { provider: providerLabel[id] })}</Text>
          </Pressable>
        </Emerge>
      ))}
      {providers.length === 0 && (
        <Emerge theme={t} still={still} delay={delay} pulse={999}>
          <Pressable testID="chat-login" accessibilityRole="button" onPress={() => onLogin()}
            style={state => [styles.primary, { backgroundColor: t.accent }, hovered(state) ? styles.lift : null]}>
            <Text style={[styles.primaryText, { color: t.accentText }]}>{tr('login.signIn')}</Text>
          </Pressable>
        </Emerge>
      )}
    </View>
  );
}

function Chip({ label, onPress, t, primary, selected, disabled, testID }: {
  label: string; onPress: () => void; t: Theme; primary?: boolean; selected?: boolean; disabled?: boolean;
  testID?: string;
}) {
  return (
    <Pressable testID={testID} accessibilityRole="button" onPress={onPress} disabled={disabled} aria-pressed={selected}
      style={state => [styles.chip, {
        backgroundColor: primary ? t.accent : selected ? t.card2 : t.surface,
        borderColor: primary || selected ? t.accent : t.border, opacity: disabled ? 0.45 : 1
      }, web({ backdropFilter: 'blur(14px)', WebkitBackdropFilter: 'blur(14px)' }),
      hovered(state) && !disabled ? styles.lift : null]}>
      <Text style={[styles.chipText, { color: primary ? t.accentText : t.text }]}>{label}</Text>
    </Pressable>
  );
}

function MicGlyph({ color }: { color: string }) {
  return (
    <View style={styles.mic}>
      <View style={{ width: 8, height: 12, borderRadius: 4, borderWidth: 2, borderColor: color }} />
      <View style={{ width: 14, height: 7, marginTop: -4, borderWidth: 2, borderTopWidth: 0, borderColor: color,
        borderBottomLeftRadius: 7, borderBottomRightRadius: 7 }} />
      <View style={{ width: 2, height: 3, backgroundColor: color }} />
    </View>
  );
}

const styles = StyleSheet.create({
  fill: { flex: 1 },
  welcome: { flexGrow: 1, alignItems: 'center', justifyContent: 'center', paddingTop: 12, paddingBottom: 56 },
  welcomeText: { alignItems: 'center', maxWidth: 560, gap: 12, marginTop: 4 },
  centered: { alignItems: 'center' },
  stage: { alignItems: 'center' },
  status: { fontSize: 10, letterSpacing: 1.6, textTransform: 'uppercase', minHeight: 14, marginTop: -4, marginBottom: 4 },
  column: { width: '100%', maxWidth: 720, alignSelf: 'center', paddingTop: 6, paddingBottom: 28, gap: 14 },
  taoLine: { alignSelf: 'stretch', paddingRight: 28 },
  hero: { fontSize: 60, lineHeight: 68, letterSpacing: -1.5 },
  heroCompact: { fontSize: 46, lineHeight: 54, letterSpacing: -1 },
  greeting: { fontSize: 32, lineHeight: 40, letterSpacing: -0.6 },
  lead: { fontSize: 19, lineHeight: 28 },
  line: { fontSize: 19, lineHeight: 29 },
  ghost: { opacity: 0 },
  userRow: { alignItems: 'flex-end' },
  userBubble: { borderRadius: 22, borderBottomRightRadius: 6, paddingHorizontal: 16, paddingVertical: 10, maxWidth: '82%' },
  userText: { fontSize: 15, lineHeight: 21, fontWeight: '600' },
  widget: { alignSelf: 'stretch' },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  chip: { borderWidth: 1, borderRadius: 999, paddingHorizontal: 16, paddingVertical: 10 },
  chipText: { fontSize: 14, fontWeight: '700' },
  lift: { transform: [{ translateY: -1 }] },
  loginWrap: { width: '100%', alignItems: 'center' },
  languageWrap: { marginTop: 36, alignItems: 'center', gap: 14, maxWidth: 560 },
  login: { width: '100%', maxWidth: 340, gap: 12, marginTop: 30 },
  brandButton: { flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 12, borderWidth: 1,
    borderRadius: 999, minHeight: 52, paddingHorizontal: 22, boxShadow: '0 10px 30px rgba(0,0,0,0.18)' },
  brandText: { fontSize: 15, fontWeight: '600' },
  primary: { borderRadius: 999, minHeight: 52, alignItems: 'center', justifyContent: 'center' },
  primaryText: { fontSize: 15, fontWeight: '700' },
  textButton: { alignItems: 'center', paddingVertical: 8 },
  linkText: { fontSize: 13, textDecorationLine: 'underline' },
  dock: { paddingTop: 6, paddingBottom: 18 },
  composer: { width: '100%', maxWidth: 720, alignSelf: 'center', flexDirection: 'row', alignItems: 'center', gap: 8,
    borderWidth: 1, borderRadius: 999, paddingVertical: 7, paddingHorizontal: 8 },
  input: { flex: 1, fontSize: 16, minHeight: 40, paddingHorizontal: 8 },
  round: { width: 40, height: 40, borderRadius: 20, alignItems: 'center', justifyContent: 'center' },
  sendGlyph: { fontWeight: '800', fontSize: 17 },
  mic: { alignItems: 'center' }
});
