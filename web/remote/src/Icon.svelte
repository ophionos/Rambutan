<script module>
  // Icons: the desktop app's geometry (App/Styles/Icons.axaml), decorative only;
  // the control that holds one carries the name.
  export const paths = {
    note: 'M12 3v10.55A4 4 0 1 0 14 17V7h4V3h-6z',
    queue: 'M15 6H3v2h12V6zm0 4H3v2h12v-2zM3 16h8v-2H3v2zM17 6v8.18c-.31-.11-.65-.18-1-.18-1.66 0-3 1.34-3 3s1.34 3 3 3 3-1.34 3-3V8h3V6h-5z',
    search:
      'M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z',
    more: 'M12 8c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm0 2c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2zm0 6c-1.1 0-2 .9-2 2s.9 2 2 2 2-.9 2-2-.9-2-2-2z',
    up: 'M4 12l1.41 1.41L11 7.83V20h2V7.83l5.58 5.59L20 12l-8-8-8 8z',
    down: 'M20 12l-1.41-1.41L13 16.17V4h-2v12.17l-5.58-5.59L4 12l8 8 8-8z',
    remove: 'M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z',
    add: 'M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z',
    play: 'M8 5v14l11-7z',
    playNext: 'M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z',
  };
</script>

<script>
  let { name, size = 24 } = $props();
</script>

<svg viewBox="0 0 24 24" width={size} height={size} aria-hidden="true" focusable="false"><path d={paths[name]} /></svg>

<style>
  svg {
    fill: currentColor;
    flex: none;
  }
</style>
