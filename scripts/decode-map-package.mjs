import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {Edds} from '@bis-toolkit/edds';
const [manifestPath, output] = process.argv.slice(2);
const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
fs.mkdirSync(output, {recursive:true});
for (const entry of manifest.sources.filter(e=>e.path.endsWith('.edds'))) {
  const bytes = fs.readFileSync(entry.file);
  if(crypto.createHash('sha256').update(bytes).digest('hex')!==entry.sha256)throw Error('Cached original hash mismatch');
  const image=new Edds();image.read(bytes);
  const id=entry.path===manifest.recipe.raster?'raster':entry.path.slice(manifest.recipe.tilePrefix.length).split('_')[0];
  if(!/^(raster|\d+)$/.test(id))throw Error('Invalid decoded texture identity');
  fs.writeFileSync(path.join(output,`${id}.dds`),Buffer.concat([bytes.subarray(0,148),...image.mipmaps.map(m=>m.data)]));
}
console.log(`Decoded ${manifest.recipe.tileCount} terrain tiles and native map texture`);
