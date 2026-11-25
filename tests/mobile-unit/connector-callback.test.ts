import { describe, expect, it, vi } from 'vitest';
import {
  CONNECTOR_CALLBACK_TYPE, isConnectorCallback, parseConnectorCallback, publishConnectorCallback,
  stateFromAuthorizationUrl, waitForConnectorCallback
} from '../../src/mobile/src/services/connectorCallback';

const state = 'Q2hhbmdlLW1lLXN0YXRlLXZhbHVlLTAxMjM0NTY3ODk';

describe('connector OAuth callback relay', () => {
  it('parses a bounded state with either a code or a sanitized error', () => {
    expect(parseConnectorCallback('?code=abc&state=' + state)).toEqual(
      { type: CONNECTOR_CALLBACK_TYPE, state, code: 'abc', error: null });
    expect(parseConnectorCallback('?error=access_denied<script>&state=' + state)?.error).toBe('access_deniedscript');
    expect(parseConnectorCallback('?code=abc')).toBeNull();
    expect(parseConnectorCallback('?code=abc&state=short')).toBeNull();
    expect(parseConnectorCallback('?state=' + state)).toBeNull();
    expect(parseConnectorCallback('?code=' + 'x'.repeat(4097) + '&state=' + state)).toBeNull();
  });

  it('extracts the API state only from an https authorization URL', () => {
    expect(stateFromAuthorizationUrl('https://login.microsoftonline.com/x/authorize?state=' + state)).toBe(state);
    expect(stateFromAuthorizationUrl('http://login.example.test/?state=' + state)).toBeNull();
    expect(stateFromAuthorizationUrl('not a url')).toBeNull();
  });

  it('accepts only the expected state from the same origin and cleans up', async () => {
    const target = new EventTarget();
    const channelTarget = Object.assign(new EventTarget(), { close: vi.fn() });
    const waiting = waitForConnectorCallback(state, {
      origin: 'https://tao.example.test', timeoutMs: 1000, target, channel: () => channelTarget
    });
    const callback = { type: CONNECTOR_CALLBACK_TYPE, state, code: 'c', error: null };
    target.dispatchEvent(new MessageEvent('message', { data: callback, origin: 'https://evil.example' }));
    channelTarget.dispatchEvent(new MessageEvent('message', { data: { ...callback, state: state + 'x' } }));
    target.dispatchEvent(new MessageEvent('message', { data: callback, origin: 'https://tao.example.test' }));
    await expect(waiting).resolves.toEqual(callback);
    expect(channelTarget.close).toHaveBeenCalledTimes(1);
    expect(isConnectorCallback({ type: CONNECTOR_CALLBACK_TYPE, state }, state)).toBe(false);
  });

  it('times out or aborts instead of waiting forever', async () => {
    const target = new EventTarget();
    await expect(waitForConnectorCallback(state, { origin: 'o', timeoutMs: 5, target, channel: () => null }))
      .rejects.toThrow('timeout');
    const abort = new AbortController();
    const waiting = waitForConnectorCallback(state, { origin: 'o', timeoutMs: 1000, target, channel: () => null, signal: abort.signal });
    abort.abort();
    await expect(waiting).rejects.toThrow('aborted');
  });

  it('publishes to the same-origin opener when it still exists', () => {
    const opener = { postMessage: vi.fn() };
    const callback = { type: CONNECTOR_CALLBACK_TYPE, state, code: 'c', error: null } as const;
    publishConnectorCallback(callback, 'https://tao.example.test', opener);
    expect(opener.postMessage).toHaveBeenCalledWith(callback, 'https://tao.example.test');
    expect(() => publishConnectorCallback(callback, 'https://tao.example.test',
      { postMessage: () => { throw new Error('gone'); } })).not.toThrow();
  });
});
