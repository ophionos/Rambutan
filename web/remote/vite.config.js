import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// Served by Rambutan.LocalApi under /remote/; build output is embedded
// into the LocalApi assembly as static assets.
export default defineConfig({
  plugins: [svelte()],
  base: '/remote/',
  build: {
    outDir: '../../src/Rambutan.LocalApi/wwwroot/remote',
    emptyOutDir: true,
  },
});
