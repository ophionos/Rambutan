import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// Served by Rambutan.LocalApi under /overlay/ and shown by OBS's browser
// source; the build output is embedded into the LocalApi assembly.
export default defineConfig({
  plugins: [svelte()],
  base: '/overlay/',
  // The fonts come from the app's own folder (src/fonts.css); let the dev
  // server read them too. The build copies them either way.
  server: { fs: { allow: ['.', '../../src/Rambutan.App/Assets/Fonts'] } },
  build: {
    outDir: '../../src/Rambutan.LocalApi/wwwroot/overlay',
    emptyOutDir: true,
    // OBS embeds its own Chromium: 127 since OBS 31, 103 in OBS 30. Vite 8's
    // default (Chrome 111) would leave OBS 30 behind.
    target: ['chrome103', 'edge103', 'firefox114', 'safari16.4'],
  },
});
