<script>
  import { onDestroy } from 'svelte';
  import { t, clock, locale } from './i18n.js';
  import { loadToken, forgetToken, follow, send, artwork, Unauthorized } from './api.js';
  import Icon from './Icon.svelte';
  import Queue from './Queue.svelte';
  import Search from './Search.svelte';

  // Icons: the desktop app's geometry (App/Styles/Icons.axaml).
  const icons = {
    previous: 'M6 6h2v12H6zm3.5 6l8.5 6V6z',
    play: 'M8 5v14l11-7z',
    pause: 'M6 19h4V5H6v14zm8-14v14h4V5h-4z',
    next: 'M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z',
    volume:
      'M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z',
    note: 'M12 3v10.55A4 4 0 1 0 14 17V7h4V3h-6z',
  };

  document.documentElement.lang = locale;
  document.title = t('app.title');

  const token = loadToken();

  /** 'unpaired' | 'connecting' | 'connected' | 'lost' | 'rejected' */
  let status = $state(token ? 'connecting' : 'unpaired');
  let player = $state(null);
  let receivedAt = 0;
  let now = $state(performance.now());
  let seeking = $state(null); // the position under the thumb while dragging
  let volumeDrag = $state(null);
  let notice = $state('');
  let noticeKind = $state('alert'); // 'alert' for failures, 'status' for confirmations
  let noticeTimer;
  /** 'player' | 'queue' | 'search' */
  let view = $state('player');
  const views = [
    { id: 'player', icon: 'note', label: 'nav.player' },
    { id: 'queue', icon: 'queue', label: 'nav.queue' },
    { id: 'search', icon: 'search', label: 'nav.search' },
  ];
  let coverUrl = $state(null);
  let coverFor = null;

  const stop = token
    ? follow(token, {
        onState: (state) => {
          player = state;
          receivedAt = performance.now();
          now = receivedAt;
          updateCover(state.track);
        },
        onStatus: (next) => {
          status = next;
          if (next === 'rejected') forgetToken();
        },
      })
    : () => {};

  // The phone moves the position itself while playing; the host sends a new
  // state whenever the real one leaves this estimate.
  const tick = setInterval(() => (now = performance.now()), 250);

  onDestroy(() => {
    stop();
    clearInterval(tick);
  });

  async function updateCover(track) {
    const key = track?.hasArtwork ? track.videoId : null;
    if (key === coverFor) return;
    coverFor = key;
    if (coverUrl) URL.revokeObjectURL(coverUrl);
    coverUrl = null;
    if (!key) return;
    try {
      const url = await artwork(token, key);
      if (coverFor === key) coverUrl = url;
      else if (url) URL.revokeObjectURL(url);
    } catch {
      // The placeholder stays.
    }
  }

  let connected = $derived(status === 'connected');
  let track = $derived(player?.track ?? null);
  let playing = $derived(player?.playback === 'playing');
  let duration = $derived(player?.durationMs ?? track?.durationMs ?? 0);
  let position = $derived(
    seeking ??
      Math.min(duration || Infinity, (player?.positionMs ?? 0) + (playing ? now - receivedAt : 0)),
  );
  let volume = $derived(volumeDrag ?? Math.round((player?.volume ?? 1) * 100));

  let stateLine = $derived.by(() => {
    if (!player) return '';
    if (player.waitingForNetwork) return t('playback.waitingForNetwork');
    switch (player.playback) {
      case 'loading':
        return t('playback.loading');
      case 'buffering':
        return t('playback.buffering');
      case 'paused':
        return t('playback.paused');
      case 'faulted':
        return t('playback.faulted');
      default:
        return '';
    }
  });

  function showNotice(text, kind = 'alert') {
    notice = text;
    noticeKind = kind;
    clearTimeout(noticeTimer);
    noticeTimer = setTimeout(() => (notice = ''), 4000);
  }

  function rejected() {
    status = 'rejected';
    forgetToken();
  }

  async function command(action, body) {
    try {
      await send(token, action, body);
    } catch (error) {
      if (error instanceof Unauthorized) {
        status = 'rejected';
        forgetToken();
      } else {
        showNotice(t('control.sendFailed'));
      }
    }
  }

  function commitSeek() {
    if (seeking === null) return;
    const target = seeking;
    command('seek', { positionMs: Math.round(target) });
    // Hold the thumb where it was let go until the host's new state arrives.
    player = { ...player, positionMs: target };
    receivedAt = performance.now();
    seeking = null;
  }

  let volumeTimer = null;
  function volumeInput(value) {
    volumeDrag = value;
    if (volumeTimer) return;
    volumeTimer = setTimeout(() => {
      volumeTimer = null;
      if (volumeDrag !== null) command('volume', { volume: volumeDrag / 100 });
    }, 120);
  }

  function volumeCommit(value) {
    clearTimeout(volumeTimer);
    volumeTimer = null;
    command('volume', { volume: value / 100 });
    player = { ...player, volume: value / 100 };
    volumeDrag = null;
  }
</script>

{#snippet icon(path, size = 24)}
  <svg viewBox="0 0 24 24" width={size} height={size} aria-hidden="true" focusable="false"><path d={path} /></svg>
{/snippet}

<main>
  {#if status === 'unpaired' || status === 'rejected'}
    <section class="message" aria-live="polite">
      <h1>{t(status === 'unpaired' ? 'pairing.unpaired' : 'pairing.rejected')}</h1>
      <p>{t(status === 'unpaired' ? 'pairing.unpairedHelp' : 'pairing.rejectedHelp')}</p>
    </section>
  {:else if !player}
    <section class="message" aria-live="polite">
      <p>{status === 'lost' ? t('connection.lost') : t('connection.connecting')}</p>
      {#if status === 'lost'}<p class="help">{t('connection.lostHelp')}</p>{/if}
    </section>
  {:else}
    {#if status === 'lost'}
      <div class="banner" role="status">
        <strong>{t('connection.lost')}</strong>
        <span>{t('connection.lostHelp')}</span>
      </div>
    {/if}

    <section class="player" class:stale={!connected} hidden={view !== 'player'} aria-label={t('nav.player')}>
      <div class="cover">
        {#if coverUrl}
          <img src={coverUrl} alt={t('artwork.of', { title: track?.title ?? '' })} />
        {:else}
          <div class="placeholder" role="img" aria-label={t('artwork.none')}>{@render icon(icons.note, 72)}</div>
        {/if}
      </div>

      <div class="identity" aria-live="polite">
        {#if track}
          <h1>{track.title}</h1>
          <p class="artist">{track.artists}{track.album ? ` • ${track.album}` : ''}</p>
        {:else}
          <h1>{t('playback.nothing')}</h1>
          <p class="artist">{t('playback.nothingHelp')}</p>
        {/if}
        <p class="state">{stateLine}</p>
      </div>

      <div class="seek">
        <input
          type="range"
          min="0"
          max={Math.max(1, duration)}
          step="1000"
          value={position}
          disabled={!connected || !track || !duration}
          aria-label={t('control.seek')}
          aria-valuetext={t('control.seekValue', { elapsed: clock(position), duration: clock(duration) })}
          oninput={(e) => (seeking = Number(e.currentTarget.value))}
          onchange={commitSeek}
        />
        <div class="times" aria-hidden="true">
          <span>{clock(position)}</span><span>{clock(duration)}</span>
        </div>
      </div>

      <div class="transport">
        <button disabled={!connected} aria-label={t('control.previous')} onclick={() => command('previous')}>
          {@render icon(icons.previous, 36)}
        </button>
        <button
          class="primary"
          disabled={!connected}
          aria-label={t(playing ? 'control.pause' : 'control.play')}
          onclick={() => command(playing ? 'pause' : 'play')}
        >
          {@render icon(playing ? icons.pause : icons.play, 40)}
        </button>
        <button disabled={!connected} aria-label={t('control.next')} onclick={() => command('next')}>
          {@render icon(icons.next, 36)}
        </button>
      </div>

      <div class="volume">
        {@render icon(icons.volume, 22)}
        <input
          type="range"
          min="0"
          max="100"
          step="1"
          value={volume}
          disabled={!connected}
          aria-label={t('control.volume')}
          aria-valuetext={t('control.volumeValue', { percent: volume })}
          oninput={(e) => volumeInput(Number(e.currentTarget.value))}
          onchange={(e) => volumeCommit(Number(e.currentTarget.value))}
        />
      </div>
    </section>

    <!-- Kept while hidden, so a search's results survive a look at the queue. -->
    <div class="panel" class:stale={!connected} hidden={view !== 'queue'}>
      <Queue
        {token}
        version={player.queueVersion ?? 0}
        active={view === 'queue'}
        {connected}
        onRejected={rejected}
        notify={showNotice}
      />
    </div>
    <div class="panel" hidden={view !== 'search'}>
      <Search {token} {connected} onRejected={rejected} notify={showNotice} />
    </div>

    <nav aria-label={t('nav.label')}>
      {#each views as v (v.id)}
        <button aria-current={view === v.id ? 'page' : undefined} onclick={() => (view = v.id)}>
          <Icon name={v.icon} size={22} />
          <span>{t(v.label)}</span>
        </button>
      {/each}
    </nav>
  {/if}

  {#if notice}
    <div class="notice" class:raised={!!player} role={noticeKind}>
      <span>{notice}</span>
      <button onclick={() => (notice = '')} aria-label={t('notice.dismiss')}>×</button>
    </div>
  {/if}
</main>

<style>
  :global(:root) {
    color-scheme: dark;
    --bg: #121212;
    --panel: #1e1e1e;
    --text: #ffffff;
    --muted: #b3b3b3;
    --accent: #ff4e45;
    --warn: #3a2a12;
    --line: #3a3a3a;
    --nav-height: 64px;
  }
  :global([hidden]) {
    display: none !important;
  }
  :global(.visually-hidden) {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip-path: inset(50%);
    white-space: nowrap;
  }
  :global(html, body) {
    margin: 0;
    background: var(--bg);
    color: var(--text);
    font-family: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif;
    -webkit-tap-highlight-color: transparent;
  }
  main {
    box-sizing: border-box;
    min-height: 100dvh;
    max-width: 480px;
    margin: 0 auto;
    padding: max(16px, env(safe-area-inset-top)) 16px max(16px, env(safe-area-inset-bottom));
    display: flex;
    flex-direction: column;
    gap: 16px;
  }
  .message {
    margin: auto 0;
    text-align: center;
  }
  .message h1 {
    font-size: 1.4rem;
  }
  .message p,
  .help {
    color: var(--muted);
    line-height: 1.5;
  }
  .banner {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 12px;
    border-radius: 10px;
    background: var(--warn);
    font-size: 0.9rem;
  }
  .banner span {
    color: var(--muted);
  }
  .player {
    display: flex;
    flex-direction: column;
    gap: 20px;
    transition: opacity 0.2s;
  }
  .player.stale {
    opacity: 0.55;
  }
  .cover {
    aspect-ratio: 1;
    width: 100%;
    border-radius: 12px;
    overflow: hidden;
    background: var(--panel);
  }
  .cover img {
    width: 100%;
    height: 100%;
    object-fit: cover;
    display: block;
  }
  .placeholder {
    width: 100%;
    height: 100%;
    display: grid;
    place-items: center;
    color: var(--muted);
  }
  .placeholder :global(svg) {
    fill: currentColor;
  }
  .identity h1 {
    margin: 0;
    font-size: 1.35rem;
    font-weight: 800;
    overflow-wrap: anywhere;
  }
  .identity p {
    margin: 4px 0 0;
  }
  .artist {
    color: var(--muted);
  }
  .state {
    min-height: 1.2em;
    color: var(--accent);
    font-size: 0.9rem;
  }
  input[type='range'] {
    width: 100%;
    accent-color: var(--accent);
    height: 32px;
    margin: 0;
  }
  .times {
    display: flex;
    justify-content: space-between;
    color: var(--muted);
    font-size: 0.8rem;
    font-variant-numeric: tabular-nums;
  }
  .transport {
    display: flex;
    justify-content: center;
    align-items: center;
    gap: 28px;
  }
  button {
    border: 0;
    background: none;
    color: var(--text);
    border-radius: 50%;
    width: 56px;
    height: 56px;
    display: grid;
    place-items: center;
    cursor: pointer;
  }
  button :global(svg) {
    fill: currentColor;
  }
  button.primary {
    width: 72px;
    height: 72px;
    background: var(--text);
    color: var(--bg);
  }
  button:disabled {
    opacity: 0.4;
    cursor: default;
  }
  button:focus-visible,
  input:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: 3px;
  }
  .volume {
    display: flex;
    align-items: center;
    gap: 12px;
    color: var(--muted);
  }
  .volume :global(svg) {
    fill: currentColor;
    flex: none;
  }
  .notice {
    position: fixed;
    left: 16px;
    right: 16px;
    bottom: max(16px, env(safe-area-inset-bottom));
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 8px 8px 8px 16px;
    border-radius: 10px;
    background: var(--panel);
    box-shadow: 0 4px 16px rgb(0 0 0 / 0.5);
  }
  .notice button {
    width: 40px;
    height: 40px;
    font-size: 1.4rem;
  }
  .notice.raised {
    bottom: calc(var(--nav-height) + max(16px, env(safe-area-inset-bottom)));
  }
  main:has(nav) {
    padding-bottom: calc(var(--nav-height) + max(16px, env(safe-area-inset-bottom)));
  }
  .panel {
    transition: opacity 0.2s;
  }
  .panel.stale {
    opacity: 0.55;
  }
  nav {
    position: fixed;
    left: 0;
    right: 0;
    bottom: 0;
    display: flex;
    justify-content: center;
    padding-bottom: env(safe-area-inset-bottom);
    background: var(--panel);
    border-top: 1px solid var(--line);
  }
  nav button {
    flex: 1;
    max-width: 160px;
    width: auto;
    height: var(--nav-height);
    border-radius: 0;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 2px;
    color: var(--muted);
    font: inherit;
    font-size: 0.75rem;
  }
  nav button[aria-current='page'] {
    color: var(--text);
    font-weight: 700;
  }
  nav button:focus-visible {
    outline-offset: -3px;
  }
  @media (prefers-reduced-motion: reduce) {
    .player,
    .panel {
      transition: none;
    }
  }
</style>
