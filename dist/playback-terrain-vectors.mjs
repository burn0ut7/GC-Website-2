import {terrainIndex} from './playback-terrain-index.mjs';

class GeometrySurface {
  constructor(canvas,changed){
    this.canvas=canvas;this.buffers=new Map();
    this.gl=canvas.getContext('webgl2',{alpha:true,antialias:true,depth:true,stencil:false,premultipliedAlpha:true});
    if(!this.gl)throw new Error('WebGL 2 is required for map geometry.');
    canvas.addEventListener('webglcontextlost',e=>{e.preventDefault();this.lost=true;});
    canvas.addEventListener('webglcontextrestored',()=>{this.buffers.clear();this.initialize();this.lost=false;changed();});
    this.initialize();
  }
  initialize(){
    const gl=this.gl;
    const shader=(type,source)=>{const s=gl.createShader(type);gl.shaderSource(s,source);gl.compileShader(s);if(!gl.getShaderParameter(s,gl.COMPILE_STATUS))throw new Error(gl.getShaderInfoLog(s));return s;};
    const vs=shader(gl.VERTEX_SHADER,`#version 300 es
      in vec2 world; in vec2 offset; in vec3 tint; in float order;
      uniform vec2 center; uniform vec2 viewport; uniform float scale;
      out vec3 color;
      void main(){vec2 p=(world-center)*scale+offset;gl_Position=vec4(p*2./viewport,order,1.);color=tint;}`);
    const fs=shader(gl.FRAGMENT_SHADER,`#version 300 es
      precision highp float; in vec3 color; out vec4 pixel;
      void main(){pixel=vec4(color,1.);}`);
    const program=gl.createProgram();gl.attachShader(program,vs);gl.attachShader(program,fs);gl.linkProgram(program);
    gl.deleteShader(vs);gl.deleteShader(fs);if(!gl.getProgramParameter(program,gl.LINK_STATUS))throw new Error(gl.getProgramInfoLog(program));
    this.program=program;gl.useProgram(program);
    this.uniforms=Object.fromEntries(['center','viewport','scale'].map(k=>[k,gl.getUniformLocation(program,k)]));
    this.attributes=['world','offset','tint','order'].map(k=>gl.getAttribLocation(program,k));
    gl.enable(gl.DEPTH_TEST);gl.depthFunc(gl.LEQUAL);gl.clearDepth(1);gl.disable(gl.CULL_FACE);
  }
  clearBuffers(){for(const b of this.buffers.values())this.gl.deleteBuffer(b);this.buffers.clear();}
  draw(batches,view,width,height,dpr){
    if(this.lost)return;
    const gl=this.gl,w=Math.round(width*dpr),h=Math.round(height*dpr);
    if(this.canvas.width!==w||this.canvas.height!==h){this.canvas.width=w;this.canvas.height=h;}
    gl.viewport(0,0,w,h);gl.clearColor(0,0,0,0);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);gl.useProgram(this.program);
    gl.uniform2f(this.uniforms.center,view.x,view.z);gl.uniform2f(this.uniforms.viewport,width,height);gl.uniform1f(this.uniforms.scale,view.scale);
    for(const batch of batches){
      if(!batch.vertices.length)continue;
      let buffer=this.buffers.get(batch);
      if(!buffer){buffer=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,buffer);gl.bufferData(gl.ARRAY_BUFFER,batch.vertices,gl.STATIC_DRAW);this.buffers.set(batch,buffer);}
      else gl.bindBuffer(gl.ARRAY_BUFFER,buffer);
      for(let i=0;i<4;i++){const loc=this.attributes[i];gl.enableVertexAttribArray(loc);gl.vertexAttribPointer(loc,[2,2,3,1][i],gl.FLOAT,false,32,[0,8,16,28][i]);}
      gl.drawArrays(gl.TRIANGLES,0,batch.vertices.length/8);
    }
  }
}

// One geometry pipeline at every zoom. Triangulation happens once off the UI
// thread; camera movement only updates uniforms over immutable GPU buffers.
export class TerrainVectors {
  constructor(canvas,powerCanvas,changed,failed){
    this.canvas=canvas;this.changed=changed;this.failed=failed;this.generation=0;this.visible=[];this.batches=[];
    this.geometry=new GeometrySurface(canvas,changed);this.power=new GeometrySurface(powerCanvas,changed);
  }
  setData(data){
    if(this.data===data)return;
    this.data=data;this.ready=false;this.visible=[];this.batches=[];this.query=null;
    this.geometry.clearBuffers();this.power.clearBuffers();this.worker?.terminate();this.worker=null;
    const generation=++this.generation;
    if(!data){this.ready=true;return;}
    this.worker=new Worker(new URL('./playback-terrain-mesh-worker.mjs',import.meta.url),{type:'module'});
    this.worker.onmessage=({data:mesh})=>{
      if(generation!==this.generation)return;
      if(mesh.error){this.failed(mesh.error);this.worker?.terminate();this.worker=null;return;}
      this.batches=mesh.batches;this.featureCount=mesh.featureCount;this.bytes=mesh.bytes;this.query=terrainIndex(this.batches,512);this.ready=true;
      this.worker.terminate();this.worker=null;this.changed();
    };
    this.worker.onerror=e=>{if(generation===this.generation){this.failed(e.message || "Map geometry worker failed to load.");this.worker?.terminate();this.worker=null;}};
    this.worker.postMessage({generation,data});
  }
  update(data,view,width,height,dpr,options){
    this.setData(data);this.dpr=dpr;
    const margin=4/view.scale;
    this.visible=this.query?.([view.x-width/(2*view.scale)-margin,view.z-height/(2*view.scale)-margin,
      view.x+width/(2*view.scale)+margin,view.z+height/(2*view.scale)+margin])??[];
    const enabled=b=>b.layer==='roads'?options.terrainRoads:b.layer==='buildings'?options.terrainBuildings:b.layer==='walls'?options.terrainBuildings&&view.scale>=.4:false;
    this.geometry.draw(this.visible.filter(enabled),view,width,height,dpr);
  }
  paintPower(ctx,view,width,height,options){
    const power=options.terrainPower?this.visible.filter(b=>b.layer==='powerlines'&&view.scale>=([.0625,.125,.25][b.kind]??0)):[];
    this.power.draw(power,view,width,height,this.dpr);
  }
}
