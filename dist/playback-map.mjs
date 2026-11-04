/** Color.PackToInt uses ARGB. Keep recorded opacity and color, independent of faction UI colors. */
export function mapColor(packed) {
  const n = packed >>> 0;
  return `rgba(${(n >>> 16) & 255},${(n >>> 8) & 255},${n & 255},${(n >>> 24) / 255})`;
}
export function drawMapShapes(ctx, shapes, screen, scale, width, height) {
  ctx.save();
  for (const shape of shapes) {
    const path = new Path2D();
    for (let i = 0; i < shape.points.length; i += 2) {
      const p = screen([shape.points[i], 0, shape.points[i + 1]]);
      if (i === 0) path.moveTo(...p);
      else path.lineTo(...p);
    }
    if (shape.closed) path.closePath();
    if (shape.fillColor >>> 24 > 0 && shape.closed) {
      ctx.fillStyle = mapColor(shape.fillColor);
      if (shape.inverted) {
        const outside = new Path2D();
        outside.rect(0, 0, width, height);
        outside.addPath(path);
        ctx.fill(outside, "evenodd");
      } else ctx.fill(path, "evenodd");
    }
    if (shape.lineColor >>> 24 > 0 && shape.width > 0) {
      ctx.lineWidth = shape.worldWidth
        ? Math.max(1.5, shape.width * scale)
        : shape.width;
      ctx.strokeStyle = mapColor(shape.lineColor);
      ctx.stroke(path);
    }
  }
  ctx.restore();
}
