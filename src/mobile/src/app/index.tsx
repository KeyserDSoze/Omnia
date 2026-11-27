import { useEffect, useMemo, useRef, useState } from 'react';
import {
  AccessibilityInfo, useColorScheme, useWindowDimensions, Platform, Pressable, ScrollView, Text, TextInput, View, StyleSheet
} from 'react-native';
import { RecordingPresets, requestRecordingPermissionsAsync, setAudioModeAsync, useAudioRecorder } from 'expo-audio';
import { normalizeMemo } from '../domain/tags';
import { configuredWebMode, type WebMode } from '../services/config';
import { createBrowserAuth, type BrowserAuth } from '../services/webAuth';
import { MemoryApi, type ConsentDto, type RemoteMemory } from '../services/memoryApi';
import { CloudViewEpoch, appendUniqueById } from '../services/cloudViewEpoch';
import { ConsentLedgerEpoch, appendUniqueConsents } from '../services/consentLedgerView';
import { filterLoadedMemories, sourceOptions } from '../services/timelineSourceFilter';
import { filterLoadedByTag, tagOptions } from '../services/timelineTagFilter';
import { groupTimelineByLocalDay } from '../services/timelineDayGrouping';
import { dayOptions, filterLoadedByLocalDay } from '../services/timelineDayFilter';
import { filterLoadedByText } from '../services/timelineTextFilter';
import { ChatHome } from '../components/ChatHome';
import { SettingsPanel } from '../components/SettingsPanel';
import { UpdateBanner } from '../components/UpdateBanner';
import { NotificationInvite } from '../components/NotificationInvite';
import { usePushNotifications } from '../services/usePushNotifications';
import { useAppUpdates } from '../services/useAppUpdates';
import { Backdrop } from '../components/Backdrop';
import { themes } from '../components/theme';
import { loginOptions, type ConnectorProviderId } from '../services/memoryApi';
import { createDemoToday } from '../services/demoToday';
import { createAiRouter, defaultRoutes, noAiRouter, type AiRoutesDto } from '../services/aiRouter';
import { browserEngines } from '../services/browserSpeech';
import { intlTag, localeInfo, t as translateNow, useLocale, useT, type MessageKey } from '../i18n';

type LocalMemo = { id: string; source: string; occurredAt: string; content: string; tags: string[]; time: string };
const webMode: WebMode = Platform.OS === 'web' ? configuredWebMode() : { kind: 'demo' };
// Public demo only: synthetic cards, no network and no storage.
const demoToday = webMode.kind === 'demo' ? createDemoToday() : null;
const activeManualGrant = (grants: ConsentDto[]) =>
  grants.find(item => item.source === 'manual' && item.purpose === 'memory.capture' && item.active) ?? null;
const timelineMemo = (item: RemoteMemory): LocalMemo => ({
  id: item.id, source: item.source, occurredAt: item.occurredAt,
  content: item.summary, tags: item.tags,
  time: new Date(item.occurredAt).toLocaleTimeString(intlTag(), { hour: '2-digit', minute: '2-digit' })
});

export default function Home() {
  const t = useT();
  const locale = useLocale();
  const updates = useAppUpdates();
  const rtl = localeInfo(locale).rtl === true;
  // The document follows the language: screen readers, hyphenation, form controls and RTL text.
  useEffect(() => {
    if (typeof document === 'undefined') return;
    document.documentElement.lang = localeInfo(locale).tag;
    document.documentElement.dir = rtl ? 'rtl' : 'ltr';
  }, [locale, rtl]);
  const device = useColorScheme();
  const [override, setOverride] = useState<'light' | 'dark' | null>(null);
  // The static export is rendered without a device: keep the first client render identical
  // (dark, no speech engines, wide layout) and adapt after hydration, otherwise React reports
  // a mismatch (#418).
  const [hydrated, setHydrated] = useState(false);
  useEffect(() => { setHydrated(true); }, []);
  const mode = override ?? (hydrated && device === 'light' ? 'light' : 'dark');
  const p = themes[mode];
  const { width, height } = useWindowDimensions();
  const compact = hydrated && (width < 640 || height < 600);
  const [still, setStill] = useState(false);
  useEffect(() => {
    void AccessibilityInfo.isReduceMotionEnabled().then(setStill).catch(() => undefined);
    const subscription = AccessibilityInfo.addEventListener('reduceMotionChanged', setStill);
    return () => subscription.remove();
  }, []);
  const [aiRoutes, setAiRoutes] = useState<AiRoutesDto>(defaultRoutes);
  const [voiceOn, setVoiceOn] = useState(false);
  const router = useMemo(() => hydrated && Platform.OS === 'web'
    ? createAiRouter(aiRoutes, browserEngines) : noAiRouter, [hydrated, aiRoutes]);
  const voice = useMemo(() => ({ router, on: voiceOn, setOn: setVoiceOn }), [router, voiceOn]);
  const [draft, setDraft] = useState('');
  const [memos, setMemos] = useState<LocalMemo[]>([]);
  // Source filters are intentionally limited to loaded, actor-scoped rows.
  // Never store filter state in the URL, browser storage or server.
  const [sourceFilter, setSourceFilter] = useState<string | null>(null);
  const [tagFilter, setTagFilter] = useState<string | null>(null);
  const [dayFilter, setDayFilter] = useState<string | null>(null);
  const [textFilter, setTextFilter] = useState('');
  // Choices come from ALL actor-scoped rows already loaded, independently
  // of current filters. Source, keyword-tag and local day compose with AND.
  // Do not treat these choices as a global database search.
  const loadedSources = sourceOptions(memos);
  const loadedTags = tagOptions(memos);
  const loadedDays = dayOptions(memos);
  const visibleMemos = filterLoadedByText(filterLoadedByLocalDay(
    filterLoadedByTag(filterLoadedMemories(memos, sourceFilter), tagFilter), dayFilter), textFilter);
  // Recompute from actor-scoped rows after each source switch, first-page
  // refresh or older-page append. No duplicate persisted day headings.
  const timelineDays = groupTimelineByLocalDay(visibleMemos);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const [olderError, setOlderError] = useState('');
  const [authState, setAuthState] = useState<'checking' | 'signed-out' | 'signed-in' | 'error'>(
    webMode.kind === 'cloud' ? 'checking' : 'signed-out');
  const [grant, setGrant] = useState<ConsentDto | null>(null);
  const [ledgerVisible, setLedgerVisible] = useState(false);
  const [ledgerRows, setLedgerRows] = useState<ConsentDto[]>([]);
  const [ledgerCursor, setLedgerCursor] = useState<string | null>(null);
  const [ledgerLoaded, setLedgerLoaded] = useState(false);
  const [ledgerLoading, setLedgerLoading] = useState(false);
  const [ledgerError, setLedgerError] = useState('');
  const [accepted, setAccepted] = useState(false);
  const [busy, setBusy] = useState(false);
  const [erasePhrase, setErasePhrase] = useState('');
  const [eraseAcknowledged, setEraseAcknowledged] = useState(false);
  const authRef = useRef<BrowserAuth | null>(null);
  const apiRef = useRef<MemoryApi | null>(null);
  const cloudViewRef = useRef(new CloudViewEpoch());
  const ledgerViewRef = useRef(new ConsentLedgerEpoch());
  const ledgerRequestRef = useRef(0);
  const ledgerInProgressRef = useRef(false);
  const pageRequestRef = useRef(0);
  const pagingInProgressRef = useRef(false);
  const sessionEpochRef = useRef(0);
  const logoutInProgressRef = useRef(false);
  const retryRef = useRef<{ raw: string; entry: ReturnType<typeof normalizeMemo> } | null>(null);
  const [recording, setRecording] = useState(false);
  const [audioUri, setAudioUri] = useState<string | null>(null);
  const [notice, setNotice] = useState('');
  const [view, setView] = useState<'chat' | 'settings' | 'memory'>('chat');
  const [profile, setProfile] = useState<{ name: string | null; email: string | null } | null>(null);
  const [socialProviders, setSocialProviders] = useState<ConnectorProviderId[]>([]);
  const [setupRequest, setSetupRequest] = useState(0);
  const recorder = useAudioRecorder(RecordingPresets.HIGH_QUALITY);

  // Keep every consent-history page and its opaque cursor confined to the
  // current signed-in view. Never retain an old account's ledger on sign-out,
  // 401, erasure, consent mutation or a new timeline refresh.
  function resetConsentLedger() {
    ledgerViewRef.current.invalidate();
    ledgerRequestRef.current++;
    ledgerInProgressRef.current = false;
    setLedgerLoading(false);
    setLedgerError('');
    setLedgerRows([]);
    setLedgerCursor(null);
    setLedgerLoaded(false);
  }

  // Invalidate the authenticated view before any awaited token/provider I/O.
  // No signed-out or expired session may continue showing its old private notes.
  function clearSignedInView() {
    sessionEpochRef.current++;
    cloudViewRef.current.invalidate();
    resetConsentLedger();
    setLedgerVisible(false);
    pageRequestRef.current++;
    pagingInProgressRef.current = false;
    setLoadingOlder(false);
    setOlderError('');
    setNextCursor(null);
    setAuthState('signed-out');
    setMemos([]);
    setSourceFilter(null);
    setTagFilter(null);
    setDayFilter(null);
    setTextFilter('');
    setGrant(null);
    setAccepted(false);
    setErasePhrase('');
    setEraseAcknowledged(false);
    setDraft('');
    retryRef.current = null;
    setProfile(null);
    setVoiceOn(false);
    // Release a pending mutation's disabled controls after API 401/logout.
    setBusy(false);
  }

  function sameSession(epoch: number): boolean {
    return sessionEpochRef.current === epoch;
  }

  async function refreshCloud(): Promise<boolean> {
    const api = apiRef.current;
    if (!api) throw new Error(t('notice.apiMissing'));
    resetConsentLedger();
    // A new first-page refresh supersedes in-flight older pages. Neither
    // previous-account nor old-page responses may repopulate the timeline.
    pageRequestRef.current++;
    pagingInProgressRef.current = false;
    setLoadingOlder(false);
    setOlderError('');
    return cloudViewRef.current.refresh(api, (consents, page) => {
      setGrant(activeManualGrant(consents));
      setMemos(page.items.map(timelineMemo));
      setNextCursor(page.nextCursor);
    });
  }

  async function loadOlder() {
    if (authState !== 'signed-in' || !apiRef.current || !nextCursor ||
        busy || pagingInProgressRef.current) return;
    const epoch = sessionEpochRef.current;
    const request = ++pageRequestRef.current;
    pagingInProgressRef.current = true;
    setLoadingOlder(true);
    setOlderError('');
    try {
      await cloudViewRef.current.append(apiRef.current, nextCursor, page => {
        setMemos(current => appendUniqueById(
          current, page.items.map(timelineMemo)));
        setNextCursor(page.nextCursor);
      });
    } catch {
      if (sameSession(epoch) && pageRequestRef.current === request)
        setOlderError(t('notice.olderFailed'));
    } finally {
      if (pageRequestRef.current === request) {
        pagingInProgressRef.current = false;
        if (sameSession(epoch)) setLoadingOlder(false);
      }
    }
  }

  async function openConsentLedger() {
    const api = apiRef.current;
    if (authState !== 'signed-in' || !api || busy || ledgerInProgressRef.current) return;
    const epoch = sessionEpochRef.current;
    resetConsentLedger();
    setLedgerVisible(true);
    const request = ++ledgerRequestRef.current;
    ledgerInProgressRef.current = true;
    setLedgerLoading(true);
    try {
      await ledgerViewRef.current.refresh(api, page => {
        setLedgerRows(page.items);
        setLedgerCursor(page.nextCursor);
        setLedgerLoaded(true);
      });
    } catch {
      if (sameSession(epoch) && ledgerRequestRef.current === request)
        setLedgerError(t('notice.ledgerFailed'));
    } finally {
      if (ledgerRequestRef.current === request) {
        ledgerInProgressRef.current = false;
        if (sameSession(epoch)) setLedgerLoading(false);
      }
    }
  }

  async function loadMoreConsents() {
    const api = apiRef.current;
    if (authState !== 'signed-in' || !api || !ledgerVisible || !ledgerCursor ||
        busy || ledgerInProgressRef.current) return;
    const epoch = sessionEpochRef.current;
    const cursor = ledgerCursor;
    const request = ++ledgerRequestRef.current;
    ledgerInProgressRef.current = true;
    setLedgerLoading(true);
    setLedgerError('');
    try {
      await ledgerViewRef.current.append(api, cursor, page => {
        setLedgerRows(current => appendUniqueConsents(current, page.items));
        setLedgerCursor(page.nextCursor);
      });
    } catch {
      if (sameSession(epoch) && ledgerRequestRef.current === request)
        setLedgerError(t('notice.ledgerPageFailed'));
    } finally {
      if (ledgerRequestRef.current === request) {
        ledgerInProgressRef.current = false;
        if (sameSession(epoch)) setLedgerLoading(false);
      }
    }
  }

  async function revokeSourceConsent(item: ConsentDto) {
    const api = apiRef.current;
    if (authState !== 'signed-in' || !item.active || !api ||
        busy || ledgerInProgressRef.current) return;
    const epoch = sessionEpochRef.current;
    // Withdraw any stale read BEFORE starting a private mutation.
    resetConsentLedger();
    cloudViewRef.current.invalidate();
    setBusy(true);
    try {
      // Server resolves the scope from the persisted, actor-owned grant ID
      // and withdraws any newer regrant for the same source+purpose.
      await api.revokeConsent(item.id);
      if (!sameSession(epoch)) return;
      if (item.source === 'manual' && item.purpose === 'memory.capture')
        setGrant(null);
      setNotice(t('notice.sourceRevoked', { source: item.source }));
      try {
        await refreshCloud();
      } catch {
        if (sameSession(epoch)) setNotice(t('notice.sourceRevokedStale', { source: item.source }));
      }
    } catch {
      if (sameSession(epoch)) setNotice(t('notice.sourceRevokeFailed'));
    } finally { if (sameSession(epoch)) setBusy(false); }
  }

  useEffect(() => {
    if (webMode.kind !== 'cloud') return;
    let live = true;
    const bootEpoch = sessionEpochRef.current;
    const auth = createBrowserAuth(webMode.config);
    authRef.current = auth;
    apiRef.current = new MemoryApi(webMode.config, () => auth.accessToken(), {
      currentEpoch: () => sessionEpochRef.current,
      unauthorized: () => {
        // Triggered only by a current-session 401 (or missing local token).
        // Never await a provider here: clear all visible personal data first.
        clearSignedInView();
        const epoch = sessionEpochRef.current;
        setNotice(translateNow('notice.sessionInvalid'));
        void auth.discardLocalSession().catch(() => {
          if (sameSession(epoch))
            setNotice(translateNow('notice.sessionInvalidClose'));
        });
      }
    });
    void (async () => {
      try {
        void loginOptions(webMode.config.apiBaseUrl).then(providers => { if (live) setSocialProviders(providers); });
        await auth.handleRedirect();
        if (!live || !sameSession(bootEpoch)) return;
        if (!await auth.hasSession()) {
          if (live && sameSession(bootEpoch)) setAuthState('signed-out');
          return;
        }
        const who = await auth.profile();
        if (live && sameSession(bootEpoch)) {
          setProfile(who);
          setAuthState('signed-in');
          await refreshCloud();
        }
      } catch (error) {
        if (live && sameSession(bootEpoch)) {
          setAuthState('error');
          setNotice(error instanceof Error ? error.message : translateNow('notice.authFailed'));
        }
      }
    })();
    return () => {
      live = false;
      sessionEpochRef.current++;
      cloudViewRef.current.invalidate();
    };
  }, []);

  async function login(idpHint?: ConnectorProviderId) {
    if (busy) return;
    // The realm has no password form: without a provider choice, pick the only one or show the buttons.
    if (!idpHint && socialProviders.length > 1) { setView('chat'); return; }
    const provider = idpHint ?? socialProviders[0];
    try { await authRef.current?.login(provider); }
    catch { setNotice(t('notice.loginFailed')); }
  }

  async function logout() {
    // Never block a user-requested logout for a slow or unresponsive API.
    // A POST already accepted by PostgreSQL is NOT rolled back by signout.
    if (logoutInProgressRef.current) return;
    logoutInProgressRef.current = true;
    const hadPendingRequest = busy;
    clearSignedInView(); // synchronous invalidation before provider I/O
    setBusy(true); // block another login until local removal has completed
    let revoked = true;
    try { await authRef.current?.logout(); }
    catch { revoked = false; }
    finally {
      logoutInProgressRef.current = false;
      setBusy(false);
      const uncertainty = hadPendingRequest ? ' ' + t('notice.pendingRequest') : '';
      setNotice(t(revoked ? 'notice.loggedOut' : 'notice.loggedOutUnconfirmed') + uncertainty);
    }
  }

  async function grantConsent() {
    if (!accepted || !apiRef.current || busy || authState !== 'signed-in') return;
    const epoch = sessionEpochRef.current;
    cloudViewRef.current.invalidate();
    resetConsentLedger();
    setBusy(true);
    try {
      await apiRef.current.grantManualCapture();
      if (!sameSession(epoch) || !await refreshCloud() || !sameSession(epoch)) return;
      setAccepted(false);
      setNotice(t('notice.consentOn'));
    } catch (error) {
      if (sameSession(epoch))
        setNotice(error instanceof Error ? error.message : t('notice.consentFailed'));
    } finally { if (sameSession(epoch)) setBusy(false); }
  }

  async function revokeConsent() {
    if (!grant || !apiRef.current || busy) return;
    const epoch = sessionEpochRef.current;
    cloudViewRef.current.invalidate();
    resetConsentLedger();
    setBusy(true);
    try {
      await apiRef.current.revokeConsent(grant.id);
      if (!sameSession(epoch)) return;
      setGrant(null);
      setAccepted(false);
      setNotice(t('notice.consentOff'));
    } catch (error) {
      if (sameSession(epoch))
        setNotice(error instanceof Error ? error.message : t('notice.consentRevokeFailed'));
    } finally { if (sameSession(epoch)) setBusy(false); }
  }

  async function exportPersonalData() {
    if (webMode.kind !== 'cloud' || authState !== 'signed-in' || !apiRef.current || busy) return;
    const epoch = sessionEpochRef.current;
    setBusy(true);
    try {
      const archive = await apiRef.current.exportPersonalData();
      if (!sameSession(epoch)) return; // never download an old session's archive
      if (archive.size === 0) throw new Error(t('notice.exportEmpty'));
      const objectUrl = URL.createObjectURL(archive);
      try {
        const element = document.createElement('a');
        element.href = objectUrl;
        element.download = 'tao-personal-data.json';
        document.body.appendChild(element);
        element.click();
        element.remove();
      } finally {
        // Browser downloads may still be initializing immediately after click.
        setTimeout(() => URL.revokeObjectURL(objectUrl), 2000);
      }
      setNotice(t('notice.exportStarted'));
    } catch (error) {
      if (sameSession(epoch))
        setNotice(error instanceof Error ? error.message : t('notice.exportFailed'));
    } finally { if (sameSession(epoch)) setBusy(false); }
  }

  async function erasePersonalData() {
    if (webMode.kind !== 'cloud' || authState !== 'signed-in' || !apiRef.current ||
        busy || erasePhrase !== t('privacy.erasePhrase') || !eraseAcknowledged) return;
    const epoch = sessionEpochRef.current;
    cloudViewRef.current.invalidate();
    resetConsentLedger();
    setLedgerVisible(false);
    setBusy(true);
    try {
      const receipt = await apiRef.current.erasePersonalData('ERASE_MY_DATA');
      if (!sameSession(epoch)) return;
      setGrant(null);
      setMemos([]);
      setSourceFilter(null);
      setTagFilter(null);
      setDayFilter(null);
      setTextFilter('');
      setNextCursor(null);
      setOlderError('');
      setDraft('');
      retryRef.current = null;
      setAccepted(false);
      setErasePhrase('');
      setEraseAcknowledged(false);
      setNotice(t('notice.erased', { memories: receipt.deletedMemories, consents: receipt.deletedConsents }));
    } catch (error) {
      if (sameSession(epoch))
        setNotice(error instanceof Error ? error.message : t('notice.eraseFailed'));
    } finally { if (sameSession(epoch)) setBusy(false); }
  }

  async function captureText() {
    if (busy) return;
    try {
      const normalized = normalizeMemo(draft, new Date().toISOString(), webMode.kind === 'cloud');
      if (webMode.kind !== 'cloud') {
        setMemos(current => [{
          id: normalized.externalId, source: normalized.source,
          occurredAt: normalized.occurredAt,
          content: normalized.content, tags: normalized.tags,
          time: new Date().toLocaleTimeString(intlTag(), { hour: '2-digit', minute: '2-digit' })
        }, ...current]);
        setDraft('');
        setNotice(t('notice.localMemo'));
        return;
      }
      if (authState !== 'signed-in') {
        setNotice(t('notice.signInFirst'));
        return;
      }
      if (!grant) {
        setNotice(t('notice.consentFirst'));
        return;
      }
      const api = apiRef.current;
      if (!api) throw new Error(t('notice.apiMissing'));
      const epoch = sessionEpochRef.current;
      const entry = retryRef.current?.raw === normalized.content ? retryRef.current.entry : normalized;
      retryRef.current = { raw: normalized.content, entry };
      setBusy(true);
      try {
        await api.save(entry);
        if (!sameSession(epoch)) return;
        retryRef.current = null;
        setDraft('');
        setNotice(t('notice.noteSaved'));
        try { await refreshCloud(); }
        catch {
          if (sameSession(epoch))
            setNotice(t('notice.noteSavedStale'));
        }
      } catch (error) {
        if (sameSession(epoch))
          setNotice(error instanceof Error ? error.message : t('notice.saveUnconfirmed'));
      } finally { if (sameSession(epoch)) setBusy(false); }
    } catch (error) {
      setNotice(error instanceof Error ? error.message : t('notice.invalidNote'));
    }
  }

  async function captureAudio() {
    try {
      if (recording) {
        await recorder.stop();
        setRecording(false);
        setAudioUri(recorder.uri ?? null);
        setNotice(t('notice.recorded'));
        return;
      }
      const { granted } = await requestRecordingPermissionsAsync();
      if (!granted) { setNotice(t('notice.micDenied')); return; }
      await setAudioModeAsync({ allowsRecording: true, playsInSilentMode: true });
      await recorder.prepareToRecordAsync();
      recorder.record();
      setAudioUri(null);
      setRecording(true);
      setNotice(t('notice.recording'));
    } catch {
      setRecording(false);
      setNotice(t('notice.recordingUnavailable'));
    }
  }

  const cardStyle = { backgroundColor: p.surface, borderColor: p.border };
  const tagName = (tag: string) => (['lavoro', 'relazioni', 'attualita', 'benessere', 'personale'].includes(tag)
    ? t(`tag.${tag}` as MessageKey) : tag);
  const heading = { color: p.text };
  const signedIn = webMode.kind === 'cloud' && authState === 'signed-in' && apiRef.current !== null;
  const canSettings = signedIn || webMode.kind === 'demo';
  const push = usePushNotifications(signedIn ? apiRef.current : null);
  const activeView = view === 'settings' && !canSettings ? 'chat' : view;
  const chatMode = webMode.kind === 'demo' ? 'demo' : webMode.kind === 'invalid' ? 'invalid' :
    authState === 'signed-in' ? 'signed-in' : authState === 'checking' ? 'checking' : 'signed-out';
  const initials = (profile?.name ?? profile?.email ?? '').split(/[\s@.]+/).filter(Boolean).slice(0, 2)
    .map(x => x[0]!.toUpperCase()).join('') || '⚙︎';
  const glass = { backgroundColor: p.mode === 'dark' ? 'rgba(20,22,30,0.55)' : 'rgba(255,255,255,0.7)' };
  return (
    <View style={styles.app} {...({ dir: rtl ? 'rtl' : 'ltr', lang: localeInfo(locale).tag } as object)}>
      <Backdrop theme={p} still={still} />
      {/* A tiny header: the brand brings back the chat, the right button opens settings or the sign-in. */}
      <View style={[styles.header, { paddingHorizontal: compact ? 14 : 22 }]}>
        <Pressable testID="open-chat" accessibilityRole="button" accessibilityLabel={t('nav.backToChat')}
          onPress={() => setView('chat')} style={styles.brand}>
          {activeView !== 'chat' && <Text style={[styles.back, { color: p.subtle }]}>{rtl ? '\u203A' : '\u2039'}</Text>}
          <Text style={[styles.glyph, heading]}>道</Text>
          <Text style={[styles.wordmark, heading]}>tao</Text>
        </Pressable>
        {canSettings ? (
          <Pressable testID="open-settings" accessibilityRole="button" accessibilityLabel={t('nav.settings')}
            onPress={() => setView(activeView === 'settings' ? 'chat' : 'settings')}
            style={[styles.avatar, { borderColor: p.border }, glass, signedIn ? { backgroundColor: p.accent } : null]}>
            <Text style={{ color: signedIn ? p.accentText : p.text, fontWeight: '800', fontSize: 12 }}>
              {signedIn ? initials : '⚙︎'}
            </Text>
          </Pressable>
        ) : webMode.kind === 'cloud' && authState !== 'checking' ? (
          <Pressable testID="header-login" accessibilityRole="button" accessibilityLabel={t('nav.signIn')}
            onPress={() => { void login(); }} style={[styles.pill, { borderColor: p.border }, glass]}>
            <Text style={[styles.pillText, heading]}>{t('nav.signIn')}</Text>
          </Pressable>
        ) : <View style={styles.avatarSpace} />}
      </View>
      {/* The chat stays mounted so its conversation survives a visit to the other views. */}
      <View style={activeView === 'chat' ? styles.fill : styles.hidden}>
        <ChatHome theme={p} mode={chatMode} invalidReason={webMode.kind === 'invalid' ? t(webMode.reason) : undefined}
          api={signedIn ? apiRef.current : null} demo={demoToday} profileName={profile?.name ?? null}
          socialProviders={socialProviders} onLogin={idp => { void login(idp); }}
          onOpenMemory={prefill => { if (prefill !== undefined) setDraft(prefill); setView('memory'); }}
          onOpenSettings={() => setView(canSettings ? 'settings' : 'memory')} setupRequest={setupRequest}
          voice={voice} onRoutes={setAiRoutes} still={still} compact={compact} />
      </View>
      {activeView === 'settings' && (
        <ScrollView style={styles.fill} contentContainerStyle={[styles.sheet, { paddingHorizontal: compact ? 14 : 22 }]}>
          <SettingsPanel api={signedIn ? apiRef.current : null} theme={p}
            profile={signedIn ? profile ?? { name: null, email: null } : null}
            accountConsoleUrl={webMode.kind === 'cloud' && socialProviders.length === 0 ? webMode.config.issuer + '/account' : null}
            onLogout={signedIn ? () => { setView('chat'); void logout(); } : null}
            onOpenMemory={() => setView('memory')}
            onRunSetup={() => { setView('chat'); setSetupRequest(n => n + 1); }}
            onToggleTheme={() => setOverride(mode === 'dark' ? 'light' : 'dark')} voice={voice} updates={updates} push={push} />
        </ScrollView>
      )}
      {activeView === 'memory' && <ScrollView style={styles.fill} contentContainerStyle={[styles.page, { paddingHorizontal: compact ? 14 : 22 }]}>
      <View style={styles.hero}>
        <Text style={[styles.eyebrow, { color: p.accent }]}>{t('memory.kicker')}</Text>
        <Text style={[styles.title, heading]}>{t('memory.title')}</Text>
        <Text style={[styles.description, { color: p.subtle }]}>{t('memory.description')}</Text>
      </View>
      <View style={[styles.card, cardStyle, { minHeight: 0, marginBottom: 20 }]}>
        <Text style={[styles.kicker, { color: p.subtle }]}>{t('memory.accessKicker')}</Text>
        <Text testID="auth-status" style={[styles.hint, heading]}>
          {webMode.kind === 'demo' ? t('memory.statusDemo') :
            webMode.kind === 'invalid' ? t(webMode.reason) :
            authState === 'checking' ? t('memory.statusChecking') :
            authState === 'signed-in' ? t('memory.statusSignedIn') :
            t('memory.statusSignedOut')}
        </Text>
        {webMode.kind === 'cloud' && authState !== 'checking' && (
          <View style={{ marginTop: 12, flexDirection: 'row', flexWrap: 'wrap', gap: 10 }}>
            {authState !== 'signed-in' ? (
              <Pressable testID="auth-login" onPress={() => { void login(); }}
                disabled={busy} accessibilityRole="button" style={[styles.secondaryButton, { backgroundColor: p.card2, paddingHorizontal: 16 }]}>
                <Text style={[styles.buttonText, heading]}>{t('memory.signIn')}</Text>
              </Pressable>
            ) : (
              <>
                <Pressable testID="sync-reload" onPress={() => {
                  if (busy) return;
                  const epoch = sessionEpochRef.current;
                  setBusy(true);
                  void refreshCloud().catch(() => {
                    if (sameSession(epoch))
                      setNotice(t('notice.reloadFailed'));
                  }).finally(() => {
                    if (sameSession(epoch)) setBusy(false);
                  });
                }} disabled={busy}
                  accessibilityRole="button" style={[styles.secondaryButton, { backgroundColor: p.card2, paddingHorizontal: 16 }]}>
                  <Text style={[styles.buttonText, heading]}>{t('memory.reload')}</Text>
                </Pressable>
                <Pressable testID="auth-logout" onPress={() => { void logout(); }}
                  accessibilityRole="button" style={[styles.secondaryButton, { backgroundColor: p.card2, paddingHorizontal: 16 }]}>
                  <Text style={[styles.buttonText, heading]}>{t('memory.logout')}</Text>
                </Pressable>
              </>
            )}
          </View>
        )}
        {webMode.kind === 'cloud' && authState === 'signed-in' && (
          <View style={{ marginTop: 16 }}>
            {grant ? (
              <>
                <Text testID="consent-state" style={[styles.hint, heading]}>
                  {t('memory.consentActive', { date: new Date(grant.expiresAt).toLocaleDateString(intlTag()) })}
                </Text>
                <Pressable testID="revoke-consent" accessibilityRole="button"
                  onPress={() => { void revokeConsent(); }} disabled={busy}
                  style={[styles.secondaryButton, { backgroundColor: p.card2, paddingHorizontal: 16, marginTop: 9, alignSelf: 'flex-start' }]}>
                  <Text style={[styles.buttonText, heading]}>{t('memory.consentRevoke')}</Text>
                </Pressable>
              </>
            ) : (
              <>
                <Text testID="consent-state" style={[styles.hint, heading]}>{t('memory.consentNone')}</Text>
                <Pressable testID="consent-accept" accessibilityRole="checkbox"
                  accessibilityState={{ checked: accepted }} onPress={() => setAccepted(!accepted)}
                  style={{ marginTop: 12, flexDirection: 'row', alignItems: 'flex-start', gap: 9 }}>
                  <Text style={heading}>{accepted ? '☑' : '☐'}</Text>
                  <Text style={[styles.hint, heading, { flex: 1, marginTop: 0 }]}>{t('memory.consentText')}</Text>
                </Pressable>
                <Pressable testID="grant-consent" accessibilityRole="button"
                  disabled={!accepted || busy} onPress={() => { void grantConsent(); }}
                  style={[styles.secondaryButton, { backgroundColor: p.card2,
                    paddingHorizontal: 16, marginTop: 11, opacity: accepted && !busy ? 1 : 0.5, alignSelf: 'flex-start' }]}>
                  <Text style={[styles.buttonText, heading]}>{t('memory.consentGrant')}</Text>
                </Pressable>
              </>
            )}
          </View>
        )}
        {webMode.kind === 'cloud' && authState === 'signed-in' && (
          <View testID="consent-ledger" style={{ marginTop: 18, borderTopWidth: 1,
            borderTopColor: p.border, paddingTop: 15 }}>
            <Text style={[styles.kicker, { color: p.subtle }]}>{t('ledger.kicker')}</Text>
            <Text style={[styles.hint, heading]}>{t('ledger.hint')}</Text>
            <Pressable testID="consent-ledger-open" accessibilityRole="button"
              disabled={busy || ledgerLoading}
              onPress={() => { void openConsentLedger(); }}
              style={[styles.secondaryButton, { backgroundColor: p.card2,
                paddingHorizontal: 16, marginTop: 11, alignSelf: 'flex-start',
                opacity: busy || ledgerLoading ? 0.5 : 1 }]}>
              <Text style={[styles.buttonText, heading]}>
                {ledgerVisible ? t('ledger.refresh') : t('ledger.open')}
              </Text>
            </Pressable>
            {ledgerVisible && (
              <View style={{ marginTop: 11 }}>
                <Text testID="consent-ledger-count" style={[styles.hint, heading]}>
                  {ledgerLoaded
                    ? t('ledger.count', { count: ledgerRows.length }) + ' ' + t(ledgerCursor ? 'ledger.more' : 'ledger.end')
                    : ledgerLoading ? t('ledger.loading') : t('ledger.stale')}
                </Text>
                {ledgerRows.map(item => (
                  <View key={item.id} testID="consent-ledger-row"
                    style={{ borderTopWidth: 1, borderColor: p.border,
                      paddingVertical: 11 }}>
                    <Text style={[styles.memoText, heading]}>
                      {item.source} · {item.purpose}
                    </Text>
                    <Text style={[styles.hint, { color: p.subtle }]}>
                      {item.active
                        ? t('ledger.activeUntil', { date: new Date(item.expiresAt).toLocaleDateString(intlTag()) })
                        : item.revokedAt
                          ? t('ledger.revokedOn', { date: new Date(item.revokedAt).toLocaleDateString(intlTag()) })
                          : t('ledger.expiredOn', { date: new Date(item.expiresAt).toLocaleDateString(intlTag()) })}
                      {' \u00B7 ' + t('ledger.policy', { version: item.policyVersion })}
                    </Text>
                    {item.active && (
                      <Pressable testID="consent-ledger-revoke" accessibilityRole="button"
                        accessibilityLabel={t('ledger.revokeLabel', { source: item.source })}
                        disabled={busy || ledgerLoading}
                        onPress={() => { void revokeSourceConsent(item); }}
                        style={[styles.secondaryButton, { backgroundColor: p.card2,
                          paddingHorizontal: 12, marginTop: 8, alignSelf: 'flex-start',
                          opacity: busy || ledgerLoading ? 0.5 : 1 }]}>
                        <Text style={[styles.buttonText, heading]}>
                          {t('ledger.revoke', { source: item.source })}
                        </Text>
                      </Pressable>
                    )}
                  </View>
                ))}
                {ledgerLoaded && ledgerCursor && (
                  <Pressable testID="consent-ledger-more" accessibilityRole="button"
                    disabled={busy || ledgerLoading}
                    onPress={() => { void loadMoreConsents(); }}
                    style={[styles.secondaryButton, { backgroundColor: p.card2,
                      paddingHorizontal: 16, marginTop: 12, alignSelf: 'flex-start' }]}>
                    <Text style={[styles.buttonText, heading]}>
                      {ledgerLoading ? t('common.loading') : t('ledger.loadMore')}
                    </Text>
                  </Pressable>
                )}
                {!!ledgerError && (
                  <Text testID="consent-ledger-error" accessibilityRole="alert"
                    style={[styles.hint, heading]}>{ledgerError}</Text>
                )}
              </View>
            )}
          </View>
        )}
        {webMode.kind === 'cloud' && authState === 'signed-in' && (
          <View testID="privacy-actions" style={{ marginTop: 20, borderTopWidth: 1,
            borderTopColor: p.border, paddingTop: 15 }}>
            <Text style={[styles.kicker, { color: p.subtle }]}>{t('privacy.kicker')}</Text>
            <Text style={[styles.hint, heading]}>{t('privacy.exportText')}</Text>
            <Pressable testID="export-data" accessibilityRole="button"
              disabled={busy} onPress={() => { void exportPersonalData(); }}
              style={[styles.secondaryButton, { backgroundColor: p.card2,
                paddingHorizontal: 16, alignSelf: 'flex-start', marginTop: 12 }]}>
              <Text style={[styles.buttonText, heading]}>{t('privacy.export')}</Text>
            </Pressable>
            <Text style={[styles.hint, { color: p.subtle, marginTop: 19 }]}>{t('privacy.eraseText')}</Text>
            <Pressable testID="erase-acknowledge" accessibilityRole="checkbox"
              accessibilityState={{ checked: eraseAcknowledged }}
              onPress={() => setEraseAcknowledged(value => !value)}
              style={{ flexDirection: 'row', marginTop: 12, gap: 9 }}>
              <Text style={heading}>{eraseAcknowledged ? '☑' : '☐'}</Text>
              <Text style={[styles.hint, heading, { flex: 1, marginTop: 0 }]}>{t('privacy.eraseAck')}</Text>
            </Pressable>
            <TextInput testID="erase-confirmation"
              accessibilityLabel={t('privacy.erasePlaceholder', { phrase: t('privacy.erasePhrase') })}
              placeholder={t('privacy.erasePlaceholder', { phrase: t('privacy.erasePhrase') })}
              placeholderTextColor={p.subtle} value={erasePhrase}
              onChangeText={setErasePhrase}
              style={[styles.input, { minHeight: 42, marginTop: 9, color: p.text,
                backgroundColor: p.bg, borderColor: p.border }]} />
            <Pressable testID="erase-data" accessibilityRole="button"
              disabled={busy || !eraseAcknowledged || erasePhrase !== t('privacy.erasePhrase')}
              onPress={() => { void erasePersonalData(); }}
              style={[styles.secondaryButton, { backgroundColor: p.card2,
                alignSelf: 'flex-start', paddingHorizontal: 16, marginTop: 9,
                opacity: !busy && eraseAcknowledged && erasePhrase === t('privacy.erasePhrase') ? 1 : 0.45 }]}>
              <Text style={[styles.buttonText, heading]}>{t('privacy.erase')}</Text>
            </Pressable>
          </View>
        )}
      </View>
      <View style={styles.columns}>
        <View style={[styles.card, cardStyle, styles.primaryCard]}>
          <Text style={[styles.kicker, { color: p.subtle }]}>{t('capture.kicker')}</Text>
          <Text style={[styles.cardTitle, heading]}>{t('capture.title')}</Text>
          <TextInput testID="memo-input" placeholder={t('capture.placeholder')} placeholderTextColor={p.subtle} multiline value={draft} onChangeText={value => {
            setDraft(value);
            retryRef.current = null;
          }} style={[styles.input, { color: p.text, backgroundColor: p.bg, borderColor: p.border }]} />
          <Pressable testID="save-memo" onPress={() => { void captureText(); }} disabled={busy}
            accessibilityRole="button" style={[styles.mainButton, { backgroundColor: p.accent }]}>
            <Text style={[styles.buttonText, { color: p.accentText }]}>{t('capture.save')}</Text>
          </Pressable>
          <View style={[styles.separator, { backgroundColor: p.border }]} />
          <Pressable testID="record-audio" onPress={() => { void captureAudio(); }} accessibilityRole="button" style={[styles.secondaryButton, { backgroundColor: p.card2 }]}>
            <Text style={[styles.buttonText, heading]}>{recording ? t('capture.stop') : t('capture.record')}</Text>
          </Pressable>
          <Text style={[styles.hint, { color: p.subtle }]}>{t('capture.recordHint')}</Text>
          {audioUri && <Text testID="audio-result" style={[styles.hint, heading]}>{t('capture.audioDone')}</Text>}
          {!!notice && <Text accessibilityRole="alert" testID="notice" style={[styles.notice, { color: p.text }]}>{notice}</Text>}
        </View>
        <View testID="memory-timeline" style={[styles.card, cardStyle, styles.sideCard]}>
          <Text style={[styles.kicker, { color: p.subtle }]}>{t('timeline.kicker')}</Text>
          <Text style={[styles.cardTitle, heading]}>{t('timeline.title')}</Text>
          {webMode.kind === 'cloud' && authState === 'signed-in' && memos.length > 0 && (
            <View testID="memory-source-filter" style={{ marginBottom: 12 }}>
              <Text style={[styles.hint, { color: p.subtle }]}>{t('timeline.filtersHint')}</Text>
              <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: 8, marginTop: 11 }}>
                <Pressable testID="memory-source-all" accessibilityRole="button"
                  accessibilityLabel={t('timeline.allSourcesLabel')}
                  accessibilityState={{ selected: sourceFilter === null }}
                  onPress={() => setSourceFilter(null)}
                  style={[styles.secondaryButton, { backgroundColor: p.card2,
                    paddingHorizontal: 12, minHeight: 38,
                    borderWidth: sourceFilter === null ? 2 : 0, borderColor: p.accent }]}>
                  <Text style={[styles.buttonText, heading]}>{t('timeline.allSources')}</Text>
                </Pressable>
                {loadedSources.map(source => (
                  <Pressable key={source} testID="memory-source-option"
                    accessibilityRole="button"
                    accessibilityLabel={t('timeline.sourceLabel', { source })}
                    accessibilityState={{ selected: sourceFilter === source }}
                    onPress={() => setSourceFilter(source)}
                    style={[styles.secondaryButton, { backgroundColor: p.card2,
                      paddingHorizontal: 12, minHeight: 38,
                      borderWidth: sourceFilter === source ? 2 : 0, borderColor: p.accent }]}>
                    <Text style={[styles.buttonText, heading]}>{source}</Text>
                  </Pressable>
                ))}
              </View>
              <View testID="memory-tag-filter" style={{ marginTop: 14 }}>
                <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: 8, marginTop: 11 }}>
                  <Pressable testID="memory-tag-all" accessibilityRole="button"
                    accessibilityLabel={t('timeline.allTagsLabel')}
                    accessibilityState={{ selected: tagFilter === null }}
                    onPress={() => setTagFilter(null)}
                    style={[styles.secondaryButton, { backgroundColor: p.card2,
                      paddingHorizontal: 12, minHeight: 38,
                      borderWidth: tagFilter === null ? 2 : 0, borderColor: p.accent }]}>
                    <Text style={[styles.buttonText, heading]}>{t('timeline.allTags')}</Text>
                  </Pressable>
                  {loadedTags.map(tag => (
                    <Pressable key={tag} testID="memory-tag-option"
                      accessibilityRole="button"
                      accessibilityLabel={t('timeline.tagLabel', { tag: tagName(tag) })}
                      accessibilityState={{ selected: tagFilter === tag }}
                      onPress={() => setTagFilter(tag)}
                      style={[styles.secondaryButton, { backgroundColor: p.card2,
                        paddingHorizontal: 12, minHeight: 38,
                        borderWidth: tagFilter === tag ? 2 : 0, borderColor: p.accent }]}>
                      <Text style={[styles.buttonText, heading]}>{tagName(tag)}</Text>
                    </Pressable>
                  ))}
                </View>
              </View>
              <View testID="memory-day-filter" style={{ marginTop: 14 }}>
                <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: 8, marginTop: 11 }}>
                  <Pressable testID="memory-day-all" accessibilityRole="button"
                    accessibilityLabel={t('timeline.allDaysLabel')}
                    accessibilityState={{ selected: dayFilter === null }}
                    onPress={() => setDayFilter(null)}
                    style={[styles.secondaryButton, { backgroundColor: p.card2,
                      paddingHorizontal: 12, minHeight: 38,
                      borderWidth: dayFilter === null ? 2 : 0, borderColor: p.accent }]}>
                    <Text style={[styles.buttonText, heading]}>{t('timeline.allDays')}</Text>
                  </Pressable>
                  {loadedDays.map(day => (
                    <Pressable key={day.key} testID="memory-day-option"
                      accessibilityRole="button"
                      accessibilityLabel={t('timeline.dayLabel', { day: day.label })}
                      accessibilityState={{ selected: dayFilter === day.key }}
                      onPress={() => setDayFilter(day.key)}
                      style={[styles.secondaryButton, { backgroundColor: p.card2,
                        paddingHorizontal: 12, minHeight: 38,
                        borderWidth: dayFilter === day.key ? 2 : 0, borderColor: p.accent }]}>
                      <Text style={[styles.buttonText, heading]}>{day.label}</Text>
                    </Pressable>
                  ))}
                </View>
              </View>
              <View testID="memory-text-filter-container" style={{ marginTop: 14 }}>
                <TextInput testID="memory-text-filter"
                  accessibilityLabel={t('timeline.searchLabel')}
                  placeholder={t('timeline.search')}
                  placeholderTextColor={p.subtle}
                  value={textFilter} maxLength={100} autoCapitalize="none"
                  autoCorrect={false}
                  onChangeText={value => setTextFilter(value.slice(0, 100))}
                  style={[styles.input, { minHeight: 44, marginTop: 9,
                    color: p.text, backgroundColor: p.bg, borderColor: p.border }]} />
                {textFilter.length > 0 && (
                  <Pressable testID="memory-text-clear" accessibilityRole="button"
                    accessibilityLabel={t('timeline.clearSearchLabel')}
                    onPress={() => setTextFilter('')}
                    style={[styles.secondaryButton, { backgroundColor: p.card2,
                      paddingHorizontal: 12, marginTop: 8,
                      alignSelf: 'flex-start', minHeight: 38 }]}>
                    <Text style={[styles.buttonText, heading]}>{t('timeline.clearSearch')}</Text>
                  </Pressable>
                )}
              </View>
              <Text testID="memory-source-count" style={[styles.hint, { color: p.subtle }]}>
                {t('timeline.count', { visible: visibleMemos.length, total: memos.length })}{' '}
                {t(nextCursor ? 'timeline.morePages' : 'timeline.endPages')}
              </Text>
            </View>
          )}
          {memos.length === 0 ? (
            <View style={styles.empty}>
              <Text style={{ fontSize: 35 }}>✦</Text>
              <Text style={[styles.emptyTitle, heading]}>{t('timeline.emptyTitle')}</Text>
              <Text style={[styles.hint, { color: p.subtle }]}>{t(webMode.kind === 'cloud' ? 'timeline.emptyCloud' : 'timeline.emptyLocal')}</Text>
            </View>
          ) : visibleMemos.length === 0 ? (
            <Text testID="memory-source-empty" style={[styles.hint, { color: p.subtle }]}>
              {t('timeline.noMatch')}{nextCursor ? ' ' + t('timeline.noMatchMore') : ''}
            </Text>
          ) : timelineDays.map(day => (
            <View key={day.key} testID="memory-day-group">
              <Text testID="memory-day-heading" accessibilityRole="header"
                style={[styles.dayHeading, { color: p.text }]}>
                {day.label}
              </Text>
              {day.items.map(memo => (
                <View key={memo.id} testID="memory-day-item"
                  style={[styles.memo, { borderColor: p.border }]}>
                  <Text style={[styles.memoTime, { color: p.subtle }]}>
                    {memo.time} · {memo.source} · {memo.tags.map(tagName).join(', ')}
                  </Text>
                  <Text style={[styles.memoText, heading]}>{memo.content}</Text>
                </View>
              ))}
            </View>
          ))}
          {webMode.kind === 'cloud' && authState === 'signed-in' && nextCursor && (
            <Pressable testID="load-older" accessibilityRole="button"
              accessibilityLabel={t('timeline.loadOlderLabel')}
              disabled={busy || loadingOlder}
              onPress={() => { void loadOlder(); }}
              style={[styles.secondaryButton, { backgroundColor: p.card2,
                marginTop: 12, opacity: busy || loadingOlder ? 0.5 : 1 }]}>
              <Text style={[styles.buttonText, heading]}>
                {loadingOlder ? t('common.loading') : t('timeline.loadOlder')}
              </Text>
            </Pressable>
          )}
          {!!olderError && authState === 'signed-in' && (
            <Text testID="older-error" accessibilityRole="alert"
              style={[styles.hint, { color: p.text, marginTop: 8 }]}>
              {olderError}
            </Text>
          )}
        </View>
      </View>
      <View style={styles.features}>
        {[
          { icon: '◈', title: t('feature.connect'), detail: t('feature.connectDetail'), state: webMode.kind === 'cloud' ? t('feature.active') : t('feature.onPrem') },
          { icon: '✧', title: t('feature.triage'), detail: t('feature.triageDetail'), state: webMode.kind === 'cloud' ? t('feature.active') : t('feature.onPrem') },
          { icon: '◇', title: t('feature.act'), detail: t('feature.actDetail'), state: t('feature.confirmed') }
        ].map(feature => <View key={feature.title} style={[styles.feature, { borderColor: p.border }]}>
          <Text style={{ color: p.accent, fontSize: 25 }}>{feature.icon}</Text>
          <View style={{ flex: 1 }}><Text style={[styles.featureTitle, heading]}>{feature.title}</Text><Text style={[styles.hint, { color: p.subtle }]}>{feature.detail}</Text></View>
          <Text style={[styles.featureState, { color: p.subtle }]}>{feature.state}</Text>
        </View>)}
      </View>
      </ScrollView>}
      <UpdateBanner updates={updates} theme={p} />
      <NotificationInvite push={push} signedIn={signedIn} otherPromptOpen={updates.decision === 'prompt'} theme={p} />
    </View>
  );
}

const styles = StyleSheet.create({
  app: { flex: 1 },
  fill: { flex: 1 },
  header: { height: 44, flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' },
  brand: { flexDirection: 'row', alignItems: 'center', gap: 7, paddingVertical: 6 },
  back: { fontSize: 22, lineHeight: 22, marginRight: 2 },
  glyph: { fontSize: 17, fontWeight: '700' },
  wordmark: { fontSize: 15, fontWeight: '700', letterSpacing: 1.5 },
  avatar: { width: 32, height: 32, borderRadius: 16, borderWidth: 1, alignItems: 'center', justifyContent: 'center' },
  avatarSpace: { width: 32, height: 32 },
  pill: { borderWidth: 1, borderRadius: 999, paddingHorizontal: 14, paddingVertical: 6 },
  pillText: { fontSize: 13, fontWeight: '700' },
  hidden: { display: 'none' },
  sheet: { paddingTop: 8 },
  page: { paddingTop: 8, paddingBottom: 44, width: '100%', maxWidth: 1120, alignSelf: 'center' },
  eyebrow: { fontSize: 10, letterSpacing: 2, fontWeight: '800' },
  hero: { paddingTop: 24, paddingBottom: 28, maxWidth: 790 },
  title: { fontSize: 48, lineHeight: 57, letterSpacing: -2.6, fontWeight: '800', paddingTop: 12 },
  description: { fontSize: 17, lineHeight: 26, maxWidth: 610, paddingTop: 16 },
  columns: { flexDirection: 'row', gap: 18, flexWrap: 'wrap' },
  card: { borderWidth: 1, borderRadius: 25, padding: 26, minHeight: 340 },
  primaryCard: { flex: 1.2, minWidth: 285 }, sideCard: { flex: 1, minWidth: 285 },
  kicker: { fontSize: 11, fontWeight: '800', letterSpacing: 1.2 },
  cardTitle: { fontSize: 25, fontWeight: '700', letterSpacing: -0.8, marginTop: 12, marginBottom: 18 },
  input: { minHeight: 96, borderWidth: 1, borderRadius: 14, padding: 13, textAlignVertical: 'top', fontSize: 15, lineHeight: 22 },
  mainButton: { marginTop: 12, minHeight: 48, alignItems: 'center', justifyContent: 'center', borderRadius: 13 },
  secondaryButton: { minHeight: 48, alignItems: 'center', justifyContent: 'center', borderRadius: 13 },
  buttonText: { fontSize: 15, fontWeight: '700' }, separator: { height: 1, marginVertical: 15 },
  hint: { fontSize: 12, lineHeight: 18, marginTop: 8 }, notice: { marginTop: 12, fontSize: 12, lineHeight: 18 },
  empty: { paddingTop: 17, alignItems: 'flex-start' }, emptyTitle: { fontSize: 18, fontWeight: '700', paddingTop: 12 },
  memo: { borderTopWidth: 1, paddingVertical: 12 },
  dayHeading: { fontSize: 14, fontWeight: '800', marginTop: 15, marginBottom: 4 },
  memoTime: { fontSize: 11, fontWeight: '700' }, memoText: { fontSize: 15, marginTop: 7, lineHeight: 22 },
  features: { marginTop: 20, flexDirection: 'row', flexWrap: 'wrap', gap: 12 },
  feature: { flex: 1, minWidth: 250, borderWidth: 1, borderRadius: 16, padding: 18, flexDirection: 'row', alignItems: 'center', gap: 12 },
  featureTitle: { fontSize: 16, fontWeight: '700' }, featureState: { fontSize: 9, fontWeight: '800' }
});
