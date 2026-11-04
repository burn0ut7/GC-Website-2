// Small shared preferences use a cookie; potentially large recording-specific
// unit selections stay in browser storage so every request need not carry them.
export function playbackPreferences(doc = document, storage = null) {
  if (!storage) try { storage = localStorage; } catch {}
  const cookieName = 'gc_playback_filters_v1';
  let state = {};
  try { state = JSON.parse(decodeURIComponent(doc.cookie.split('; ').find(x => x.startsWith(cookieName+'='))?.slice(cookieName.length+1) || '{}')); } catch {}
  if (!state || typeof state !== 'object' || Array.isArray(state)) state = {};
  const controls = [...doc.querySelectorAll('.filters input[id], .filters select[id], .event-filters input')].filter(el => !el.closest('.recording-bar'));
  const sections = [...doc.querySelectorAll('[data-filter-section]')];
  const key = el => el.id || 'event:'+el.dataset.event;
  const value = el => el.type === 'checkbox' ? el.checked : el.value;
  const defaults = Object.fromEntries(controls.map(el=>[key(el),value(el)]));
  const defaultSections = Object.fromEntries(sections.map(el=>[el.dataset.filterSection,el.open]));
  function apply(values, opened) {
    for (const el of controls) {
      const saved = values?.[key(el)];
      if (el.type === 'checkbox' && typeof saved === 'boolean') el.checked = saved;
      else if (el.type !== 'checkbox' && typeof saved === 'string') el.value = saved;
    }
    for (const el of sections) if (typeof opened?.[el.dataset.filterSection] === 'boolean') el.open = opened[el.dataset.filterSection];
  }
  // Start the new edge controls with both panels open; remember subsequent choices.
  if (state.panelControlsVersion !== 2) { state.panels = {}; state.panelControlsVersion = 2; }
  const workspace = doc.querySelector('.workspace');
  const panelButtons = [...doc.querySelectorAll('[data-panel-toggle]')];
  function applyPanels() {
    for (const button of panelButtons) {
      const panel = button.dataset.panelToggle;
      const collapsed = state.panels?.[panel] === true;
      workspace?.classList.toggle(panel+'-collapsed', collapsed);
      button.setAttribute('aria-expanded', String(!collapsed));
      const action = (collapsed ? 'Expand ' : 'Collapse ') + panel;
      button.setAttribute('aria-label', action);
      button.title = action;
    }
  }
  applyPanels();
  for (const button of panelButtons) button.addEventListener('click', () => {
    const panel = button.dataset.panelToggle;
    state.panels = {...state.panels, [panel]: state.panels?.[panel] !== true};
    applyPanels();
    save();
  });
  const mapPanel = doc.querySelector('.map-panel');
  // Reveal controls near the boundary without stealing map dragging or opening panels unexpectedly.
  doc.addEventListener('pointermove', event => {
    if (event.pointerType === 'touch' || event.buttons) return;
    const rect = mapPanel?.getBoundingClientRect();
    if (!rect) return;
    const width = doc.defaultView.innerWidth;
    for (const button of panelButtons) {
      const panel = button.dataset.panelToggle;
      const horizontal = panel === 'activity' ? width <= 1100 : width <= 650;
      const near = horizontal
        ? event.clientX >= rect.left && event.clientX <= rect.right && Math.abs(event.clientY - (panel === 'activity' ? rect.bottom : rect.top)) < 64
        : event.clientY >= rect.top && event.clientY <= rect.bottom && Math.abs(event.clientX - (panel === 'activity' ? rect.right : rect.left)) < 64;
      button.classList.toggle('edge-near', near);
    }
  }, {passive: true});
  doc.documentElement.addEventListener('pointerleave', () => panelButtons.forEach(button => button.classList.remove('edge-near')));
  let markerFaction = typeof state.controls?.mapFaction === "string" ? state.controls.mapFaction : "";
  apply(state.controls,state.sections);
  function save() {
    state.controls=Object.fromEntries(controls.map(el=>[key(el),value(el)]));
    state.controls.mapFaction=markerFaction;
    state.sections=Object.fromEntries(sections.map(el=>[el.dataset.filterSection,el.open]));
    const encoded=encodeURIComponent(JSON.stringify(state));
    // Avoid silently truncating a cookie if an unusual custom map has many factions.
    if (encoded.length < 3800) try { doc.cookie=cookieName+'='+encoded+'; Path=/; Max-Age=31536000; SameSite=Lax'+(location.protocol==='https:'?'; Secure':''); } catch {}
  }
  for (const el of controls) el.addEventListener('change',()=>{ if(el.id==='mapFaction') markerFaction=el.value; save(); });
  for (const el of controls) if (el.type==='search' || el.type==='range') el.addEventListener('input',save);
  for (const el of sections) el.addEventListener('toggle',save);
  let units = {recording:null,default:true,exceptions:{}};
  const unitKey='gc_playback_units_v1';
  function saveUnits() { try { storage.setItem(unitKey,JSON.stringify(units)); } catch {} }
  return {
    markerFaction:()=>markerFaction,
    factionEnabled:key=>state.factions?.[key] !== false,
    factionColor:key=>/^#[0-9a-f]{6}$/i.test(state.colors?.[key] || '')?state.colors[key]:null,
    setFaction(key,enabled) { state.factions={...state.factions,[key]:enabled};save(); },
    setColor(key,color) { state.colors={...state.colors,[key]:color};save(); },
    loadUnits(recording) {
      units={recording,default:true,exceptions:{}};
      try { const saved=JSON.parse(storage.getItem(unitKey));if(saved?.recording===recording&&typeof saved.default==='boolean'&&saved.exceptions&&typeof saved.exceptions==='object') units=saved; } catch {}
    },
    unitEnabled:id=>typeof units.exceptions[id]==='boolean'?units.exceptions[id]:units.default,
    setUnit(id,enabled) { units.exceptions[id]=enabled;saveUnits(); },
    allUnits(enabled) { units.default=enabled;units.exceptions={};saveUnits(); },
    reset() { state={panels:state.panels,panelControlsVersion:2};markerFaction="";apply(defaults,defaultSections);units.default=true;units.exceptions={};saveUnits();save(); }
  };
}
