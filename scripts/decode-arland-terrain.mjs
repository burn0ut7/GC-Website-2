import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {createRequire} from 'node:module';
import {pathToFileURL} from 'node:url';
// Build-only dependency installed under artifacts/map-tools; not part of the web server.
const require=createRequire(path.resolve('artifacts/map-tools/package.json'));
const {Edds}=await import(pathToFileURL(require.resolve('@bis-toolkit/edds')).href);
const [manifestPath,output]=process.argv.slice(2);
const manifest=JSON.parse(fs.readFileSync(manifestPath,'utf8'));
if(manifest.length!==256) throw Error('Expected all 256 Arland tiles');
fs.mkdirSync(output,{recursive:true});
const ids=new Set();
for(const entry of manifest){
 const match=entry.entry.path.match(/^worlds\/Arland\/Terrain\/\.Data\/Terrain_(\d+)_supertexture\.edds$/);
 if(!match || +match[1]>255 || ids.has(+match[1])) throw Error('Unexpected/duplicate terrain tile');
 ids.add(+match[1]);
 const bytes=fs.readFileSync(entry.file);
 if(crypto.createHash('sha256').update(bytes).digest('hex')!==entry.sha256)throw Error('Source hash mismatch');
 if(bytes.readUInt32LE(128)!==99)throw Error('Expected BC7 sRGB terrain texture');
 const e=new Edds();e.read(bytes);
 fs.writeFileSync(path.join(output,`${match[1]}.dds`),Buffer.concat([bytes.subarray(0,148),...e.mipmaps.map(m=>m.data)]));
}
console.log(`Decoded ${ids.size} verified Arland tiles`);
