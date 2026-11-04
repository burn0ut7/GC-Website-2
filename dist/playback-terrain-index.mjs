// Static uniform grid. Large features stay in a separate list so coastlines
// and long contour paths cannot multiply memory by thousands of grid cells.
export function terrainIndex(features, cellSize = 256) {
  const cells = new Map(), large = [];
  features.forEach((f, id) => {
    const [x,z,r,t] = f.bounds.map(v=>Math.floor(v/cellSize));
    if ((r-x+1)*(t-z+1)>64) { large.push(id); return; }
    for(let a=x;a<=r;a++) for(let b=z;b<=t;b++) {
      const key=`${a},${b}`;
      if(!cells.has(key)) cells.set(key,[]);
      cells.get(key).push(id);
    }
  });
  return bounds => {
    const [x,z,r,t]=bounds, ids=new Set(large);
    const left=Math.floor(x/cellSize),right=Math.floor(r/cellSize);
    const bottom=Math.floor(z/cellSize),top=Math.floor(t/cellSize);
    // Whole-map overview is faster as one scan than thousands of grid lookups.
    if((right-left+1)*(top-bottom+1)>4096) return features.filter(f=>intersects(f.bounds,bounds));
    for(let a=left;a<=right;a++) for(let b=bottom;b<=top;b++)
      for(const id of cells.get(`${a},${b}`) ?? []) ids.add(id);
    return [...ids].sort((a,b)=>a-b).filter(id=>intersects(features[id].bounds,bounds)).map(id=>features[id]);
  };
}
function intersects(a,b) {return a[2]>=b[0]&&a[0]<=b[2]&&a[3]>=b[1]&&a[1]<=b[3];}
