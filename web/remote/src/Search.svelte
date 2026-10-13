<script>
  import { t, clock } from './i18n.js';
  import { tick } from 'svelte';
  import { search, searchMore, sendQueue, Unauthorized, HttpError } from './api.js';
  import Icon from './Icon.svelte';
  import Cover from './Cover.svelte';

  /** See Queue.svelte: the same props, less the queue's. */
  let { token, connected, onRejected, notify } = $props();

  let query = $state('');
  let searched = $state('');
  /** 'idle' | 'searching' | 'done' | 'error' */
  let phase = $state('idle');
  let failure = $state('failed');
  let results = $state([]);
  /** The key for the next page; null on the last. */
  let more = $state(null);
  /** 'idle' | 'loading' | 'error' | 'expired', for the next page. */
  let morePhase = $state('idle');
  let controller = null;

  /** Why a request failed, as the status keys word it. A status with a reason
   *  is the host's; no status means the phone couldn't reach Rambutan at all. */
  const reason = (error) =>
    error instanceof HttpError ? (error.kind === 'offline' ? 'offline' : 'failed') : 'unreachable';

  async function run(event) {
    event?.preventDefault();
    const text = query.trim();
    if (!text || !connected) return;
    controller?.abort();
    const mine = (controller = new AbortController());
    searched = text;
    phase = 'searching';
    morePhase = 'idle';
    try {
      const page = await search(token, text, mine.signal);
      results = page.results;
      more = page.more;
      phase = 'done';
    } catch (error) {
      if (mine.signal.aborted) return;
      if (error instanceof Unauthorized) {
        onRejected();
        return;
      }
      failure = reason(error);
      phase = 'error';
    }
  }

  async function loadMore() {
    if (!more || !connected) return;
    controller?.abort();
    const mine = (controller = new AbortController());
    const first = results.length;
    morePhase = 'loading';
    try {
      const page = await searchMore(token, more, mine.signal);
      // A song can come back on a later page; each row is one song.
      const seen = new Set(results.map((song) => song.videoId));
      results = [...results, ...page.results.filter((song) => !seen.has(song.videoId))];
      more = page.more;
      morePhase = 'idle';
      // Keep the user's place on the first new song, not on one of its
      // buttons: a stray tap on Play would replace the queue.
      await tick();
      (document.querySelectorAll('.search li')[first]?.querySelector('.info') ?? nextArea)?.focus();
    } catch (error) {
      if (mine.signal.aborted) return;
      if (error instanceof Unauthorized) {
        onRejected();
        return;
      }
      if (error instanceof HttpError && error.status === 404) morePhase = 'expired';
      else {
        failure = reason(error);
        morePhase = 'error';
      }
      // The More button is gone: focus Retry, or the text that says why.
      await tick();
      (nextArea?.querySelector('button') ?? nextArea)?.focus();
    }
  }

  let nextArea = $state(null);

  async function play(song) {
    try {
      await sendQueue(token, 'play', { videoId: song.videoId });
      notify(t('search.playing', { title: song.title }), 'status');
    } catch (error) {
      if (error instanceof Unauthorized) onRejected();
      else if (error instanceof HttpError && error.status === 404) notify(t('search.gone'));
      else notify(t('control.sendFailed'));
    }
  }

  async function add(song, next) {
    try {
      await sendQueue(token, 'add', { videoId: song.videoId, next });
      notify(t(next ? 'search.addedNext' : 'search.added', { title: song.title }), 'status');
    } catch (error) {
      if (error instanceof Unauthorized) onRejected();
      else if (error instanceof HttpError && error.status === 404) notify(t('search.gone'));
      else notify(t('control.sendFailed'));
    }
  }

  const details = (song) =>
    [song.artists, song.album, song.durationMs ? clock(song.durationMs) : null].filter(Boolean).join(' • ');
</script>

<section class="search" aria-labelledby="search-heading">
  <h2 id="search-heading">{t('search.title')}</h2>
  <form role="search" onsubmit={run}>
    <label for="search-box" class="visually-hidden">{t('search.label')}</label>
    <input
      id="search-box"
      type="search"
      bind:value={query}
      maxlength="200"
      autocomplete="off"
      enterkeyhint="search"
      placeholder={t('search.placeholder')}
      aria-describedby={connected ? undefined : 'search-disconnected'}
    />
    <button class="go" type="submit" disabled={!connected || !query.trim()} aria-label={t('search.submit')}>
      <Icon name="search" />
    </button>
  </form>

  <div aria-live="polite" aria-busy={phase === 'searching'}>
    <!-- The search button is disabled while disconnected, so Enter does nothing:
         say why where the search was tried, not only in the banner. -->
    {#if !connected}
      <p class="status" id="search-disconnected">{t('search.disconnected')}</p>
    {:else if phase === 'idle'}
      <p class="status">{t('search.idle')}</p>
    {:else if phase === 'searching'}
      <p class="status">{t('search.searching')}</p>
    {:else if phase === 'error'}
      <div class="status">
        <p>{t(`search.${failure}`)}</p>
        <button class="pill" disabled={!connected} onclick={() => run()}>{t('common.retry')}</button>
      </div>
    {:else if results.length === 0}
      <p class="status">{t('search.none', { query: searched })}</p>
    {:else}
      <h3 class="visually-hidden">{t('search.results', { query: searched })}</h3>
    {/if}
  </div>

  {#if phase === 'done' && results.length > 0}
    <ul>
      {#each results as song (song.videoId)}
        <li>
          <div class="song">
            <Cover {token} videoId={song.videoId} has={song.hasArtwork} />
            <div class="info" tabindex="-1">
              <span class="title">{song.title}</span>
              <span class="sub">{details(song)}</span>
            </div>
          </div>
          <div class="actions">
            <button
              class="pill"
              disabled={!connected}
              aria-label={t('search.playLabel', { title: song.title })}
              onclick={() => play(song)}
            >
              <Icon name="play" size={18} />{t('search.play')}
            </button>
            <button
              class="pill"
              disabled={!connected}
              aria-label={t('search.playNextLabel', { title: song.title })}
              onclick={() => add(song, true)}
            >
              <Icon name="playNext" size={18} />{t('search.playNext')}
            </button>
            <button
              class="pill"
              disabled={!connected}
              aria-label={t('search.addLabel', { title: song.title })}
              onclick={() => add(song, false)}
            >
              <Icon name="add" size={18} />{t('search.add')}
            </button>
          </div>
        </li>
      {/each}
    </ul>
    <div class="next" aria-live="polite" tabindex="-1" bind:this={nextArea}>
      {#if morePhase === 'loading'}
        <p class="status">{t('search.loadingMore')}</p>
      {:else if morePhase === 'expired'}
        <p class="status">{t('search.moreExpired')}</p>
      {:else if morePhase === 'error'}
        <div class="status">
          <p>{t(`search.${failure}`)}</p>
          <button class="pill" disabled={!connected} onclick={loadMore}>{t('common.retry')}</button>
        </div>
      {:else if more}
        <button class="pill wide" disabled={!connected} onclick={loadMore}>{t('search.more')}</button>
      {/if}
    </div>
  {/if}
</section>

<style>
  h2 {
    margin: 0 0 8px;
    font-size: 1.2rem;
  }
  form {
    display: flex;
    gap: 8px;
  }
  input {
    flex: 1;
    min-width: 0;
    height: 44px;
    box-sizing: border-box;
    padding: 0 14px;
    border: 1px solid var(--line);
    border-radius: 22px;
    background: var(--panel);
    color: var(--text);
    font: inherit;
  }
  .go {
    width: 44px;
    height: 44px;
    flex: none;
    display: grid;
    place-items: center;
    border: 0;
    border-radius: 50%;
    background: var(--text);
    color: var(--bg);
    cursor: pointer;
  }
  .status {
    padding: 24px 0;
    text-align: center;
    color: var(--muted);
    line-height: 1.5;
  }
  ul {
    list-style: none;
    margin: 8px 0 0;
    padding: 0;
  }
  li {
    padding: 10px 0;
    border-bottom: 1px solid var(--line);
  }
  .song {
    display: flex;
    align-items: center;
    gap: 12px;
  }
  .next {
    display: flex;
    justify-content: center;
    padding: 12px 0;
  }
  .next:focus,
  .info:focus {
    outline: none;
  }
  .next .status {
    padding: 0;
  }
  .wide {
    min-width: 60%;
    justify-content: center;
  }
  .info {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }
  .title,
  .sub {
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }
  .title {
    font-weight: 600;
  }
  .sub {
    color: var(--muted);
    font-size: 0.85rem;
  }
  .actions {
    display: flex;
    flex-wrap: wrap;
    justify-content: flex-end;
    gap: 8px;
    margin-top: 8px;
  }
  .pill {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    white-space: nowrap;
    min-height: 40px;
    padding: 0 12px;
    border: 1px solid var(--line);
    border-radius: 20px;
    background: none;
    color: var(--text);
    font: inherit;
    font-size: 0.9rem;
    cursor: pointer;
  }
  button:disabled {
    opacity: 0.4;
    cursor: default;
  }
  button:focus-visible,
  input:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }
</style>
