// Terrain-only compositor. Geometry is rasterized once in the worker; movement
// changes quad coordinates. Actors, labels and interaction retain their 2D canvas.
export class TerrainCompositor {
  constructor(canvas, changed, transparent=false) {
    this.canvas=canvas;
    this.transparent=transparent;
    this.gl=canvas.getContext('webgl2',{alpha:transparent,antialias:false,depth:false,stencil:false,premultipliedAlpha:true});
    if(!this.gl) throw new Error('WebGL 2 is required for terrain rendering.');
    this.textures=new Map();this.globalAlpha=1;this.stack=[];
    canvas.addEventListener('webglcontextlost',event=>{event.preventDefault();this.lost=true;});
    canvas.addEventListener('webglcontextrestored',()=>{this.textures.clear();this.initialize();this.lost=false;changed();});
    this.initialize();
  }
  initialize() {
    const gl=this.gl;
    const shader=(type,source)=>{
      const s=gl.createShader(type);gl.shaderSource(s,source);gl.compileShader(s);
      if(!gl.getShaderParameter(s,gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
      return s;
    };
    const vertex=shader(gl.VERTEX_SHADER,`#version 300 es
      in vec2 position; uniform vec4 destination; uniform vec4 source;
      uniform vec2 viewport; out vec2 uv;
      void main(){vec2 p=destination.xy+position*destination.zw;
        gl_Position=vec4(p.x/viewport.x*2.-1.,1.-p.y/viewport.y*2.,0.,1.);
        uv=source.xy+position*source.zw;}`);
    const fragment=shader(gl.FRAGMENT_SHADER,`#version 300 es
      precision highp float; uniform sampler2D picture; uniform float opacity;
      in vec2 uv; out vec4 color;
      void main(){color=texture(picture,uv)*opacity;}`);
    const program=gl.createProgram();gl.attachShader(program,vertex);gl.attachShader(program,fragment);gl.linkProgram(program);
    gl.deleteShader(vertex);gl.deleteShader(fragment);
    if(!gl.getProgramParameter(program,gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
    gl.useProgram(program);this.program=program;
    this.uniforms=Object.fromEntries(['destination','source','viewport','opacity'].map(k=>[k,gl.getUniformLocation(program,k)]));
    const buffer=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,buffer);
    gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([0,0,1,0,0,1,0,1,1,0,1,1]),gl.STATIC_DRAW);
    const loc=gl.getAttribLocation(program,'position');gl.enableVertexAttribArray(loc);gl.vertexAttribPointer(loc,2,gl.FLOAT,false,0,0);
    gl.enable(gl.BLEND);gl.blendFunc(gl.ONE,gl.ONE_MINUS_SRC_ALPHA);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL,true);
    this.maxTextureSize=gl.getParameter(gl.MAX_TEXTURE_SIZE);
  }
  begin(width,height,dpr,images) {
    if(this.lost) return false;
    const w=Math.round(width*dpr),h=Math.round(height*dpr);
    if(this.canvas.width!==w||this.canvas.height!==h){this.canvas.width=w;this.canvas.height=h;}
    this.dpr=dpr;this.globalAlpha=1;this.stack.length=0;
    const gl=this.gl;gl.viewport(0,0,w,h);gl.uniform2f(this.uniforms.viewport,width,height);
    for(const [image,chunks] of this.textures) if(!images.has(image)) {for(const texture of chunks.values())gl.deleteTexture(texture);this.textures.delete(image);}
    return true;
  }
  getTransform(){return {a:this.dpr};}
  save(){this.stack.push(this.globalAlpha);}
  restore(){this.globalAlpha=this.stack.pop();}
  fillRect(){const gl=this.gl;if(this.transparent)gl.clearColor(0,0,0,0);else gl.clearColor(25/255,32/255,23/255,1);gl.clear(gl.COLOR_BUFFER_BIT);}
  drawImage(image,sx,sy,sw,sh,x,y,w,h) {
    if(w<=0||h<=0||this.globalAlpha===0)return;
    const gl=this.gl,iw=image.naturalWidth||image.width,ih=image.naturalHeight||image.height;
    let chunks=this.textures.get(image);
    if(!chunks){chunks=new Map();this.textures.set(image,chunks);}
    // Oversized addon rasters are partitioned without rescaling or discarding
    // pixels. Upload only source chunks intersecting the visible crop.
    const chunkSize=Math.max(iw,ih)>this.maxTextureSize?Math.min(2048,this.maxTextureSize-2):Math.max(iw,ih);
    for(let cy=Math.max(0,Math.floor(sy/chunkSize));cy<=Math.min(Math.ceil(ih/chunkSize)-1,Math.floor((sy+sh)/chunkSize));cy++)
    for(let cx=Math.max(0,Math.floor(sx/chunkSize));cx<=Math.min(Math.ceil(iw/chunkSize)-1,Math.floor((sx+sw)/chunkSize));cx++) {
      const ox=cx*chunkSize,oy=cy*chunkSize,cw=Math.min(chunkSize,iw-ox),ch=Math.min(chunkSize,ih-oy);
      const l=Math.max(sx,ox),t=Math.max(sy,oy),r=Math.min(sx+sw,ox+cw),b=Math.min(sy+sh,oy+ch);
      if(r<=l||b<=t)continue;
      const split=chunkSize<Math.max(iw,ih);
      const ul=split?Math.max(0,ox-1):ox,ut=split?Math.max(0,oy-1):oy;
      const ur=split?Math.min(iw,ox+cw+1):ox+cw,ub=split?Math.min(ih,oy+ch+1):oy+ch;
      const tw=ur-ul,th=ub-ut;
      const key=`${cx}:${cy}`;let texture=chunks.get(key);
      if(!texture) {
        let source=image;
        if(split) {
          source=new OffscreenCanvas(tw,th);
          source.getContext('2d').drawImage(image,ul,ut,tw,th,0,0,tw,th);
        }
        texture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,texture);
        gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
        gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,source);
        // Do not synchronously query the GPU per upload: it stalls the render
        // queue during tile arrival. Context loss is handled by the canvas events.
        chunks.set(key,texture);
      } else gl.bindTexture(gl.TEXTURE_2D,texture);
      gl.uniform4f(this.uniforms.source,(l-ul)/tw,(t-ut)/th,(r-l)/tw,(b-t)/th);
      gl.uniform4f(this.uniforms.destination,x+(l-sx)/sw*w,y+(t-sy)/sh*h,(r-l)/sw*w,(b-t)/sh*h);
      gl.uniform1f(this.uniforms.opacity,this.globalAlpha);
      gl.drawArrays(gl.TRIANGLES,0,6);
    }
  }
}
