import {missionTypes, mapName, missionFactions, missionLabels, slotLabel} from './mission-presentation.mjs?v=metadata-6';
let mapNames=[];
const $=id=>document.getElementById(id);

const node=(tag,text)=>{const e=document.createElement(tag);if(text!=null)e.textContent=text;return e;};

const link=(text,url)=>{const a=node('a',text);a.href=url;return a;};

const date=value=>value?new Date(value*1000).toLocaleString(): '—';

async function get(url){const r=await fetch(url,{cache:'no-store'});if(!r.ok)throw Object.assign(Error(r.status===401?'Sign in to view session history.':r.status===403?'Session history and playback require the Developer role.':`Could not load data (${r.status}).`),{status:r.status});return r.json();}

let next=null,sessionNext=null,missionId=new URLSearchParams(location.search).get('id');

async function list(append=false){$('status').textContent='Loading…';try{const q=new URLSearchParams({search:$('search').value,tag:$('tag').value,offset:append?next:0});const data=await get('/api/catalog?'+q);if(!append)$('missions').replaceChildren();for(const m of data.missions){const row=node('tr'),name=node('td');name.append(link(m.name,'/missions?id='+m.id),node('small',[...missionLabels(m.metadata),...(!m.available&&m.metadata?.kind!=='test'?['Archived']:[])].join(' · ')));row.append(name,...[missionTypes[m.metadata?.missionType]||'—',mapName(m.metadata,mapNames)||'—',m.author||'—',playerRange(m),m.playCount,date(m.lastPlayedAt)].map(v=>node('td',v)));$('missions').append(row);}next=data.nextOffset;$('more').hidden=next==null;$('status').textContent=$('missions').children.length?'':'No missions found.';}catch(e){$('status').textContent=e.message;}}

async function sessions(append=false) {
  try {
    const data=await get('/api/admin/catalog/'+encodeURIComponent(missionId)+'/sessions?offset='+(append?sessionNext:0));
    const container=$('sessions');
    if(!append)container.replaceChildren();
    for(const session of data.sessions){
      const meta=session.metadata||{},item=node('p');
      item.append(link(date(session.startedAt),'/sessions/'+session.serverId+'/'+session.missionId));
      const facts=[meta.serverName,meta.status,meta.outcome,meta.observedSeconds==null?null:Math.round(meta.observedSeconds/60)+' min'].filter(Boolean);
      if(facts.length)item.append(' · '+facts.join(' · '));
      container.append(item);
    }
    sessionNext=data.nextOffset;
    $('moreSessions').hidden=sessionNext==null;
    if(!container.children.length)container.textContent='No linked sessions yet.';
  }catch(e){$('sessions').textContent=e.message;if(e.status===401)$('sessions').append(node('br'),link('Sign in with Discord','/auth/discord'));}
}
async function start(){try{mapNames=await get('/api/catalog/maps').catch(()=>[]);if(missionId){$('catalog').hidden=true;$('detail').hidden=false;const m=await get('/api/catalog/'+encodeURIComponent(missionId));$('missionName').textContent=m.name;$('missionMeta').textContent=[mapName(m.metadata,mapNames),m.author,playerRange(m)==='—'?null:playerRange(m)+' players',m.metadata?.kind==='test'?'Test mission':m.available?null:'Archived'].filter(Boolean).join(' · ');$('description').textContent=m.description||'Description not supplied.';renderMetadata(m);$('source').hidden=m.metadata?.sourceKind==='local';if(!$('source').hidden)$('source').href='https://github.com/'+m.repository;await Promise.all([statistics(),sessions()]);}else{await list();const data=await get('/api/catalog/status').catch(()=>null);if(!data){freshness.textContent='Update status unavailable.';return;}for(const tag of data.tags){const o=node('option',tag);o.value=tag;$('tag').append(o);}const issues=data.sources.flatMap(s=>(s.issues||[]).map(i=>({...i,repository:s.repository})));if(issues.length){const details=node('details');details.append(node('summary',issues.length+' source file'+(issues.length===1?' needs':'s need')+' attention'));for(const issue of issues)details.append(node('p',issue.path+' · '+(issue.retained?'Last valid version retained':'Missing or invalid mission header')));$('freshness').after(details);}const dates=data.sources.map(s=>s.succeededAt).filter(Boolean).sort();$('freshness').textContent=data.sources.some(s=>s.error)?'Update unavailable · showing the last successful import':data.sources.every(s=>s.branch==='local')?'Test mission library':dates.length?'Automatically updated · checked '+new Date(dates[0]).toLocaleString():'Waiting for the first automatic import';}}catch(e){$('status').textContent=e.message;}}

$('filters').onsubmit=e=>{e.preventDefault();list();};$('more').onclick=()=>list(true);$('moreSessions').onclick=()=>sessions(true);start();


async function statistics(){
 try{const s=await get(`/api/admin/catalog/${missionId}/statistics`);$('missionStats').replaceChildren();
 for(const [label,value] of [['Sessions',s.sessions],['Players',s.players],['Playbacks',s.recordings],['Player hours',s.playerSeconds==null?'—':(s.playerSeconds/3600).toFixed(1)],['Kills',s.kills??'—'],['Deaths',s.deaths??'—']]){const item=node('div');item.append(node('strong',value),node('span',label));$('missionStats').append(item);}
 }catch(e){if(e.status!==401&&e.status!==403)$('missionStats').textContent='Statistics unavailable.';}
}

function playerRange(m){const min=m.metadata?.minPlayers,max=m.maxPlayers;return min&&max&&min!==max?min+'\u2013'+max:max?String(max):min?min+'+':'\u2014';}

function renderMetadata(m){
 const metadata=m.metadata||{},details=$('missionMetadata');details.replaceChildren();
 const fields=[['Start time',metadata.startTime?[metadata.startTime]:[]],['Mission type',metadata.missionType?[missionTypes[metadata.missionType]||'Not specified']:[]],['Players',playerRange(m)==='—'?[]:[playerRange(m)]],['Objectives',metadata.objectives||[]],['Mods',metadata.mods||[]],['Force composition',metadata.assetTypes||[]]];
 if(metadata.era)fields.push(['Era',[metadata.era]]);
 if(metadata.tags?.length)fields.push(['Tags',metadata.tags]);
 const factions=missionFactions(metadata);if(factions.length)fields.push(['Factions',factions]);
 for(const [label,values] of fields){
  const row=node('div');row.append(node('dt',label));const value=node('dd');
  if(!values.length)value.textContent='Not specified';else for(const text of values)value.append(node('span',text));
  row.append(value);details.append(row);
 }
 $('missionDetails').textContent=metadata.details||'';$('missionDetails').hidden=!metadata.details;
 const content=$('missionContent');content.replaceChildren();content.hidden=true;
 const generated=metadata.generated;
 if(generated){
  content.hidden=false;content.append(node('summary','Content & dependencies'));
  if(generated.playerSlots?.length){
   const slots=node('details');slots.append(node('summary',`Player slots · ${generated.playerSlots.length}`));
   const table=node('table'),head=node('tr');for(const text of ['Group','Slot','Faction','Loadout'])head.append(node('th',text));table.append(head);
   for(const slot of generated.playerSlots){const row=node('tr');for(const text of [slot.group||'—',slot.index+1,slot.faction||slot.loadoutFaction||'—',slotLabel(slot)])row.append(node('td',text));table.append(row);}
   slots.append(table);content.append(slots);
  }
  if(generated){
   content.append(node('h3','Detected world content'));
   const assets=Object.entries(generated.assets||{}).map(([name,count])=>`${name.replaceAll('_',' ')}: ${count}`);
   content.append(node('p',assets.length?assets.join(' · '):'No asset categories recorded'));
   content.append(node('p','Placed content only · player availability not inferred'));
   const limits=node('ul');for(const limit of generated.limitations||[])limits.append(node('li',limit));content.append(limits);
  }
 }
}
