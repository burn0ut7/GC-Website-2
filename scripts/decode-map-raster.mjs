// Decode a managed, original game EDDS export; preserve the source unchanged.
import fs from 'node:fs';
import { Edds } from '@bis-toolkit/edds';
const [source, output] = process.argv.slice(2);
if (!source || !output) throw new Error('Usage: node scripts/decode-map-raster.mjs input.edds output.dds');
const bytes = fs.readFileSync(source);
const image = new Edds();
image.read(bytes);
fs.writeFileSync(output, Buffer.concat([bytes.subarray(0, 148), ...image.mipmaps.map(m => m.data)]));
