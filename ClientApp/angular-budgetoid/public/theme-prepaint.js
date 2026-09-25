// Apply a saved theme before first paint to avoid a flash of the wrong mode.
// ThemeService remains the runtime authority; this only pre-paints.
//
// This lives in its own file rather than inline in index.html so that the
// Content-Security-Policy can say `script-src 'self'` and mean it: an inline
// script is refused by that directive, and the alternative — pinning a
// `'sha256-...'` of this snippet in the policy — puts the hash and the snippet
// in two files that drift the first time either is edited, with a theme flash
// as the only symptom.
//
// Referenced from <head> without `defer` and without `type="module"`: both
// would postpone execution past first paint, which is the one thing this
// script exists to beat.
try {
  const m = localStorage.getItem('budgetoid-theme');
  if (m === 'light' || m === 'dark') {
    document.documentElement.style.colorScheme = m;
  }
} catch (e) {}
