"""Assemble installed Arland terrain supertextures into a north-up color atlas.
Input DDS files are decoded original EDDS mip chains; no palette substitution.
"""
import sys
from pathlib import Path
import numpy as np
from PIL import Image

TILE_SIZE = 256
BORDER = 4
CELL_METRES = 4

def tile_interior(tile, border_tolerance=.15):
    """Remove observed repeated border blocks from this installed supertexture layout."""
    if tile.width not in (128, 256, 512) or tile.height != tile.width:
        raise ValueError('Unexpected terrain tile size')
    # Anizay's 128px BC7 tiles retain the same four-pixel border; measured
    # worst edge error across all 1600 tiles is 0.422 of one 8-bit step.
    if tile.width == 128:
        border_tolerance = max(border_tolerance, .5)
    pixels = np.asarray(tile.convert('RGB'))
    # Fail closed if a future source revision changes the layout. All four
    # edge blocks in the supported 128/256 tiles repeat the outer row/column.
    for edge in (pixels[:BORDER], pixels[-BORDER:],
                 pixels.transpose(1,0,2)[:BORDER], pixels.transpose(1,0,2)[-BORDER:]):
        # BC7 can perturb edge texels; use the validated tolerance for the
        # source tile size rather than requiring identical compressed pixels.
        if np.abs(edge.astype(float) - edge[0].astype(float)).mean() > border_tolerance:
            raise ValueError('Unsupported terrain border layout')
    return tile.convert('RGB').crop((BORDER, BORDER, tile.width-BORDER, tile.height-BORDER)).resize(
        (TILE_SIZE, TILE_SIZE), Image.Resampling.BILINEAR)

def average_ground(atlas, cell=CELL_METRES):
    """Average real source colors in linear light, then interpolate cell centers.

    This is cartographic filtering, not a material/layer-weight reconstruction.
    Work on the assembled world so filtering cannot create independent tile seams.
    """
    if cell < 1 or atlas.width % cell or atlas.height % cell:
        raise ValueError('Cell size must divide the atlas')
    srgb = np.asarray(atlas, dtype=np.float32) / 255
    linear = np.where(srgb <= .04045, srgb/12.92, ((srgb+.055)/1.055)**2.4)
    cells = linear.reshape(atlas.height//cell, cell, atlas.width//cell, cell, 3).mean(axis=(1,3))
    smooth = np.stack([np.asarray(Image.fromarray(cells[:,:,i]).resize(atlas.size, Image.Resampling.BILINEAR)) for i in range(3)],axis=2)
    result = np.where(smooth <= .0031308, smooth*12.92, 1.055*np.maximum(smooth,0)**(1/2.4)-.055)
    return Image.fromarray(np.uint8(np.rint(np.clip(result,0,1)*255)))

def build(folder, output):
    atlas=Image.new('RGB',(4096,4096))
    for index in range(256):
        tile=Image.open(Path(folder)/f'{index}.dds')
        atlas.paste(tile_interior(tile),((index%16)*256,(index//16)*256))
    # Tile numbers run X first, then northward Z; texture rows also increase Z.
    average_ground(atlas.transpose(Image.Transpose.FLIP_TOP_BOTTOM)).save(output)
if __name__=='__main__':build(*sys.argv[1:])
