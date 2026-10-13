// The remote's side of Rambutan's local API. The pairing link carries the
// token in its fragment (#t=…), which the browser never sends to a server;
// it is kept in localStorage and sent as a Bearer header on every request.

const STORAGE_KEY = 'rambutan.remote.token';

/** Reads the token from the pairing link, if it carries one, and removes it
 *  from the address bar; otherwise the one kept from an earlier visit. */
export function loadToken() {
  const fragment = new URLSearchParams(location.hash.slice(1));
  const fromLink = fragment.get('t');
  if (fromLink) {
    try {
      localStorage.setItem(STORAGE_KEY, fromLink);
    } catch {
      // Private browsing may refuse storage; the token still works for this visit.
    }
    history.replaceState(null, '', location.pathname + location.search);
    return fromLink;
  }
  try {
    return localStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

export function forgetToken() {
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    // Nothing kept.
  }
}

/** The host refused the token: pairing was reset or never happened. */
export class Unauthorized extends Error {}

function headers(token, extra = {}) {
  return { Authorization: `Bearer ${token}`, ...extra };
}

/** The host answered with an error status. */
export class HttpError extends Error {
  constructor(status, kind) {
    super(`HTTP ${status}`);
    this.status = status;
    /** What the host said went wrong, when it says ("offline", "failed"). */
    this.kind = kind;
  }
}

async function post(token, path, body) {
  const response = await fetch(`/api/v1/${path}`, {
    method: 'POST',
    headers: headers(token, body ? { 'Content-Type': 'application/json' } : {}),
    body: body ? JSON.stringify(body) : undefined,
  });
  if (response.status === 401) throw new Unauthorized();
  if (!response.ok) throw new HttpError(response.status);
}

/** Sends a command; resolves when the host accepted it. */
export function send(token, action, body) {
  return post(token, `player/${action}`, body);
}

/** Sends a queue command (jump, remove, move, add); resolves when the host
 *  accepted it. The queue's new version arrives with the next state. */
export function sendQueue(token, action, body) {
  return post(token, `queue/${action}`, body);
}

async function getJson(token, path, signal) {
  const response = await fetch(`/api/v1/${path}`, { headers: headers(token), cache: 'no-store', signal });
  if (response.status === 401) throw new Unauthorized();
  if (!response.ok) {
    let kind;
    try {
      kind = (await response.json()).error;
    } catch {
      // No body that says more.
    }
    throw new HttpError(response.status, kind);
  }
  return response.json();
}

/** The queue around the current track: { version, currentIndex, total, items }. */
export function getQueue(token) {
  return getJson(token, 'queue');
}

/** Songs for a query: { results, more }, where `more` asks for the next page
 *  (searchMore) and is null on the last. Throws HttpError with kind "offline"
 *  when the computer can't reach YouTube Music. */
export function search(token, query, signal) {
  return getJson(token, `search?q=${encodeURIComponent(query)}`, signal);
}

/** The next page of a search, by its `more`; the same shape. A 404 HttpError
 *  means the host no longer keeps that page: search again. */
export function searchMore(token, more, signal) {
  return getJson(token, `search?more=${encodeURIComponent(more)}`, signal);
}

/** A cover as an object URL, or null: the playing track's, or a queued or
 *  found track's when its row says it has one. `row` asks for a small copy. */
export async function artwork(token, videoId, row = false) {
  const size = row ? '&size=row' : '';
  const response = await fetch(`/api/v1/artwork?v=${encodeURIComponent(videoId)}${size}`, { headers: headers(token) });
  if (!response.ok) return null;
  return URL.createObjectURL(await response.blob());
}

/**
 * Follows the host's live state (server-sent events, read with fetch because
 * EventSource can't send the Authorization header). Calls `onState` with each
 * state and `onStatus` with 'connecting' | 'connected' | 'lost' | 'rejected';
 * reconnects after a loss, waiting longer each time up to 10 s.
 * Returns a function that stops following.
 */
export function follow(token, { onState, onStatus }) {
  let stopped = false;
  let controller = null;
  let attempt = 0;
  // A stream that stays silent past two heartbeats is dead, even if the
  // socket hasn't noticed (a phone that slept, a network that changed).
  const silenceLimit = 35_000;

  async function run() {
    while (!stopped) {
      controller = new AbortController();
      let watchdog;
      const resetWatchdog = () => {
        clearTimeout(watchdog);
        watchdog = setTimeout(() => controller.abort(), silenceLimit);
      };
      try {
        onStatus(attempt === 0 ? 'connecting' : 'lost');
        resetWatchdog();
        const response = await fetch('/api/v1/events', {
          headers: headers(token),
          signal: controller.signal,
          cache: 'no-store',
        });
        if (response.status === 401) {
          clearTimeout(watchdog);
          onStatus('rejected');
          return;
        }
        if (!response.ok || !response.body) throw new Error(`HTTP ${response.status}`);

        const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
        let buffer = '';
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          resetWatchdog();
          buffer += value;
          let end;
          while ((end = buffer.indexOf('\n\n')) >= 0) {
            const message = buffer.slice(0, end);
            buffer = buffer.slice(end + 2);
            const data = message
              .split('\n')
              .filter((line) => line.startsWith('data: '))
              .map((line) => line.slice(6))
              .join('\n');
            if (data) {
              attempt = 0;
              onStatus('connected');
              onState(JSON.parse(data));
            }
          }
        }
      } catch {
        // Fall through to the retry below.
      } finally {
        clearTimeout(watchdog);
      }
      if (stopped) return;
      attempt += 1;
      onStatus('lost');
      await new Promise((resolve) => setTimeout(resolve, Math.min(10_000, 500 * 2 ** Math.min(attempt, 5))));
    }
  }

  run();
  return () => {
    stopped = true;
    controller?.abort();
  };
}
