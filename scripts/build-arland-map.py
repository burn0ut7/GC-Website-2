"""Build local, static map vectors from the installed Arland TOPO and a live terrain grid.

Format evidence and limitations: docs/topographical-map.md. No game files are modified.
Usage: python scripts/build-arland-map.py <Arland.topo> <terrain.json> <output.json>
"""
import hashlib
import json
import math
from pathlib import Path
import struct
import sys

import contourpy
import numpy as np

SOURCE_HASH = "479a06286587a076a0dd83e5c5e0a78154d81c8a6f9a248a453ad43ca56cf0ec"


class Reader:
    def __init__(self, data, bounds=(0, 0, 4096, 4096)):
        self.data, self.pos = data, 0
        self.bounds = bounds

    def read(self, fmt):
        size = struct.calcsize("<" + fmt)
        if self.pos + size > len(self.data):
            raise ValueError("Truncated topography")
        values = struct.unpack_from("<" + fmt, self.data, self.pos)
        self.pos += size
        return values

    def count(self, maximum=1000000):
        n, = self.read("I")
        if n > maximum:
            raise ValueError("Invalid topography count")
        return n

    def points(self):
        n = self.count()
        values = self.read("f" * (n * 2))
        if any(not math.isfinite(v) or abs(v) > 100000 for v in values):
            raise ValueError("Invalid map coordinate")
        # TOPO polygon Y points south; the replay world Z points north.
        return [(self.bounds[0] + values[i], self.bounds[3] - values[i + 1]) for i in range(0, len(values), 2)]


def hull(points):
    """Order each native road quad's four corners around its perimeter."""
    points = sorted(set(points))
    def cross(a, b, c):
        return (b[0]-a[0])*(c[1]-a[1]) - (b[1]-a[1])*(c[0]-a[0])
    def half(seq):
        result = []
        for p in seq:
            while len(result) >= 2 and cross(result[-2], result[-1], p) <= 0:
                result.pop()
            result.append(p)
        return result
    return half(points)[:-1] + half(points[::-1])[:-1]


def build(topo_path, height_path, bounds=(0, 0, 4096, 4096), expected_hash=SOURCE_HASH):
    data = Path(topo_path).read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    # Fail closed on other map/version layouts until independently verified.
    if digest != expected_hash:
        raise ValueError("Unsupported TOPO revision: verify a new map before adding it")
    root = Reader(data, bounds)
    tag, version, length, end = root.read("4sIIQ")
    if tag != b"TOPO" or version != 1 or end != len(data)-4 or data[end:] != b"_EOF":
        raise ValueError("Invalid TOPO container")
    chunks = {}
    while root.pos < end:
        tag, version, length, next_pos = root.read("4sIIQ")
        if next_pos != root.pos + length or next_pos > end:
            raise ValueError("Invalid TOPO chunk extent")
        if tag.decode() in chunks:
            raise ValueError("Duplicate TOPO chunk")
        chunks[tag.decode()] = (version, Reader(data[root.pos:next_pos], bounds))
        root.pos = next_pos
    features = []
    def feature(layer, points, kind=0):
        if len(points) >= 2:
            features.append(dict(layer=layer, kind=kind, points=[round(v, 2) for p in points for v in p]))

    version, r = chunks["ROAD"]
    if version != 2:
        raise ValueError("Unsupported ROAD version")
    road_counts = r.read("8I")
    road_count = 0
    for lod in range(r.count(8)):
        count = r.count()
        for _ in range(count):
            kind, = r.read("B")
            points = r.points()
            indices = r.read("I" * r.count())
            if lod != 0:
                continue
            road_count += 1
            # The auxiliary list contains section boundaries, not triangle indices.
            # Filling each native quad needs no join across those boundaries.
            if any(i >= len(points) or i % 4 for i in indices) or len(points) % 4:
                raise ValueError("Unsupported road quad layout")
            for i in range(0, len(points), 4):
                feature("roads", hull(points[i:i+4]), kind)
    if r.pos != len(r.data) or road_count != sum(road_counts):
        raise ValueError("Incomplete ROAD data")

    version, r = chunks["BULD"]
    if version != 1:
        raise ValueError("Unsupported chunk version")
    anchors = []
    for kind in range(r.count(8)):
        for _ in range(r.count()):
            feature("walls" if kind in (2, 3) else "buildings", r.points(), kind)
        for _ in range(r.count()):
            # Native distant-symbol anchors already use world X/Z. Do not duplicate buildings.
            anchors.append(r.read("I4f"))
    if r.pos != len(r.data):
        raise ValueError("Incomplete chunk data")

    version, r = chunks["PWLN"]
    if version != 1:
        raise ValueError("Unsupported chunk version")
    for lod in range(r.count(8)):
        for _ in range(r.count()):
            kind, = r.read("I")
            points = r.points()
            if lod == 0:
                feature("powerlines", points, kind)
    if r.pos != len(r.data):
        raise ValueError("Incomplete chunk data")

    # Native water is a triangle mesh, not a sea-level contour. Retain ponds
    # and river stretches above sea level using the highest-detail WATR mesh.
    version, r = chunks["WATR"]
    if version != 1:
        raise ValueError("Unsupported WATR version")
    water_bodies = {}
    for lod in range(r.count(8)):
        for _ in range(r.count()):
            kind, flag = r.read("IB")
            points = r.points()
            indices = r.read("I" * r.count())
            if len(indices) % 3 or any(i >= len(points) for i in indices):
                raise ValueError("Unsupported water mesh layout")
            # Meshes only select probe locations. Live water queries determine
            # actual coverage/depth, including authored surfaces hidden by ground.
            # Native style kind/flag are retained, not interpreted as the live
            # EWaterSurfaceType enum (the values are not interchangeable).
            if lod == 0:
                water_bodies[kind] = water_bodies.get(kind, 0) + 1
                if height_path is not None:
                    for i in range(0, len(indices), 3):
                        feature("water", hull([points[j] for j in indices[i:i+3]]), kind)
    if r.pos != len(r.data):
        raise ValueError("Incomplete WATR data")

    if height_path is None:
        # File-only prototype: no invented elevation contours or water depths.
        counts = {layer:sum(f["layer"] == layer for f in features) for layer in sorted({f["layer"] for f in features})}
        return dict(version=1, bounds=list(bounds), source=dict(topoSha256=digest,
            nativeRoads=road_count, buildingSymbolAnchors=len(anchors), waterBodies=water_bodies,
            heightData="not-exported", waterGeometry="authored mesh; not terrain-clipped"), counts=counts, features=features)

    height_bytes = Path(height_path).read_bytes()
    terrain = json.loads(height_bytes)
    columns = int((bounds[2]-bounds[0])/8)+1
    rows = int((bounds[3]-bounds[1])/8)+1
    if terrain["spacing"] != 8 or terrain["rows"] != rows or terrain["columns"] != columns:
        raise ValueError("Unsupported terrain grid")
    heights = np.asarray(terrain["heights"]).reshape((rows, columns))
    if not np.isfinite(heights).all() or heights.max() <= 50:
        raise ValueError("Invalid live terrain grid")
    contour = contourpy.contour_generator(x=bounds[0]+np.arange(columns)*8, y=bounds[1]+np.arange(rows)*8, z=heights)
    for elevation in range(0, math.ceil(heights.max()), 5):
        for line in contour.lines(elevation):
            feature("contours", line, elevation)
    counts = {layer:sum(f["layer"] == layer for f in features) for layer in sorted({f["layer"] for f in features})}
    return dict(version=1, bounds=list(bounds), source=dict(topoSha256=digest,
        terrainSha256=hashlib.sha256(height_bytes).hexdigest(), gameVersion="1.8.0.13",
        terrainSpacingMetres=8, contourIntervalMetres=5, nativeRoads=road_count,
        buildingSymbolAnchors=len(anchors), waterBodies=water_bodies), counts=counts, features=features)


if __name__ == "__main__":
    result = build(sys.argv[1], sys.argv[2])
    output = Path(sys.argv[3])
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, separators=(",", ":")), encoding="utf-8")
    print(json.dumps(dict(counts=result["counts"], bytes=output.stat().st_size, source=result["source"])))
