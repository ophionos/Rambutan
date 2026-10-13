<script>
  // Bars from the analyzer's raw bands. The host sends them as they are; the
  // smoothing, the peaks and the look are done here, as the desktop app's
  // VisualizerControl does them for its own bars.
  import { onDestroy, untrack } from 'svelte';
  import { follow } from './api.js';

  let { token, bars = 32, peaks = true, accent, peakColor, height } = $props();

  // Rise fast enough to feel live, fall slow enough to read as motion
  // (the same constants as VisualizerControl).
  const attackTau = 0.035;
  const decayTau = 0.16;
  const peakFallPerSecond = 0.9;
  // No new frame for this long: the feed stopped (pause); fall to rest.
  const staleAfterMs = 300;
  const bandCount = 32;

  const target = new Float32Array(bandCount);
  const display = new Float32Array(bandCount);
  const peakLevels = new Float32Array(bandCount);
  let lastFrameAt = -Infinity;
  let lastDrawAt = 0;
  let frame = 0;
  let canvas = $state();

  // The key is fixed for the page's life: read it once.
  const stop = follow('/api/v1/overlay/bands', untrack(() => token), {
    onEvent: (name, data) => {
      if (name !== 'bands' || !Array.isArray(data.bands)) return;
      for (let i = 0; i < bandCount; i++) target[i] = Number(data.bands[i]) || 0;
      lastFrameAt = performance.now();
      wake();
    },
  });

  onDestroy(() => {
    stop();
    cancelAnimationFrame(frame);
    frame = 0;
  });

  function wake() {
    if (frame) return;
    lastDrawAt = performance.now();
    frame = requestAnimationFrame(draw);
  }

  /** `levels` folded into `count` bars, each the loudest band in its slice;
   *  the top quarter is left out, as in the app (near silent in most music).
   *  At 32 bars every band is its own bar. */
  function group(levels, count) {
    if (count >= bandCount) return levels;
    const used = (bandCount * 3) / 4;
    const out = new Float32Array(count);
    for (let g = 0; g < count; g++) {
      const start = Math.floor((g * used) / count);
      const end = Math.max(start + 1, Math.floor(((g + 1) * used) / count));
      let level = 0;
      for (let i = start; i < end && i < used; i++) level = Math.max(level, levels[i]);
      out[g] = level;
    }
    return out;
  }

  function draw(now) {
    frame = 0;
    const dt = Math.min(0.1, Math.max(0.001, (now - lastDrawAt) / 1000));
    lastDrawAt = now;
    const stale = now - lastFrameAt > staleAfterMs;
    const attack = 1 - Math.exp(-dt / attackTau);
    const decay = 1 - Math.exp(-dt / decayTau);

    let moving = !stale;
    for (let i = 0; i < bandCount; i++) {
      const goal = stale ? 0 : target[i];
      display[i] += (goal - display[i]) * (goal > display[i] ? attack : decay);
      peakLevels[i] = Math.max(display[i], peakLevels[i] - peakFallPerSecond * dt);
      if (display[i] > 0.002 || peakLevels[i] > 0.002) moving = true;
    }

    paint();
    // At rest with no feed: stop drawing until the next frame arrives.
    if (moving) frame = requestAnimationFrame(draw);
  }

  function paint() {
    if (!canvas) return;
    const ratio = window.devicePixelRatio || 1;
    const width = canvas.clientWidth;
    const h = canvas.clientHeight;
    if (canvas.width !== Math.round(width * ratio) || canvas.height !== Math.round(h * ratio)) {
      canvas.width = Math.round(width * ratio);
      canvas.height = Math.round(h * ratio);
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    ctx.clearRect(0, 0, width, h);

    const levels = group(display, bars);
    const count = levels.length;
    const gap = Math.max(2, Math.min(4, width / count / 4));
    const barWidth = (width - (count - 1) * gap) / count;
    if (barWidth <= 0) return;
    const radius = Math.min(barWidth / 2, 2);
    const minHeight = 2;

    // Each bar's peak is the loudest peak in its slice.
    const peakLevelsGrouped = peaks ? group(peakLevels, count) : null;
    for (let i = 0; i < count; i++) {
      const x = i * (barWidth + gap);
      const barHeight = Math.max(minHeight, levels[i] * h);
      ctx.fillStyle = accent;
      roundRect(ctx, x, h - barHeight, barWidth, barHeight, radius);
      if (peakLevelsGrouped) {
        const peakY = h - Math.max(minHeight, peakLevelsGrouped[i] * h);
        if (peakY < h - barHeight - 2) {
          ctx.fillStyle = peakColor;
          roundRect(ctx, x, peakY, barWidth, 2, radius);
        }
      }
    }
  }

  function roundRect(ctx, x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
    ctx.fill();
  }

  // Restyled or resized: draw once at the new size even when at rest.
  $effect(() => {
    void bars;
    void peaks;
    void accent;
    void peakColor;
    void height;
    paint();
  });
</script>

<canvas bind:this={canvas} style:height="{height}px" aria-hidden="true"></canvas>

<style>
  canvas {
    display: block;
    width: 100%;
  }
</style>
