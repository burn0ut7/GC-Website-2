// Native viewLayer zero means hidden. All enabled landmark types share the
// same browser-map reveal threshold, including facilities and guard towers.
// Three 1.15x wheel steps farther out than the previous threshold of 1.
const landmarkMinimumScale = 1 / (1.15 ** 3);
export function mapSymbolVisible(style, scale) {
  const level = style?.viewLayer;
  return Number.isInteger(level) && level > 0 && level <= 6 &&
    Number.isFinite(scale) && scale >= landmarkMinimumScale;
}
