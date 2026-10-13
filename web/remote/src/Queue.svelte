<script>
  import { tick } from 'svelte';
  import { t, plural, clock } from './i18n.js';
  import { getQueue, sendQueue, Unauthorized } from './api.js';
  import Icon from './Icon.svelte';
  import Cover from './Cover.svelte';

  /**
   * token: the pairing token; version: the queue version from the latest
   * state; active: the tab is shown (the queue is only fetched then);
   * connected: commands can be sent; onRejected: the token was refused;
   * notify(text): a short message for the user.
   */
  let { token, version, active, connected, onRejected, notify } = $props();

  let queue = $state(null);
  /** 'loading' | 'ready' | 'error' */
  let phase = $state('loading');
  let open = $state(null); // the row whose actions are shown
  let heading;
  let loadedVersion = -1;
  let request = 0;
  let scrolled = false;
  let refocus = null; // { key, action } to focus once the queue comes back

  const key = (item) => `${item.index}:${item.videoId}`;

  async function load() {
    const id = ++request;
    if (!queue) phase = 'loading';
    try {
      const next = await getQueue(token);
      if (id !== request) return;
      queue = next;
      phase = 'ready';
      loadedVersion = next.version;
      await tick();
      restoreFocus();
      if (!scrolled && next.currentIndex >= 0) {
        scrolled = true;
        document.getElementById('queue-current')?.scrollIntoView({ block: 'center' });
      }
    } catch (error) {
      if (id !== request) return;
      if (error instanceof Unauthorized) onRejected();
      else phase = 'error';
    }
  }

  // Ask again whenever the host says the queue changed, and on coming back
  // to the tab or to a lost connection.
  $effect(() => {
    // After a lost connection Rambutan may have restarted and counted afresh.
    if (!connected) loadedVersion = -1;
    else if (active && version !== loadedVersion) load();
  });

  function restoreFocus() {
    if (!refocus) return;
    const { key: target, action } = refocus;
    refocus = null;
    if (queue.items.some((item) => key(item) === target)) {
      open = target;
      // The same button, or the row's menu when that one is now disabled
      // (moved to the top or the bottom).
      tick().then(() => {
        const find = (name) => document.querySelector(`[data-focus="${CSS.escape(`${target}/${name}`)}"]`);
        const button = find(action);
        (button && !button.disabled ? button : find('more'))?.focus();
      });
    } else {
      heading?.focus();
    }
  }

  async function command(action, body, after) {
    // Set before sending: the new queue may arrive before the reply does.
    refocus = after;
    try {
      await sendQueue(token, action, body);
    } catch (error) {
      refocus = null;
      if (error instanceof Unauthorized) onRejected();
      else notify(t('control.sendFailed'));
    }
  }

  function jump(item) {
    open = null;
    command('jump', { index: item.index, videoId: item.videoId }, null);
  }

  function move(item, by) {
    const to = item.index + by;
    command(
      'move',
      { from: item.index, videoId: item.videoId, to },
      { key: `${to}:${item.videoId}`, action: by < 0 ? 'up' : 'down' },
    );
  }

  function remove(item) {
    open = null;
    // The row is gone; the heading keeps the user's place in the page.
    command('remove', { index: item.index, videoId: item.videoId }, { key: '', action: '' });
  }

  let earlier = $derived(queue?.items.length ? queue.items[0].index : 0);
  let later = $derived(queue?.items.length ? queue.total - 1 - queue.items[queue.items.length - 1].index : 0);
</script>

<section class="queue" aria-labelledby="queue-heading">
  <h2 id="queue-heading" tabindex="-1" bind:this={heading}>{t('queue.title')}</h2>

  {#if !queue && phase === 'loading'}
    <p class="status" role="status">{connected ? t('queue.loading') : t('connection.connecting')}</p>
  {:else if !queue}
    <div class="status" role="alert">
      <p>{t('queue.failed')}</p>
      <button class="pill" disabled={!connected} onclick={load}>{t('common.retry')}</button>
    </div>
  {:else if queue.total === 0}
    <div class="status">
      <p class="empty">{t('queue.empty')}</p>
      <p class="help">{t('queue.emptyHelp')}</p>
    </div>
  {:else}
    {#if phase === 'error'}
      <div class="inline-error" role="alert">
        <span>{t('queue.refreshFailed')}</span>
        <button class="pill" disabled={!connected} onclick={load}>{t('common.retry')}</button>
      </div>
    {/if}
    {#if earlier > 0}<p class="more">{plural('queue.earlier', earlier)}</p>{/if}
    <ol>
      {#each queue.items as item (key(item))}
        {@const current = item.index === queue.currentIndex}
        {@const k = key(item)}
        <li
          class:current
          class:played={item.index < queue.currentIndex}
          id={current ? 'queue-current' : undefined}
          aria-current={current ? 'true' : undefined}
        >
          <div class="row">
            <span class="art"><Cover {token} videoId={item.videoId} has={item.hasArtwork} /></span>
            {#if current}
              <div class="main">
                <span class="badge">{t('queue.nowPlaying')}</span>
                <span class="title">{item.title}</span>
                <span class="sub">{item.artists}</span>
              </div>
            {:else}
              <button
                class="main"
                disabled={!connected}
                aria-label={t(item.autoplay ? 'queue.playAutoplay' : 'queue.play', {
                  title: item.title,
                  artists: item.artists,
                })}
                onclick={() => jump(item)}
              >
                <span class="title">{item.title}</span>
                <span class="sub">
                  {#if item.autoplay}<span class="tag">{t('queue.autoplay')}</span>{/if}{item.artists}
                </span>
              </button>
            {/if}
            <span class="time" aria-hidden="true">{item.durationMs ? clock(item.durationMs) : ''}</span>
            <button
              class="icon"
              data-focus={`${k}/more`}
              aria-label={t('queue.actions', { title: item.title })}
              aria-expanded={open === k}
              aria-controls={open === k ? `actions-${item.index}` : undefined}
              onclick={() => (open = open === k ? null : k)}
            >
              <Icon name="more" />
            </button>
          </div>
          {#if open === k}
            <div class="actions" id={`actions-${item.index}`}>
              <button
                class="pill"
                data-focus={`${k}/up`}
                disabled={!connected || item.index === 0}
                onclick={() => move(item, -1)}
              >
                <Icon name="up" size={18} />{t('queue.moveUp')}
              </button>
              <button
                class="pill"
                data-focus={`${k}/down`}
                disabled={!connected || item.index >= queue.total - 1}
                onclick={() => move(item, 1)}
              >
                <Icon name="down" size={18} />{t('queue.moveDown')}
              </button>
              <button class="pill" disabled={!connected || current} onclick={() => remove(item)}>
                <Icon name="remove" size={18} />{t('queue.remove')}
              </button>
            </div>
          {/if}
        </li>
      {/each}
    </ol>
    {#if later > 0}<p class="more">{plural('queue.later', later)}</p>{/if}
  {/if}
</section>

<style>
  h2 {
    margin: 0 0 8px;
    font-size: 1.2rem;
  }
  h2:focus {
    outline: none;
  }
  .status {
    padding: 32px 0;
    text-align: center;
    color: var(--muted);
  }
  .empty {
    color: var(--text);
    font-weight: 700;
  }
  .help {
    line-height: 1.5;
  }
  .inline-error {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 8px 12px;
    margin-bottom: 8px;
    border-radius: 10px;
    background: var(--warn);
    font-size: 0.9rem;
  }
  .more {
    margin: 8px 0;
    color: var(--muted);
    font-size: 0.85rem;
    text-align: center;
  }
  ol {
    list-style: none;
    margin: 0;
    padding: 0;
  }
  li {
    border-radius: 10px;
  }
  li.current {
    background: var(--panel);
  }
  li.played .title,
  li.played .sub,
  li.played .art {
    opacity: 0.6;
  }
  .art {
    display: flex;
    padding-left: 8px;
  }
  .row {
    display: flex;
    align-items: center;
    gap: 4px;
    min-height: 56px;
  }
  .main {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 2px;
    padding: 8px;
    border: 0;
    border-radius: 10px;
    background: none;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
  }
  div.main {
    cursor: default;
  }
  .title,
  .sub {
    max-width: 100%;
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
  .badge {
    color: var(--accent);
    font-size: 0.75rem;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }
  .tag {
    margin-right: 6px;
    padding: 0 6px;
    border-radius: 4px;
    background: var(--panel);
    font-size: 0.75rem;
  }
  .time {
    color: var(--muted);
    font-size: 0.8rem;
    font-variant-numeric: tabular-nums;
  }
  .icon {
    width: 44px;
    height: 44px;
    flex: none;
    display: grid;
    place-items: center;
    border: 0;
    border-radius: 50%;
    background: none;
    color: var(--muted);
    cursor: pointer;
  }
  .actions {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    padding: 0 8px 12px;
  }
  .pill {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    min-height: 40px;
    padding: 0 14px;
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
  button:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }
</style>
