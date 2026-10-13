<script>
  import { onDestroy } from 'svelte';
  import { artwork } from './api.js';
  import Icon from './Icon.svelte';

  /**
   * A row's cover, decorative: the row carries the title. Fetched only
   * once the row scrolls near the screen, as a long queue has hundreds of rows;
   * the host serves covers only for tracks it knows (`has`).
   */
  let { token, videoId, has, size = 44 } = $props();

  let element;
  let url = $state(null);
  let loadedFor = null;
  let visible = $state(false);
  let observer = null;

  $effect(() => {
    if (!element || typeof IntersectionObserver === 'undefined') {
      visible = true;
      return;
    }
    observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) {
          visible = true;
          observer?.disconnect();
        }
      },
      { rootMargin: '200px' },
    );
    observer.observe(element);
    return () => observer?.disconnect();
  });

  $effect(() => {
    const key = has && visible ? videoId : null;
    if (key === loadedFor) return;
    loadedFor = key;
    drop();
    if (!key) return;
    artwork(token, key, true)
      .then((next) => {
        if (loadedFor === key) url = next;
        else if (next) URL.revokeObjectURL(next);
      })
      .catch(() => {
        // The placeholder stays.
      });
  });

  function drop() {
    if (url) URL.revokeObjectURL(url);
    url = null;
  }

  onDestroy(drop);
</script>

<span class="cover" bind:this={element} style:width={`${size}px`} style:height={`${size}px`} aria-hidden="true">
  {#if url}
    <img src={url} alt="" />
  {:else}
    <Icon name="note" size={Math.round(size / 2)} />
  {/if}
</span>

<style>
  .cover {
    flex: none;
    display: grid;
    place-items: center;
    overflow: hidden;
    border-radius: 6px;
    background: var(--line);
    color: var(--muted);
  }
  img {
    width: 100%;
    height: 100%;
    object-fit: cover;
  }
</style>
