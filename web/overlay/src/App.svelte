<script>
  import { onDestroy } from 'svelte';
  import { t, clock, locale } from './i18n.js';
  import { readToken, follow, artwork } from './api.js';
  import Visualizer from './Visualizer.svelte';

  // The app's note glyph (App/Styles/Icons.axaml), for a track with no cover.
  const notePath = 'M12 3v10.55A4 4 0 1 0 14 17V7h4V3h-6z';

  document.documentElement.lang = locale;
  document.title = t('app.title');

  const token = readToken();

  /** 'missing' | 'connecting' | 'connected' | 'lost' | 'rejected' */
  let status = $state(token ? 'connecting' : 'missing');
  // A restart of Rambutan or of the overlay drops the stream for a moment; the
  // last state stays up that long before the overlay says the connection is lost.
  let lostLongEnough = $state(false);
  let lostTimer;
  let player = $state(null);
  let style = $state(null);
  let receivedAt = 0;
  let now = $state(performance.now());
  let coverUrl = $state(null);
  let coverFor = null;

  // The viewer's system may ask for reduced motion: no bars, no fades.
  const motionQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
  let reduceMotion = $state(motionQuery.matches);
  const onMotionChange = (e) => (reduceMotion = e.matches);
  motionQuery.addEventListener('change', onMotionChange);

  const stop = token
    ? follow('/api/v1/overlay/events', token, {
        onEvent: (name, data) => {
          if (name === 'config') {
            style = data;
          } else if (name === 'state') {
            player = data;
            receivedAt = performance.now();
            now = receivedAt;
            updateCover(data.track);
          }
        },
        onStatus: (next) => {
          status = next;
          clearTimeout(lostTimer);
          if (next === 'lost') lostTimer = setTimeout(() => (lostLongEnough = true), 3000);
          else lostLongEnough = false;
        },
      })
    : () => {};

  // The page advances the position itself while playing; the host sends a new
  // state whenever the real one leaves this estimate.
  const tick = setInterval(() => (now = performance.now()), 250);

  onDestroy(() => {
    stop();
    clearInterval(tick);
    clearTimeout(lostTimer);
    motionQuery.removeEventListener('change', onMotionChange);
  });

  async function updateCover(track) {
    const key = track?.hasArtwork ? track.videoId : null;
    if (key === coverFor) return;
    coverFor = key;
    const previous = coverUrl;
    const next = key ? await artwork(token, key).catch(() => null) : null;
    if (coverFor !== key) {
      if (next) URL.revokeObjectURL(next);
      return;
    }
    coverUrl = next;
    if (previous) URL.revokeObjectURL(previous);
  }

  const track = $derived(player?.track ?? null);
  const playing = $derived(player?.playback === 'playing');
  const duration = $derived(player?.durationMs ?? track?.durationMs ?? null);
  const position = $derived.by(() => {
    if (!player) return 0;
    const advanced = player.positionMs + (playing ? now - receivedAt : 0);
    return duration ? Math.min(advanced, duration) : advanced;
  });
  const fraction = $derived(duration ? Math.max(0, Math.min(1, position / duration)) : 0);

  const note = $derived.by(() => {
    if (!player) return '';
    if (player.waitingForNetwork) return t('playback.waitingForNetwork');
    switch (player.playback) {
      case 'paused':
        return t('playback.paused');
      case 'loading':
        return t('playback.loading');
      case 'buffering':
        return t('playback.buffering');
      case 'faulted':
        return t('playback.faulted');
      default:
        return '';
    }
  });

  const show = $derived(style?.show ?? {});
  const scale = $derived(style?.scale ?? 1);
  const showBars = $derived(!!show.visualizer && !reduceMotion && !!track);
  // What the card shows: a problem the streamer must fix, the track, or idle.
  const view = $derived.by(() => {
    if (status === 'missing') return 'missing';
    if (status === 'rejected') return 'rejected';
    if (status === 'lost' && lostLongEnough) return 'lost';
    if (!style || !player) return status === 'lost' ? 'lost' : 'connecting';
    if (track) return 'track';
    return style.whenIdle === 'hide' ? 'hidden' : 'idle';
  });
</script>

<main
  class:right={style?.align === 'right'}
  class:still={reduceMotion}
  style:--text={style?.colors?.text ?? '#FFFFFF'}
  style:--secondary={style?.colors?.secondaryText ?? '#FFFFFFB3'}
  style:--accent={style?.colors?.accent ?? '#FF4E45'}
  style:--background={style?.colors?.background ?? '#000000A6'}
  style:--radius="{(style?.cornerRadius ?? 12) * scale}px"
  style:--scale={scale}
>
  {#if view === 'track'}
    {#key track.videoId}
      <section class="card" aria-live="polite">
        <div class="row">
          {#if show.artwork}
            {#if coverUrl}
              <img class="cover" src={coverUrl} alt={t('artwork.of', { title: track.title })} />
            {:else}
              <div class="cover none" role="img" aria-label={t('artwork.none')}>
                <svg viewBox="0 0 24 24" aria-hidden="true"><path d={notePath} /></svg>
              </div>
            {/if}
          {/if}
          <div class="text">
            <div class="title">{track.title}</div>
            <div class="secondary">{track.artists}</div>
            {#if show.album && track.album}
              <div class="secondary album">{track.album}</div>
            {/if}
            {#if note}
              <div class="note">{note}</div>
            {/if}
          </div>
        </div>
        {#if showBars}
          <Visualizer
            {token}
            bars={style.visualizer?.bars ?? 32}
            peaks={style.visualizer?.peaks ?? true}
            accent={style.colors.accent}
            peakColor={style.colors.secondaryText}
            height={Math.round(32 * scale)}
          />
        {/if}
        {#if show.progress && duration}
          <div
            class="progress"
            role="progressbar"
            aria-valuemin="0"
            aria-valuemax="100"
            aria-valuenow={Math.round(fraction * 100)}
            aria-valuetext={t('playback.progress', { elapsed: clock(position), duration: clock(duration) })}
          >
            <div class="fill" style:width="{fraction * 100}%"></div>
          </div>
          {#if show.times}
            <div class="times">
              <span>{clock(position)}</span>
              <span>{clock(duration)}</span>
            </div>
          {/if}
        {/if}
      </section>
    {/key}
  {:else if view === 'idle'}
    <section class="card quiet">
      <div class="secondary">{t('playback.nothing')}</div>
    </section>
  {:else if view === 'connecting'}
    <section class="card quiet">
      <div class="secondary">{t('connection.connecting')}</div>
    </section>
  {:else if view === 'lost'}
    <section class="card problem" role="alert">
      <div class="title">{t('connection.lost')}</div>
      <div class="secondary">{t('connection.lostHelp')}</div>
    </section>
  {:else if view === 'rejected'}
    <section class="card problem" role="alert">
      <div class="title">{t('key.rejected')}</div>
      <div class="secondary">{t('key.rejectedHelp')}</div>
    </section>
  {:else if view === 'missing'}
    <section class="card problem" role="alert">
      <div class="title">{t('key.missing')}</div>
      <div class="secondary">{t('key.missingHelp')}</div>
    </section>
  {/if}
</main>

<style>
  :global(html),
  :global(body) {
    margin: 0;
    padding: 0;
    /* OBS composites the page: only the card may cover the scene. */
    background: transparent;
    overflow: hidden;
  }

  main {
    --pad: calc(16px * var(--scale));
    box-sizing: border-box;
    display: flex;
    justify-content: flex-start;
    padding: calc(8px * var(--scale));
    font-family: Figtree, Inter, system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif;
    color: var(--text);
    font-size: calc(16px * var(--scale));
    line-height: 1.25;
  }

  main.right {
    justify-content: flex-end;
  }

  .card {
    box-sizing: border-box;
    width: min(100%, calc(560px * var(--scale)));
    padding: var(--pad);
    border-radius: var(--radius);
    background: var(--background);
    display: flex;
    flex-direction: column;
    gap: calc(10px * var(--scale));
    animation: enter 320ms ease-out;
  }

  .still .card {
    animation: none;
  }

  @keyframes enter {
    from {
      opacity: 0;
    }
    to {
      opacity: 1;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .card {
      animation: none;
    }
  }

  .card.quiet {
    width: auto;
  }

  .row {
    display: flex;
    align-items: center;
    gap: calc(14px * var(--scale));
    min-width: 0;
  }

  main.right .row {
    flex-direction: row-reverse;
    text-align: right;
  }

  .cover {
    flex: none;
    width: calc(72px * var(--scale));
    height: calc(72px * var(--scale));
    border-radius: calc(var(--radius) * 0.6);
    object-fit: cover;
  }

  /* Tints made from a colour of the style: a layer at low opacity, since
     color-mix() needs Chrome 111 and OBS 30 has 103. */
  .cover.none,
  .progress {
    position: relative;
    overflow: hidden;
  }

  .cover.none::before,
  .progress::before {
    content: '';
    position: absolute;
    inset: 0;
    background: var(--secondary);
    opacity: 0.2;
  }

  .cover.none {
    display: grid;
    place-items: center;
  }

  .cover.none svg {
    width: 45%;
    height: 45%;
    position: relative;
    fill: var(--secondary);
  }

  .text {
    min-width: 0;
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: calc(2px * var(--scale));
  }

  .title,
  .secondary,
  .note {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .title {
    font-weight: 700;
    font-size: 1.15em;
  }

  .secondary {
    color: var(--secondary);
  }

  .album {
    font-size: 0.9em;
  }

  .note {
    color: var(--accent);
    font-size: 0.85em;
    font-weight: 600;
  }

  .problem .title,
  .problem .secondary {
    white-space: normal;
  }

  .problem .title {
    font-size: 1em;
  }

  .problem .secondary {
    font-size: 0.85em;
  }

  .progress {
    height: calc(4px * var(--scale));
    border-radius: 999px;
  }

  .fill {
    position: relative;
    height: 100%;
    background: var(--accent);
    border-radius: inherit;
  }

  .times {
    display: flex;
    justify-content: space-between;
    color: var(--secondary);
    font-size: 0.8em;
    font-variant-numeric: tabular-nums;
    margin-top: calc(-4px * var(--scale));
  }
</style>
