/** White, unrotated speaking glyph inside the existing faction-colored marker. */
export function drawVoiceGlyph(ctx, [x, y], channel) {
  ctx.save();
  ctx.translate(x, y);
  ctx.strokeStyle = "#fff";
  ctx.fillStyle = "#fff";
  ctx.lineWidth = 1.8;
  ctx.lineJoin = "round";
  ctx.lineCap = "round";
  ctx.beginPath();
  if (channel === "radio") {
    // A solid walkie-talkie silhouette stays legible at the 28px marker size.
    ctx.lineWidth = 2.5;
    ctx.moveTo(-3.5, -4); ctx.lineTo(-3.5, -11);
    ctx.moveTo(3, -4); ctx.lineTo(3, -6);
    ctx.stroke();
    ctx.beginPath();
    ctx.roundRect(-6, -4, 12, 14, 2);
    ctx.rect(-3.5, -1.5, 7, 3);
    ctx.rect(-3, 4, 6, 1.25);
    ctx.rect(-3, 6.5, 6, 1.25);
    ctx.fill("evenodd");
    ctx.beginPath();
  } else {
    ctx.moveTo(-8, -3); ctx.lineTo(-4, -3);
    ctx.lineTo(1, -7); ctx.lineTo(1, 7);
    ctx.lineTo(-4, 3); ctx.lineTo(-8, 3); ctx.closePath();
    ctx.fill();
    ctx.moveTo(5, -4); ctx.quadraticCurveTo(9, 0, 5, 4);
    ctx.moveTo(8, -7); ctx.quadraticCurveTo(14, 0, 8, 7);
  }
  ctx.stroke();
  ctx.restore();
}
