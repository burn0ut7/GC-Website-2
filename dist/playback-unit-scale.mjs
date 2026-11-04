import {vehicleShape} from './playback-vehicles.mjs';
// Symbol footprints, not measured model bounds. Infantry is about 2m across;
// the 3px visibility floor represents 10m at a .3px/m overview scale.
export function unitSymbolSize(kind, icon, scale, worldScale, legacyRadius) {
  if (!worldScale) return {radius:legacyRadius,factor:1,simple:false};
  const metres=kind==='vehicle' ? ({car:5,apc:7,truck:8,helicopter:14,turret:3,vehicle:6}[vehicleShape(icon)]) : 2;
  const diameter=Math.max(3,metres*scale);
  return {radius:diameter/2,factor:diameter/(legacyRadius*2),simple:diameter<12};
}
