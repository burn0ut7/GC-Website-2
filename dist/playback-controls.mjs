// Keep idle controls out of the way without resizing or rebuilding the map.
export function playbackControls(bar, button, doc = document) {
  let playing = false, lastActivity = performance.now(), hidden = false;
  const pointers = new Set();
  const header = doc.querySelector('.quiet-header');
  if (header) {
    const measure = () => doc.documentElement.style.setProperty('--playback-header-height', `${header.getBoundingClientRect().height}px`);
    new ResizeObserver(measure).observe(header);
    measure();
  }
  const surfaces = [bar, ...doc.querySelectorAll(".quiet-header, .map-toolbar, .map-name")];
  function reveal() {
    lastActivity = performance.now();
    setHidden(false);
  }
  function setHidden(value) {
    if (hidden === value) return;
    hidden = value;
    for (const surface of surfaces) {
      surface.classList.toggle('transport-hidden', value);
      surface.inert = value;
    }
  }
  for (const name of ['pointermove', 'wheel', 'keydown', 'focusin', 'input'])
    doc.addEventListener(name, reveal, {capture:true, passive:true});
  doc.addEventListener('pointerdown', e => { pointers.add(e.pointerId); reveal(); }, true);
  for (const name of ['pointerup', 'pointercancel'])
    doc.addEventListener(name, e => { pointers.delete(e.pointerId); reveal(); }, true);
  doc.defaultView.addEventListener('blur', () => { pointers.clear(); reveal(); });
  return {
    setPlaying(value) {
      playing = value;
      button.dataset.playing = String(value);
      const label = value ? 'Pause' : 'Play';
      button.setAttribute('aria-label', label);
      button.title = label + ' (Space)';
      reveal();
    },
    tick(now) {
      const keyboardFocus = surfaces.some(surface => surface.contains(doc.activeElement)) && doc.activeElement.matches(':focus-visible');
      const navigationOpen = Boolean(doc.querySelector('.quiet-header .pages-toggle[aria-expanded="true"]'));
      setHidden(playing && !pointers.size && !keyboardFocus && !navigationOpen && now-lastActivity >= 3000);
    }
  };
}
