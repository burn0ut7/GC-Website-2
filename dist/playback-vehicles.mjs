// Top-down silhouettes: north-facing artwork, classified by the recorded native icon.
// Native editor vehicle icons are side views and cannot express map heading clearly.
export function vehicleShape(icon) {
  const name = icon?.name ?? "";
  if (name.includes("Helicopter")) return "helicopter";
  if (name.includes("Apc")) return "apc";
  if (name.includes("Truck")) return "truck";
  if (name.includes("Turret")) return "turret";
  if (name.includes("Offroad") || name.includes("Civilian")) return "car";
  return "vehicle";
}

export function drawVehicle(ctx, point, heading, color, icon) {
  const shape = vehicleShape(icon);
  ctx.save();
  ctx.translate(...point);
  ctx.rotate(((heading ?? 0) * Math.PI) / 180);
  ctx.fillStyle = color;
  ctx.strokeStyle = "#10170f";
  ctx.lineWidth = 2;
  ctx.lineJoin = "round";
  const body = (points) => {
    ctx.beginPath();
    points.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
    ctx.closePath();
    ctx.fill();
    ctx.stroke();
  };
  if (shape === "helicopter") {
    body([
      [-4, -12],
      [0, -16],
      [4, -12],
      [5, 3],
      [2, 7],
      [2, 17],
      [7, 19],
      [-7, 19],
      [-2, 17],
      [-2, 7],
      [-5, 3],
    ]);
    ctx.strokeStyle = color;
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.moveTo(-17, -3);
    ctx.lineTo(17, -3);
    ctx.moveTo(0, -19);
    ctx.lineTo(0, 13);
    ctx.stroke();
  } else if (shape === "turret") {
    body([
      [-8, 7],
      [-5, -4],
      [5, -4],
      [8, 7],
    ]);
    ctx.strokeStyle = color;
    ctx.lineWidth = 4;
    ctx.beginPath();
    ctx.moveTo(0, 2);
    ctx.lineTo(0, -16);
    ctx.stroke();
  } else {
    const wide = shape === "apc" ? 10 : 8;
    const length = shape === "truck" ? 18 : 14;
    // Wheels/tracks and tapered nose remain recognizable without a circular badge.
    ctx.fillRect(-wide - 3, -9, 4, 7);
    ctx.fillRect(wide - 1, -9, 4, 7);
    ctx.fillRect(-wide - 3, 7, 4, 7);
    ctx.fillRect(wide - 1, 7, 4, 7);
    body([
      [-wide + 3, -length],
      [wide - 3, -length],
      [wide, -length + 5],
      [wide, length],
      [-wide, length],
      [-wide, -length + 5],
    ]);
    ctx.fillStyle = "#10170f";
    ctx.fillRect(-wide + 2, -length + 5, wide * 2 - 4, 4);
    if (shape === "truck") ctx.strokeRect(-5, -1, 10, 15);
    if (shape === "apc") {
      ctx.strokeRect(-4, -2, 8, 8);
      ctx.lineWidth = 3;
      ctx.beginPath();
      ctx.moveTo(0, 2);
      ctx.lineTo(0, -18);
      ctx.stroke();
    }
  }
  ctx.restore();
}
