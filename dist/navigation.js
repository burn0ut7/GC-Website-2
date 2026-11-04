(()=>{
 let cleanup;
 window.mountNavigation=()=>{
  const header=document.querySelector('.quiet-header');if(!header||header.querySelector('.site-navigation'))return;
  cleanup?.abort();cleanup=new AbortController();const signal=cleanup.signal;
  const nav=document.createElement('nav');nav.className='site-navigation';nav.setAttribute('aria-label','Website pages');
  nav.innerHTML='<button type="button" class="pages-toggle" aria-expanded="false" aria-controls="sitePages">Explore pages <span aria-hidden="true">☰</span></button><div id="sitePages" class="pages-panel" hidden><p>Loading pages…</p></div>';
  header.append(nav);const button=nav.querySelector('button'),panel=nav.querySelector('.pages-panel');let request=0,pinned=false;
  async function refresh(){const version=++request;panel.replaceChildren();const message=document.createElement('p');message.textContent='Loading pages…';panel.append(message);try{const response=await fetch('/api/navigation',{cache:'no-store'});if(!response.ok)throw Error();const pages=await response.json();if(version!==request)return;panel.replaceChildren();for(const page of pages){const link=document.createElement('a');link.href=page.href;link.textContent=page.label;if(location.pathname===page.href)link.setAttribute('aria-current','page');panel.append(link);}}catch{if(version===request)message.textContent='Pages unavailable. Close and try again.';}}
  function open(){if(!panel.hidden)return;panel.hidden=false;button.setAttribute('aria-expanded','true');refresh();}
  function close(){pinned=false;panel.hidden=true;button.setAttribute('aria-expanded','false');}
  button.addEventListener('click',()=>{if(pinned)close();else{open();pinned=true;}});
  nav.addEventListener('pointerenter',event=>{if(event.pointerType==='mouse')open();});nav.addEventListener('pointerleave',()=>{if(!nav.contains(document.activeElement))close();});
  nav.addEventListener('focusout',event=>{if(!nav.contains(event.relatedTarget))close();});
  nav.addEventListener('keydown',event=>{if(event.key==='Escape'){close();button.focus();}});
  document.addEventListener('click',event=>{if(!nav.contains(event.target))close();},{signal});
  window.addEventListener('pageshow',event=>{if(event.persisted){close();panel.replaceChildren();}},{signal});
 };
 window.mountNavigation();
})();


