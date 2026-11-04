"""Place water probes inside live river surfaces, independent of riverbed depth.

Native GetOutlinePoints returns alternating left/right vertices in world space.
These hints only position the query; the game still confirms water and its height.
"""
import csv
import numpy as np


def add_surface_hints(lines, surface_path):
    points = np.asarray([[float(v) for v in line.split('\t')] for line in lines], dtype=float)
    if not len(points):
        return []
    if points.shape[1] != 2 or not np.isfinite(points).all():
        raise ValueError('Expected finite X/Z probe pairs')
    rivers = {}
    with open(surface_path, encoding='utf-8-sig', newline='') as source:
        for row in csv.DictReader(source, delimiter='\t'):
            vertices = rivers.setdefault(int(row['river']), [])
            if int(row['index']) != len(vertices):
                raise ValueError('Non-contiguous river outline')
            vertices.append([float(row[key]) for key in ('x', 'y', 'z')])
    hints = np.full(len(points), -np.inf)
    for vertices in rivers.values():
        vertices = np.asarray(vertices)
        if len(vertices) < 4 or len(vertices) % 2 or not np.isfinite(vertices).all():
            raise ValueError('Invalid river outline')
        for i in range(0, len(vertices)-2, 2):
            for indices in ((i, i+1, i+2), (i+1, i+3, i+2)):
                triangle = vertices[list(indices)]
                a, b, c = triangle[:, [0, 2]]
                lo, hi = triangle[:, [0, 2]].min(axis=0), triangle[:, [0, 2]].max(axis=0)
                candidates = np.flatnonzero(np.all((points >= lo) & (points <= hi), axis=1))
                if not len(candidates):
                    continue
                ab, ac = b-a, c-a
                determinant = ab[0]*ac[1]-ab[1]*ac[0]
                if abs(determinant) < 1e-9:
                    continue
                delta = points[candidates]-a
                u = (delta[:, 0]*ac[1]-delta[:, 1]*ac[0])/determinant
                v = (ab[0]*delta[:, 1]-ab[1]*delta[:, 0])/determinant
                inside = (u >= -1e-8) & (v >= -1e-8) & (u+v <= 1+1e-8)
                selected = candidates[inside]
                height = triangle[0, 1]+u[inside]*(triangle[1, 1]-triangle[0, 1])+v[inside]*(triangle[2, 1]-triangle[0, 1])
                hints[selected] = np.maximum(hints[selected], height)
    return [line + (f'\t{height:.6f}' if np.isfinite(height) else '') for line, height in zip(lines, hints)]
