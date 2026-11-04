async function get(path){const r=await fetch(path,{cache:"no-store"});if(!r.ok)throw Error(`Data unavailable (${r.status}).`);return r.json();}
