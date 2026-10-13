// The overlay's side of Rambutan's local API. The URL pasted into OBS carries
// the overlay key in its fragment (#t=…), which the browser never sends to a
// server. It is read on every load and kept there: OBS reloads the URL it was
// given, so nothing is stored. The key only reads; it can't control playback.

/** The key from the URL's fragment, or null. */
export function readToken() {
  const token = new URLSearchParams(location.hash.slice(1)).get('t');
  return token && token.length > 0 ? token : null;
}

/** The host refused the key: it was reset, or never valid. */
export class Unauthorized extends Error {}

function headers(token) {
  return { Authorization: `Bearer ${token}` };
}

/** The playing track's cover as an object URL, or null. */
export async function artwork(token, videoId) {
  const response = await fetch(`/api/v1/overlay/artwork?v=${encodeURIComponent(videoId)}`, {
    headers: headers(token),
  });
  if (!response.ok) return null;
  return URL.createObjectURL(await response.blob());
}

/**
 * Follows a server-sent event stream (read with fetch because EventSource
 * can't send the Authorization header). Calls `onEvent(name, data)` for each
 * event and `onStatus` with 'connecting' | 'connected' | 'lost' | 'rejected';
 * reconnects after a loss, waiting longer each time up to 5 s (the overlay is
 * on stream, so it comes back quickly when Rambutan does).
 * Returns a function that stops following.
 */
export function follow(path, token, { onEvent, onStatus = () => {} }) {
  let stopped = false;
  let controller = null;
  let attempt = 0;
  // A stream silent past two heartbeats is dead, even if the socket hasn't noticed.
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
        const response = await fetch(path, {
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

        attempt = 0;
        onStatus('connected');
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
            let name = 'message';
            const data = [];
            for (const line of message.split('\n')) {
              if (line.startsWith('event: ')) name = line.slice(7);
              else if (line.startsWith('data: ')) data.push(line.slice(6));
            }
            if (data.length > 0) onEvent(name, JSON.parse(data.join('\n')));
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
      await new Promise((resolve) => setTimeout(resolve, Math.min(5_000, 250 * 2 ** Math.min(attempt, 5))));
    }
  }

  run();
  return () => {
    stopped = true;
    controller?.abort();
  };
}
