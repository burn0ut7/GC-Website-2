import {buildTerrainMesh} from './playback-terrain-mesh.mjs';
self.onmessage=({data})=>{
  try{const mesh=buildTerrainMesh(data.data);self.postMessage({generation:data.generation,...mesh},mesh.batches.map(b=>b.vertices.buffer));}
  catch(error){self.postMessage({generation:data.generation,error:String(error)});}
};
