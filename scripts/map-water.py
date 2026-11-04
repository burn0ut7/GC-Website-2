"""Shared cartographic depth palette; depths are measured, colors are presentation."""
import numpy as np
from PIL import Image

DEPTH_STOPS = [0, 2, 8, 30, 100]
WATER_COLORS = [[172,226,235], [115,195,214], [65,142,179], [35,88,130], [20,49,83]]

def terrain_pixels(heights, size):
    # Grid values sit on vertices, whereas output pixels sit at cell centres.
    image=Image.fromarray(np.asarray(heights,dtype=np.float32)[::-1])
    return np.asarray(image.transform((size,size),Image.Transform.EXTENT,
        (.5,.5,image.width-.5,image.height-.5),resample=Image.Resampling.BICUBIC))

def smooth(values, sigma=1.2):
    """Small world-space Gaussian, with no seam between water-body types."""
    radius=int(np.ceil(3*sigma))
    weights=np.exp(-np.arange(-radius,radius+1,dtype=np.float32)**2/(2*sigma*sigma))
    weights/=weights.sum()
    result=np.asarray(values,dtype=np.float32)
    for axis in (0,1):
        padding=[(0,0)]*result.ndim;padding[axis]=(radius,radius)
        source=np.pad(result,padding,mode='edge');filtered=np.zeros_like(result)
        for offset,weight in enumerate(weights):
            window=[slice(None)]*result.ndim;window[axis]=slice(offset,offset+result.shape[axis])
            filtered+=source[tuple(window)]*weight
        result=filtered
    return result

def water_colors(depth):
    depth = np.asarray(depth)
    if not np.isfinite(depth).all():
        raise ValueError('Non-finite water depth')
    return np.stack([np.interp(np.maximum(depth,0), DEPTH_STOPS, [c[i] for c in WATER_COLORS]) for i in range(3)],axis=-1)
