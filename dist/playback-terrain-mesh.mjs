import earcut from './vendor/earcut.mjs';

// Immutable world-coordinate triangles. Pixel offsets keep outlines the same
// width through zoom without tessellating again. Earcut 3.0.2, ISC (vendor/).
const roadColors=[[.784,.784,.784],[.913,.833,.124],[.913,.873,.422],[.578,.421,.268],[1,.07,.07],[.159,.159,.159],[1,1,1]];
class Vertices {
  constructor(){this.data=new Float32Array(4096);this.length=0;}
  add(x,z,dx,dz,c){if(this.length+8>this.data.length){const next=new Float32Array(this.data.length*2);next.set(this.data);this.data=next;}this.data.set([x,z,dx,dz,...c,this.depth],this.length);this.length+=8;}
  finish(){return this.data.slice(0,this.length);}
}
function stroke(out,p,color,width,closed){
  const count=p.length/2,half=width/2;
  // A round join/cap is a small world-anchored screen-space fan, prepared once.
  const disk=(x,z)=>{for(let j=0;j<8;j++){const a=j*Math.PI/4,b=(j+1)*Math.PI/4;out.add(x,z,0,0,color);out.add(x,z,Math.cos(a)*half,Math.sin(a)*half,color);out.add(x,z,Math.cos(b)*half,Math.sin(b)*half,color);}};
  for(let i=0;i<(closed?count:count-1);i++){
    const j=(i+1)%count,ax=p[i*2],az=p[i*2+1],bx=p[j*2],bz=p[j*2+1],len=Math.hypot(bx-ax,bz-az);if(len===0)continue;
    const nx=-(bz-az)/len*half,nz=(bx-ax)/len*half;
    out.add(ax,az,nx,nz,color);out.add(ax,az,-nx,-nz,color);out.add(bx,bz,nx,nz,color);
    out.add(bx,bz,nx,nz,color);out.add(ax,az,-nx,-nz,color);out.add(bx,bz,-nx,-nz,color);
    // Buildings use bevel joins; power lines use round joins/caps.
    if(!closed)disk(ax,az);
    else {
      const k=(i+count-1)%count,px=p[k*2],pz=p[k*2+1],pl=Math.hypot(ax-px,az-pz);
      if(pl){const qx=-(az-pz)/pl*half,qz=(ax-px)/pl*half;
        for(const sign of [-1,1]){out.add(ax,az,0,0,color);out.add(ax,az,nx*sign,nz*sign,color);out.add(ax,az,qx*sign,qz*sign,color);}
      }
    }
  }
  if(!closed&&count)disk(p.at(-2),p.at(-1));
}
export function buildTerrainMesh(data){
  if(data?.version!==1||!Array.isArray(data.features)||data.features.length>200000)throw new Error('Unsupported terrain geometry');
  const groups=new Map(),roadKinds=new Map();let featureCount=0;
  for(const f of data.features)if(f.layer==='roads'&&!roadKinds.has(f.kind))roadKinds.set(f.kind,roadKinds.size);
  let orderIndex=0;
  for(const f of data.features){
    if(!['roads','walls','buildings','powerlines'].includes(f.layer))continue;
    const p=f.points;
    if(!Array.isArray(p)||p.length<4||p.length%2||p.length>200000||p.some(x=>!Number.isFinite(x)||Math.abs(x)>100000))throw new Error('Invalid terrain coordinates');
    const b=[Infinity,Infinity,-Infinity,-Infinity];
    for(let i=0;i<p.length;i+=2){b[0]=Math.min(b[0],p[i]);b[1]=Math.min(b[1],p[i+1]);b[2]=Math.max(b[2],p[i]);b[3]=Math.max(b[3],p[i+1]);}
    const key=[f.layer,f.kind,Math.floor((b[0]+b[2])/1024),Math.floor((b[1]+b[3])/1024)].join(':');
    let g=groups.get(key);if(!g){g={layer:f.layer,kind:f.kind,bounds:[...b],vertices:new Vertices()};groups.set(key,g);}
    g.bounds=[Math.min(g.bounds[0],b[0]),Math.min(g.bounds[1],b[1]),Math.max(g.bounds[2],b[2]),Math.max(g.bounds[3],b[3])];
    const out=g.vertices;
    // Depth preserves authored painter order across spatial draw batches. Equal
    // depths allow each building outline to overlay its own fill. Roads retain
    // the canvas renderer's global first-seen-kind ordering.
    out.depth=f.layer==='roads'?.9-roadKinds.get(f.kind)/Math.max(1,roadKinds.size)*.3:
      f.layer==='walls'?.5-.1*(orderIndex+1)/(data.features.length+1):f.layer==='buildings'?-.8*(orderIndex+1)/(data.features.length+1):-.9;
    orderIndex++;
    if(f.layer==='powerlines')stroke(out,p,[.082,.082,.082],[2.2,1.9,1.6][f.kind]??1.6,false);
    else {
      const color=f.layer==='roads'?(roadColors[f.kind]??[1,1,1]):f.kind===0?[.456,.456,.456]:[.204,.204,.204];
      for(const i of earcut(p))out.add(p[i*2],p[i*2+1],0,0,color);
      if(f.layer==='buildings')stroke(out,p,f.kind===0?[.205,.205,.205]:[0,0,0],.9,true);
    }
    featureCount++;
  }
  const order=['roads','walls','buildings','powerlines'];
  const batches=[...groups.values()].sort((a,b)=>order.indexOf(a.layer)-order.indexOf(b.layer)).map(g=>({...g,vertices:g.vertices.finish()}));
  return {batches,featureCount,bytes:batches.reduce((n,b)=>n+b.vertices.byteLength,0)};
}
