// Flat key → string tables, as in the desktop app (CLAUDE.md: every
// user-visible string goes through them). A table is picked from the
// browser's languages; a key missing from it falls back to English.
import en from './locales/en.json';

const tables = { en };

function pick() {
  for (const tag of navigator.languages ?? [navigator.language]) {
    const base = String(tag).toLowerCase().split('-')[0];
    if (tables[base]) return base;
  }
  return 'en';
}

export const locale = pick();
const table = tables[locale];

/** The string for `key`, with `{name}` placeholders filled from `args`. */
export function t(key, args = {}) {
  const text = table[key] ?? en[key] ?? key;
  return text.replace(/\{(\w+)\}/g, (_, name) => (name in args ? String(args[name]) : `{${name}}`));
}

const plurals = new Intl.PluralRules(locale);

/** The string for `key.one`, `key.other`, … as the locale's plural rules pick
 *  for `count`; `{count}` is filled in. */
export function plural(key, count, args = {}) {
  const form = `${key}.${plurals.select(count)}`;
  return t(form in table || form in en ? form : `${key}.other`, { ...args, count });
}

/** m:ss, or h:mm:ss past an hour. */
export function clock(ms) {
  const total = Math.max(0, Math.floor((ms ?? 0) / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = String(total % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`;
}
