import { drawTopography, drawContourLabels, contourIntervalForScale, contourStyle } from "./playback-topography.mjs";
export function drawStaticMap(ctx, view, width, height, scene) {
  const {mapConfig, mapImage, naturalImages, topography, options} = scene;
  const checked = key => options[key];
  const screen = p => [width / 2 + (p[0] - view.x) * view.scale,
    height / 2 - (p[2] - view.z) * view.scale];
  if (mapConfig) {
    const b = mapConfig.bounds,
      p = screen([b[0], 0, b[3]]);
    const natural =
      checked("terrainRelief") && naturalImages.ocean && naturalImages.relief;
    ctx.globalAlpha = natural ? 1 : 0.7;
    if (natural || mapImage)
      ctx.drawImage(
        natural || mapImage,
        p[0],
        p[1],
        (b[2] - b[0]) * view.scale,
        (b[3] - b[1]) * view.scale,
      );
    ctx.globalAlpha = 1;
  }
  if (topography) {
    const contourInterval = contourIntervalForScale(view.scale);
    const style = contourStyle(view.scale, Boolean(checked("terrainRelief") && naturalImages.relief));
    // Contours and their sparse labels sit beneath roads, buildings and canopy.
    if (checked("terrainContours")) {
      drawTopography(ctx, topography, view, width, height, new Set(["contours"]), null, contourInterval, style);
      drawContourLabels(ctx, topography, view, width, height, contourInterval, style, scene.contourLabels);
    }
    const enabled = new Set();
    if (checked("terrainWater")) enabled.add("water");
    if (checked("terrainRoads")) enabled.add("roads");
    if (checked("terrainBuildings")) {
      enabled.add("buildings");
      enabled.add("walls");
    }

    drawTopography(
      ctx,
      topography,
      view,
      width,
      height,
      enabled,
      naturalImages.ocean
        ? {
            image:
              checked("terrainWater") && naturalImages.inlandWater
                ? naturalImages.inlandWater
                : naturalImages.ocean,
            bounds: mapConfig.bounds,
            includesInland: checked("terrainWater") && Boolean(naturalImages.inlandWater),
          }
        : null,
      contourInterval,
    );
  }
  // Scenery sits over water; offline heights attenuate submerged objects.
  if (mapConfig) {
    const b = mapConfig.bounds, p = screen([b[0], 0, b[3]]);
    ctx.globalAlpha = options.sceneryOpacity / 100;
    for (const [layer, control] of [
      ["trees", "terrainTrees"],
      ["bushes", "terrainBushes"],
      ["rocks", "terrainRocks"],
    ]) {
      if (checked(control) && naturalImages[layer])
        ctx.drawImage(
          naturalImages[layer],
          p[0],
          p[1],
          (b[2] - b[0]) * view.scale,
          (b[3] - b[1]) * view.scale,
        );
    }
    ctx.globalAlpha = 1;
  }
  // Utility lines remain legible above the translucent canopy layer.
  if (topography) {
    if (checked("terrainPower"))
      drawTopography(ctx, topography, view, width, height, new Set(["powerlines"]));
  }
}
