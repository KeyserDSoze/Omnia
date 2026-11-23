import { useEffect, useRef, useState } from 'react';
import { useColorScheme, Platform, Pressable, ScrollView, Text, TextInput, View, StyleSheet } from 'react-native';
import { RecordingPresets, requestRecordingPermissionsAsync, setAudioModeAsync, useAudioRecorder } from 'expo-audio';
import { normalizeMemo } from '../domain/tags';
import { configuredWebMode, type WebMode } from '../services/config';
import { createBrowserAuth, type BrowserAuth } from '../services/webAuth';
import { MemoryApi, type ConsentDto, type RemoteMemory } from '../services/memoryApi';
import { CloudViewEpoch, appendUniqueById } from '../services/cloudViewEpoch';
import { ConsentLedgerEpoch, appendUniqueConsents } from '../services/consentLedgerView';

type LocalMemo = { id: string; content: string; tags: string[]; time: string };
const webMode: WebMode = Platform.OS === 'web' ? configuredWebMode() : { kind: 'demo' };
const activeManualGrant = (grants: ConsentDto[]) =>
  grants.find(item => item.source === 'manual' && item.purpose === 'memory.capture' && item.active) ?? null;
const timelineMemo = (item: RemoteMemory): LocalMemo => ({
  id: item.id, content: item.summary, tags: item.tags,
  time: new Date(item.occurredAt).toLocaleTimeString('it-IT', { hour: '2-digit', minute: '2-digit' })
});
type Palette = { bg: string; surface: string; text: string; subtle: string; border: string; accent: string; accentText: string; card2: string };
const palettes: Record<'dark' | 'light', Palette> = {
  dark: { bg: '#0D1421', surface: '#172234', text: '#F5F8FF', subtle: '#9BA9BB', border: '#29374A', accent: '#A5E6D4', accentText: '#092D2A', card2: '#203246' },
  light: { bg: '#F5F8FA', surface: '#FFFFFF', text: '#152537', subtle: '#53657B', border: '#DFE7EB', accent: '#176F68', accentText: '#FFFFFF', card2: '#E9F3F1' }
};

export default function Home() {
  const device = useColorScheme();
  const [override, setOverride] = useState<'light' | 'dark' | null>(null);
  const mode = override ?? (device === 'dark' ? 'dark' : 'light');
  const p = palettes[mode];
  const [draft, setDraft] = useState('');
  const [memos, setMemos] = useState<LocalMemo[]>([]);
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
    setGrant(null);
    setAccepted(false);
    setErasePhrase('');
    setEraseAcknowledged(false);
    setDraft('');
    retryRef.current = null;
    // Release a pending mutation's disabled controls after API 401/logout.
    setBusy(false);
  }

  function sameSession(epoch: number): boolean {
    return sessionEpochRef.current === epoch;
  }

  async function refreshCloud(): Promise<boolean> {
    const api = apiRef.current;
    if (!api) throw new Error('Connessione API non disponibile.');
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
        setOlderError('Impossibile caricare le note precedenti. Riprova.');
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
        setLedgerError('Registro non disponibile. Riprova; nessuna revoca è stata eseguita.');
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
        setLedgerError('Pagina dei consensi non caricata. Riprova.');
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
      setNotice('Consenso revocato per ' + item.source +
        '. I dati precedenti non sono cancellati. Aggiorna il registro.');
      try {
        await refreshCloud();
      } catch {
        if (sameSession(epoch))
          setNotice('Revoca confermata per ' + item.source +
            ', ma aggiornamento non disponibile. Aggiorna timeline e registro.');
      }
    } catch {
      if (sameSession(epoch))
        setNotice('Revoca non confermata: verifica il registro prima di riprovare.');
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
        setNotice('Sessione non valida. Accedi di nuovo.');
        void auth.discardLocalSession().catch(() => {
          if (sameSession(epoch))
            setNotice('Sessione non valida. Chiudi la scheda e accedi di nuovo.');
        });
      }
    });
    void (async () => {
      try {
        await auth.handleRedirect();
        if (!live || !sameSession(bootEpoch)) return;
        if (!await auth.hasSession()) {
          if (live && sameSession(bootEpoch)) setAuthState('signed-out');
          return;
        }
        if (live && sameSession(bootEpoch)) {
          setAuthState('signed-in');
          await refreshCloud();
        }
      } catch (error) {
        if (live && sameSession(bootEpoch)) {
          setAuthState('error');
          setNotice(error instanceof Error ? error.message : 'Autenticazione non riuscita.');
        }
      }
    })();
    return () => {
      live = false;
      sessionEpochRef.current++;
      cloudViewRef.current.invalidate();
    };
  }, []);

  async function login() {
    if (busy) return;
    try { await authRef.current?.login(); }
    catch { setNotice('Impossibile avviare il login OIDC. Verifica il provider.'); }
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
      const uncertainty = hadPendingRequest
        ? ' Una richiesta già inviata potrebbe essere stata elaborata dal server. Verifica i dati al prossimo accesso.'
        : '';
      setNotice((revoked ? 'Sessione locale chiusa e revoca token richiesta.' :
        'Sessione locale chiusa; revoca remota non confermata. Chiudi anche la sessione del provider.') +
        uncertainty);
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
      setNotice('Autorizzazione attiva per le tue note manuali (30 giorni).');
    } catch (error) {
      if (sameSession(epoch))
        setNotice(error instanceof Error ? error.message : 'Consenso non salvato.');
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
      setNotice('Consenso revocato: niente nuove note. I dati precedenti restano finché non saranno cancellati.');
    } catch (error) {
      if (sameSession(epoch))
        setNotice(error instanceof Error ? error.message : 'Impossibile revocare il consenso.');
    } finally { if (sameSession(epoch)) setBusy(false); }
  }

  async function exportPersonalData() {
    if (webMode.kind !== 'cloud' || authState !== 'signed-in' || !apiRef.current || busy) return;
    const epoch = sessionEpochRef.current;
    setBusy(true);
    try {
      const archive = await apiRef.current.exportPersonalData();
      if (!sameSession(epoch)) return; // never download an old session's archive
      if (archive.size === 0) throw new Error('Il server ha restituito un file vuoto.');
      const objectUrl = URL.createObjectURL(archive);
      try {
        const element = document.createElement('a');
        element.href = objectUrl;
        element.download = 'omnia-personal-data.json';
        document.body.appendChild(element);
        element.click();
        element.remove();
      } finally {
        // Browser downloads may still be initializing immediately after click.
        setTimeout(() => URL.revokeObjectURL(objectUrl), 2000);
      }
      setNotice('Esportazione JSON avviata. Contiene dati riservati: conserva il file al sicuro.');
    } catch (error) {
      if (sameSession(epoch))
        setNotice(error instanceof Error ? error.message : 'Esportazione non riuscita.');
    } finally { if (sameSession(epoch)) setBusy(false); }
  }

  async function erasePersonalData() {
    if (webMode.kind !== 'cloud' || authState !== 'signed-in' || !apiRef.current ||
        busy || erasePhrase !== 'ELIMINA' || !eraseAcknowledged) return;
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
      setNextCursor(null);
      setOlderError('');
      setDraft('');
      retryRef.current = null;
      setAccepted(false);
      setErasePhrase('');
      setEraseAcknowledged(false);
      setNotice('Cancellazione sul database principale confermata: ' +
        receipt.deletedMemories + ' note e ' + receipt.deletedConsents +
        ' consensi. Backup e sistemi esterni non inclusi.');
    } catch (error) {
      if (sameSession(epoch))
        setNotice(error instanceof Error ? error.message : 'Cancellazione non confermata. Riprova.');
    } finally { if (sameSession(epoch)) setBusy(false); }
  }

  async function captureText() {
    if (busy) return;
    try {
      const normalized = normalizeMemo(draft, new Date().toISOString(), webMode.kind === 'cloud');
      if (webMode.kind !== 'cloud') {
        setMemos(current => [{
          id: normalized.externalId, content: normalized.content, tags: normalized.tags,
          time: new Date().toLocaleTimeString('it-IT', { hour: '2-digit', minute: '2-digit' })
        }, ...current]);
        setDraft('');
        setNotice('Memo aggiunto a questa sessione. La sincronizzazione richiederà un accesso sicuro.');
        return;
      }
      if (authState !== 'signed-in') {
        setNotice('Accedi con OIDC prima di salvare nel cloud.');
        return;
      }
      if (!grant) {
        setNotice('Autorizza prima il salvataggio delle note manuali.');
        return;
      }
      const api = apiRef.current;
      if (!api) throw new Error('API non inizializzata.');
      const epoch = sessionEpochRef.current;
      const entry = retryRef.current?.raw === normalized.content ? retryRef.current.entry : normalized;
      retryRef.current = { raw: normalized.content, entry };
      setBusy(true);
      try {
        await api.save(entry);
        if (!sameSession(epoch)) return;
        retryRef.current = null;
        setDraft('');
        setNotice('Nota salvata nel cloud.');
        try { await refreshCloud(); }
        catch {
          if (sameSession(epoch))
            setNotice('Nota salvata, ma la timeline non è aggiornata. Usa Aggiorna.');
        }
      } catch (error) {
        if (sameSession(epoch))
          setNotice(error instanceof Error ? error.message : 'Salvataggio non confermato. Riprova.');
      } finally { if (sameSession(epoch)) setBusy(false); }
    } catch (error) {
      setNotice(error instanceof Error ? error.message : 'Nota non valida.');
    }
  }

  async function captureAudio() {
    try {
      if (recording) {
        await recorder.stop();
        setRecording(false);
        setAudioUri(recorder.uri ?? null);
        setNotice('Registrazione completata sul dispositivo. Nessun audio inviato al server.');
        return;
      }
      const { granted } = await requestRecordingPermissionsAsync();
      if (!granted) { setNotice('Permesso microfono non concesso.'); return; }
      await setAudioModeAsync({ allowsRecording: true, playsInSilentMode: true });
      await recorder.prepareToRecordAsync();
      recorder.record();
      setAudioUri(null);
      setRecording(true);
      setNotice('Registrazione microfono in corso. Premi di nuovo per fermare.');
    } catch {
      setRecording(false);
      setNotice('Registrazione non disponibile su questo dispositivo/browser.');
    }
  }

  const cardStyle = { backgroundColor: p.surface, borderColor: p.border };
  const heading = { color: p.text };
  return (
    <ScrollView style={{ flex: 1, backgroundColor: p.bg }} contentContainerStyle={styles.page}>
      <View style={styles.top}>
        <View style={styles.brand}>
          <View style={[styles.mark, { backgroundColor: p.accent }]}><Text style={{ color: p.accentText, fontWeight: '900', fontSize: 19 }}>O</Text></View>
          <View><Text style={[styles.brandName, heading]}>omnia</Text><Text style={[styles.eyebrow, { color: p.subtle }]}>PERSONAL INTELLIGENCE</Text></View>
        </View>
        <Pressable testID="theme-toggle" accessibilityRole="button" accessibilityLabel="Cambia tema" onPress={() => setOverride(mode === 'dark' ? 'light' : 'dark')} style={[styles.themeButton, { borderColor: p.border, backgroundColor: p.surface }]}>
          <Text style={{ color: p.text }}>{mode === 'dark' ? '☀ Tema chiaro' : '☾ Tema scuro'}</Text>
        </Pressable>
      </View>
      <View style={styles.hero}>
        <Text style={[styles.eyebrow, { color: p.accent }]}>IL TUO SPAZIO PERSONALE</Text>
        <Text style={[styles.title, heading]}>La tua giornata.{"\n"}Più chiara, ogni giorno.</Text>
        <Text style={[styles.description, { color: p.subtle }]}>Un posto per raccogliere i tuoi momenti, collegare i puntini e costruire una memoria che controlli tu.</Text>
      </View>
      <View style={[styles.card, cardStyle, { minHeight: 0, marginBottom: 20 }]}>
        <Text style={[styles.kicker, { color: p.subtle }]}>ACCESSO E DATI</Text>
        <Text testID="auth-status" style={[styles.hint, heading]}>
          {webMode.kind === 'demo' ? 'Modalità demo locale: nessun dato sincronizzato.' :
            webMode.kind === 'invalid' ? webMode.reason :
            authState === 'checking' ? 'Verifica della sessione OIDC…' :
            authState === 'signed-in' ? 'Sessione autenticata. Le note sono lette dal server.' :
            'Accesso necessario per memorizzare le note nel cloud.'}
        </Text>
        {webMode.kind === 'cloud' && authState !== 'checking' && (
          <View style={{ marginTop: 12, flexDirection: 'row', flexWrap: 'wrap', gap: 10 }}>
            {authState !== 'signed-in' ? (
              <Pressable testID="auth-login" onPress={() => { void login(); }}
                disabled={busy} accessibilityRole="button" style={[styles.secondaryButton, { backgroundColor: p.card2, paddingHorizontal: 16 }]}>
                <Text style={[styles.buttonText, heading]}>Accedi con OIDC</Text>
              </Pressable>
            ) : (
              <>
                <Pressable testID="sync-reload" onPress={() => {
                  if (busy) return;
                  const epoch = sessionEpochRef.current;
                  setBusy(true);
                  void refreshCloud().catch(() => {
                    if (sameSession(epoch))
                      setNotice('Impossibile aggiornare la timeline. Riprova.');
                  }).finally(() => {
                    if (sameSession(epoch)) setBusy(false);
                  });
                }} disabled={busy}
                  accessibilityRole="button" style={[styles.secondaryButton, { backgroundColor: p.card2, paddingHorizontal: 16 }]}>
                  <Text style={[styles.buttonText, heading]}>Aggiorna timeline</Text>
                </Pressable>
                <Pressable testID="auth-logout" onPress={() => { void logout(); }}
                  accessibilityRole="button" style={[styles.secondaryButton, { backgroundColor: p.card2, paddingHorizontal: 16 }]}>
                  <Text style={[styles.buttonText, heading]}>Esci</Text>
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
                  Autorizzazione attiva: note manuali fino al {new Date(grant.expiresAt).toLocaleDateString('it-IT')}.
                </Text>
                <Pressable testID="revoke-consent" accessibilityRole="button"
                  onPress={() => { void revokeConsent(); }} disabled={busy}
                  style={[styles.secondaryButton, { backgroundColor: p.card2, paddingHorizontal: 16, marginTop: 9, alignSelf: 'flex-start' }]}>
                  <Text style={[styles.buttonText, heading]}>Revoca autorizzazione</Text>
                </Pressable>
              </>
            ) : (
              <>
                <Text testID="consent-state" style={[styles.hint, heading]}>Nessun consenso attivo per le note manuali.</Text>
                <Pressable testID="consent-accept" accessibilityRole="checkbox"
                  accessibilityState={{ checked: accepted }} onPress={() => setAccepted(!accepted)}
                  style={{ marginTop: 12, flexDirection: 'row', alignItems: 'flex-start', gap: 9 }}>
                  <Text style={heading}>{accepted ? '☑' : '☐'}</Text>
                  <Text style={[styles.hint, heading, { flex: 1, marginTop: 0 }]}>
                    Autorizzo per 30 giorni la conservazione e l'elaborazione delle mie note manuali.
                    La revoca blocca i nuovi salvataggi, non cancella quelli precedenti.
                    Gli eventuali modelli AI configurati sul server possono elaborare il testo.
                    Non caricherò contenuti di altre persone senza averne il diritto.
                  </Text>
                </Pressable>
                <Pressable testID="grant-consent" accessibilityRole="button"
                  disabled={!accepted || busy} onPress={() => { void grantConsent(); }}
                  style={[styles.secondaryButton, { backgroundColor: p.card2,
                    paddingHorizontal: 16, marginTop: 11, opacity: accepted && !busy ? 1 : 0.5, alignSelf: 'flex-start' }]}>
                  <Text style={[styles.buttonText, heading]}>Attiva salvataggio note</Text>
                </Pressable>
              </>
            )}
          </View>
        )}
        {webMode.kind === 'cloud' && authState === 'signed-in' && (
          <View testID="consent-ledger" style={{ marginTop: 18, borderTopWidth: 1,
            borderTopColor: p.border, paddingTop: 15 }}>
            <Text style={[styles.kicker, { color: p.subtle }]}>REGISTRO DEI CONSENSI</Text>
            <Text style={[styles.hint, heading]}>
              Storico delle autorizzazioni first-party per sorgente. Ogni pagina
              mostra al massimo 25 record, anche scaduti o revocati.
              Usa Carica altri consensi per vedere il resto dello storico.
              Revocare blocca nuovi salvataggi, non cancella dati già conservati.
            </Text>
            <Pressable testID="consent-ledger-open" accessibilityRole="button"
              disabled={busy || ledgerLoading}
              onPress={() => { void openConsentLedger(); }}
              style={[styles.secondaryButton, { backgroundColor: p.card2,
                paddingHorizontal: 16, marginTop: 11, alignSelf: 'flex-start',
                opacity: busy || ledgerLoading ? 0.5 : 1 }]}>
              <Text style={[styles.buttonText, heading]}>
                {ledgerVisible ? 'Aggiorna registro consensi' : 'Consulta registro consensi'}
              </Text>
            </Pressable>
            {ledgerVisible && (
              <View style={{ marginTop: 11 }}>
                <Text testID="consent-ledger-count" style={[styles.hint, heading]}>
                  {ledgerLoaded
                    ? 'Visualizzati ' + ledgerRows.length + ' consensi. ' +
                      (ledgerCursor ? 'Altre pagine disponibili.' : 'Fine dello storico.')
                    : ledgerLoading ? 'Caricamento registro…' :
                      'Registro da aggiornare. Premi Aggiorna registro consensi.'}
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
                        ? 'Attivo fino al ' + new Date(item.expiresAt).toLocaleDateString('it-IT')
                        : item.revokedAt
                          ? 'Revocato il ' + new Date(item.revokedAt).toLocaleDateString('it-IT')
                          : 'Scaduto il ' + new Date(item.expiresAt).toLocaleDateString('it-IT')}
                      {' · policy ' + item.policyVersion}
                    </Text>
                    {item.active && (
                      <Pressable testID="consent-ledger-revoke" accessibilityRole="button"
                        accessibilityLabel={'Revoca consenso per ' + item.source}
                        disabled={busy || ledgerLoading}
                        onPress={() => { void revokeSourceConsent(item); }}
                        style={[styles.secondaryButton, { backgroundColor: p.card2,
                          paddingHorizontal: 12, marginTop: 8, alignSelf: 'flex-start',
                          opacity: busy || ledgerLoading ? 0.5 : 1 }]}>
                        <Text style={[styles.buttonText, heading]}>
                          {'Revoca ' + item.source}
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
                      {ledgerLoading ? 'Caricamento…' : 'Carica altri consensi'}
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
            <Text style={[styles.kicker, { color: p.subtle }]}>I TUOI DATI</Text>
            <Text style={[styles.hint, heading]}>
              Puoi esportare in JSON note, originali, dati elaborati e consensi dal database principale.
              Il file contiene dati personali: custodiscilo in un luogo sicuro.
              Le note scadono 30 giorni dopo la ricezione nel database; la pulizia
              automatica avviene periodicamente e non riguarda backup o servizi esterni.
            </Text>
            <Pressable testID="export-data" accessibilityRole="button"
              disabled={busy} onPress={() => { void exportPersonalData(); }}
              style={[styles.secondaryButton, { backgroundColor: p.card2,
                paddingHorizontal: 16, alignSelf: 'flex-start', marginTop: 12 }]}>
              <Text style={[styles.buttonText, heading]}>Esporta i miei dati (JSON)</Text>
            </Pressable>
            <Text style={[styles.hint, { color: p.subtle, marginTop: 19 }]}>
              La cancellazione elimina definitivamente note, embedding e cronologia
              dei consensi dal database PostgreSQL principale. Non elimina backup,
              copie dei provider AI o account presso il gestore dell'identità.
            </Text>
            <Pressable testID="erase-acknowledge" accessibilityRole="checkbox"
              accessibilityState={{ checked: eraseAcknowledged }}
              onPress={() => setEraseAcknowledged(value => !value)}
              style={{ flexDirection: 'row', marginTop: 12, gap: 9 }}>
              <Text style={heading}>{eraseAcknowledged ? '☑' : '☐'}</Text>
              <Text style={[styles.hint, heading, { flex: 1, marginTop: 0 }]}>
                Comprendo che la cancellazione sul database principale è irreversibile
                e che i backup o servizi esterni richiedono procedure separate.
              </Text>
            </Pressable>
            <TextInput testID="erase-confirmation" accessibilityLabel="Digita ELIMINA per confermare"
              placeholder="Digita ELIMINA per confermare"
              placeholderTextColor={p.subtle} value={erasePhrase}
              onChangeText={setErasePhrase}
              style={[styles.input, { minHeight: 42, marginTop: 9, color: p.text,
                backgroundColor: p.bg, borderColor: p.border }]} />
            <Pressable testID="erase-data" accessibilityRole="button"
              disabled={busy || !eraseAcknowledged || erasePhrase !== 'ELIMINA'}
              onPress={() => { void erasePersonalData(); }}
              style={[styles.secondaryButton, { backgroundColor: p.card2,
                alignSelf: 'flex-start', paddingHorizontal: 16, marginTop: 9,
                opacity: !busy && eraseAcknowledged && erasePhrase === 'ELIMINA' ? 1 : 0.45 }]}>
              <Text style={[styles.buttonText, heading]}>Cancella tutti i miei dati dal database</Text>
            </Pressable>
          </View>
        )}
      </View>
      <View style={styles.columns}>
        <View style={[styles.card, cardStyle, styles.primaryCard]}>
          <Text style={[styles.kicker, { color: p.subtle }]}>CATTURA UN MOMENTO</Text>
          <Text style={[styles.cardTitle, heading]}>Cosa vuoi ricordare?</Text>
          <TextInput testID="memo-input" placeholder="Una riunione, un pensiero, un'intuizione…" placeholderTextColor={p.subtle} multiline value={draft} onChangeText={value => {
            setDraft(value);
            retryRef.current = null;
          }} style={[styles.input, { color: p.text, backgroundColor: p.bg, borderColor: p.border }]} />
          <Pressable testID="save-memo" onPress={() => { void captureText(); }} disabled={busy}
            accessibilityRole="button" style={[styles.mainButton, { backgroundColor: p.accent }]}>
            <Text style={[styles.buttonText, { color: p.accentText }]}>＋ Salva nota rapida</Text>
          </Pressable>
          <View style={[styles.separator, { backgroundColor: p.border }]} />
          <Pressable testID="record-audio" onPress={() => { void captureAudio(); }} accessibilityRole="button" style={[styles.secondaryButton, { backgroundColor: p.card2 }]}>
            <Text style={[styles.buttonText, heading]}>{recording ? '■ Ferma registrazione' : '◉ Registra memo vocale'}</Text>
          </Pressable>
          <Text style={[styles.hint, { color: p.subtle }]}>Registra il microfono con consenso esplicito. Le chiamate telefoniche non vengono intercettate.</Text>
          {audioUri && <Text testID="audio-result" style={[styles.hint, heading]}>Memo audio creato sul dispositivo.</Text>}
          {!!notice && <Text accessibilityRole="alert" testID="notice" style={[styles.notice, { color: p.text }]}>{notice}</Text>}
        </View>
        <View testID="memory-timeline" style={[styles.card, cardStyle, styles.sideCard]}>
          <Text style={[styles.kicker, { color: p.subtle }]}>LA TUA MEMORIA</Text>
          <Text style={[styles.cardTitle, heading]}>I tuoi momenti</Text>
          {memos.length === 0 ? (
            <View style={styles.empty}>
              <Text style={{ fontSize: 35 }}>✦</Text>
              <Text style={[styles.emptyTitle, heading]}>Si comincia da qui.</Text>
              <Text style={[styles.hint, { color: p.subtle }]}>{webMode.kind === 'cloud' ? 'Le note salvate nel cloud appariranno qui dopo l’accesso.' : 'Le note di questa sessione appariranno qui. Non sono ancora sincronizzate sul cloud.'}</Text>
            </View>
          ) : memos.map(memo => <View key={memo.id} style={[styles.memo, { borderColor: p.border }]}>
            <Text style={[styles.memoTime, { color: p.subtle }]}>{memo.time} · {memo.tags.join(', ')}</Text>
            <Text style={[styles.memoText, heading]}>{memo.content}</Text>
          </View>)}
          {webMode.kind === 'cloud' && authState === 'signed-in' && nextCursor && (
            <Pressable testID="load-older" accessibilityRole="button"
              accessibilityLabel="Carica note precedenti"
              disabled={busy || loadingOlder}
              onPress={() => { void loadOlder(); }}
              style={[styles.secondaryButton, { backgroundColor: p.card2,
                marginTop: 12, opacity: busy || loadingOlder ? 0.5 : 1 }]}>
              <Text style={[styles.buttonText, heading]}>
                {loadingOlder ? 'Caricamento…' : 'Carica altre note'}
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
          { icon: '◈', title: 'Connetti', detail: 'Le tue app, i tuoi device e i tuoi servizi', state: 'IN ROADMAP' },
          { icon: '✧', title: 'Comprendi', detail: 'Relazioni, thread e ricerca semantica', state: 'IN ROADMAP' },
          { icon: '◇', title: 'Agisci', detail: 'Suggerimenti e azioni con il tuo consenso', state: 'IN ROADMAP' }
        ].map(feature => <View key={feature.title} style={[styles.feature, { borderColor: p.border }]}>
          <Text style={{ color: p.accent, fontSize: 25 }}>{feature.icon}</Text>
          <View style={{ flex: 1 }}><Text style={[styles.featureTitle, heading]}>{feature.title}</Text><Text style={[styles.hint, { color: p.subtle }]}>{feature.detail}</Text></View>
          <Text style={[styles.featureState, { color: p.subtle }]}>{feature.state}</Text>
        </View>)}
      </View>
      <Text style={[styles.footer, { color: p.subtle }]}>OMNIA · FOUNDATION 0.2 · PRIVACY BY DESIGN · NESSUNA AUTOMAZIONE ATTIVA</Text>
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  page: { paddingHorizontal: 22, paddingTop: 32, paddingBottom: 44, width: '100%', maxWidth: 1120, alignSelf: 'center' },
  top: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 16 },
  brand: { flexDirection: 'row', alignItems: 'center', gap: 12 }, mark: { width: 43, height: 43, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  brandName: { fontSize: 27, fontWeight: '800', letterSpacing: -1.6 }, eyebrow: { fontSize: 10, letterSpacing: 2, fontWeight: '800' },
  themeButton: { borderWidth: 1, borderRadius: 13, paddingHorizontal: 14, paddingVertical: 10 },
  hero: { paddingTop: 64, paddingBottom: 36, maxWidth: 790 },
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
  memo: { borderTopWidth: 1, paddingVertical: 12 }, memoTime: { fontSize: 11, fontWeight: '700' }, memoText: { fontSize: 15, marginTop: 7, lineHeight: 22 },
  features: { marginTop: 20, flexDirection: 'row', flexWrap: 'wrap', gap: 12 },
  feature: { flex: 1, minWidth: 250, borderWidth: 1, borderRadius: 16, padding: 18, flexDirection: 'row', alignItems: 'center', gap: 12 },
  featureTitle: { fontSize: 16, fontWeight: '700' }, featureState: { fontSize: 9, fontWeight: '800' },
  footer: { fontSize: 10, letterSpacing: 1.5, textAlign: 'center', marginTop: 44 }
});
