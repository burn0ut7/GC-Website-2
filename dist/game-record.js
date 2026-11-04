import {slotLabel} from './mission-presentation.mjs?v=metadata-6';

const $ = id => document.getElementById(id);
const record = $('record');
const api = '/api/admin/game';
const params = new URLSearchParams(location.search);
const enc = encodeURIComponent;
const playerUrl = identity => '/players?identity=' + enc(identity);
const sessionUrl = s => '/sessions/' + enc(s.serverId) + '/' + enc(s.sessionId);
const missionUrl = id => '/missions?id=' + enc(id);
const date = t => t == null ? 'Date unrecorded' : new Date(t * 1000).toLocaleString();
const number = n => n == null ? '—' : Number(n).toLocaleString(undefined,{maximumFractionDigits:1});
const duration = n => n == null ? '—' : n < 60 ? Math.floor(n) + ' sec' : (n >= 3600 ? Math.floor(n/3600) + ' hr ' : '') + Math.floor(n/60)%60 + ' min';
const groups = [
  ['Combat', {kills:'Player kills',aiKills:'AI kills',teamKills:'Team kills',deaths:'Deaths',deathsByPlayers:'Deaths to players',deathsByAI:'Deaths to AI',shots:'Shots fired',grenades:'Grenades'}],
  ['Time & movement',{playSeconds:'Play time',walkMetres:'Distance on foot',driveMetres:'Distance driven',pilotMetres:'Distance piloted',radioSecondsClientReported:'Radio time (client reported)'}],
  ['Teamwork',{bandagesOthers:'Teammates bandaged',commanderMissions:'Missions as commander',leaderMissions:'Missions as leader'}]
];
function el(tag,text){const e=document.createElement(tag);if(text!=null)e.textContent=text;return e;}
function link(text,href){const e=el('a',text);e.href=href;return e;}
function heading(title,sub){document.title=title+' · Global Conflicts';$('title').textContent=title;$('subtitle').textContent=sub;}
function section(title){const s=el('section');s.append(el('h2',title));record.append(s);return s;}
function row(parent,cells,tag='td'){const tr=el('tr');for(const item of cells){const cell=el(tag);cell.append(item instanceof Node?item:document.createTextNode(item??'—'));tr.append(cell);}parent.append(tr);return tr;}
function table(parent,labels){const wrap=el('div'),t=el('table'),thead=el('thead'),body=el('tbody');wrap.className='table-scroll';row(thead,labels,'th');t.append(thead,body);wrap.append(t);parent.append(wrap);return body;}
async function get(url){const r=await fetch(url,{cache:'no-store'});if(!r.ok)throw Error(r.status===404?'This record could not be found.':r.status===401?'Your sign-in expired. Reload to sign in.':'Could not load game records ('+r.status+').');return r.json();}
function cards(parent,values){const box=el('div');box.className='mission-stats';for(const [label,value] of values){const card=el('div');card.append(el('strong',value),el('span',label));box.append(card);}parent.append(box);}
function stats(parent,counters){
  const dl=el('dl');dl.className='mission-metadata';
  for(const [group,fields] of groups){const items=Object.entries(fields).filter(([k])=>counters?.[k]!=null);if(!items.length)continue;
    const block=el('div'),values=el('dd');block.append(el('dt',group),values);
    for(const [k,label] of items){const v=counters[k];values.append(el('span',label+' · '+(k.includes('Seconds')?duration(v):number(v)+(k.endsWith('Metres')?' m':''))));}dl.append(block);}
  if(dl.children.length)parent.append(dl);else parent.append(el('p','No counters recorded.'));
}
function assignments(details){return [...new Set((details?.assignments||[]).map(a=>[a.faction,a.groupName,a.role?slotLabel({prefab:a.role}):null].filter(Boolean).join(' / ')).filter(Boolean))].join(' · ');}
function breakdown(counters,details){
  const d=el('details');d.append(el('summary','Stats & roles'));stats(d,counters);
  const a=assignments(details);if(a)d.append(el('p',a));
  if(details?.roles?.length){const body=table(d,['Role','Time','Selections']);
    for(const role of details.roles)row(body,[slotLabel(role),duration(role.seconds),number(role.selections)]);}
  return d;
}
async function paginate(parent,load,render,empty){
  let cursor=null;const more=el('button','Show more');more.hidden=true;parent.append(more);
  async function page(){more.disabled=true;try{const data=await load(cursor);for(const item of data.items)render(item);if(cursor===null&&!data.items.length)parent.append(el('p',empty));cursor=data.next;more.hidden=cursor==null;}finally{more.disabled=false;}}
  more.onclick=()=>page().catch(e=>{$('status').textContent=e.message;});await page();
}
async function showPlayers(){
  heading('Players','Game profiles from recorded sessions.');
  const form=el('form'),input=el('input'),go=el('button','Search');input.name='search';input.type='search';input.maxLength=200;input.placeholder='Find a player';input.setAttribute('aria-label','Find a player');input.value=params.get('search')||'';form.action='/players';form.append(input,go);record.append(form);
  const body=table(record,['Player','Recorded sessions']);
  await paginate(record,async cursor=>{const p=await get(api+'/players?'+new URLSearchParams({search:input.defaultValue||params.get('search')||'',offset:cursor??0}));return {items:p.players,next:p.nextOffset};},
    p=>row(body,[link(p.name||p.identity,playerUrl(p.identity)),number(p.sessions)]),'No players found.');
}
async function showSessions(){
  heading('Sessions','Recorded plays, newest first.');
  const body=table(record,['Session','Mission','Server','Outcome','Duration']);
  await paginate(record,async cursor=>{const p=await get(api+'/sessions?offset='+(cursor??0));return {items:p.sessions,next:p.nextOffset};},
    s=>{const m=s.metadata||{};row(body,[link(date(s.startedAt),sessionUrl({...s,sessionId:s.missionId})),s.catalogId?link(s.missionName,missionUrl(s.catalogId)):m.missionName,m.serverName,m.outcome,duration(m.observedSeconds)]);},'No sessions recorded.');
}
async function showPlayer(identity){
  const p=await get(api+'/player?identity='+enc(identity));heading(p.name||identity,'Player profile · Recorded career totals');
  cards(record,[['Sessions played',number(p.sessions)],['Player kills',number(p.totals.kills)],['Deaths',number(p.totals.deaths)],['Play time',duration(p.totals.playSeconds)]]);
  stats(section('Overall stats'),p.totals);
  record.append(el('p','Totals include sessions with recorded player stats. A dash means the stat was not recorded.'));
  const history=section('Sessions played'),body=table(history,['Session','Mission','Player kills','Deaths','Play time','Performance']);
  await paginate(history,async cursor=>{const p=await get(api+'/player/sessions?'+new URLSearchParams({identity,offset:cursor??0}));return {items:p.sessions,next:p.nextOffset};},
    s=>row(body,[link(date(s.startedAt),sessionUrl(s)),s.catalogId?link(s.missionName,missionUrl(s.catalogId)):s.missionName,number(s.counters.kills),number(s.counters.deaths),duration(s.counters.playSeconds),breakdown(s.counters,s.details)]),'No recorded sessions.');
}
async function showSession(server,id){
  const path='/api/admin/missions/'+enc(server)+'/'+enc(id);
  const s=await get(api+'/sessions/'+enc(server)+'/'+enc(id)),summary=s.summary||{},meta=s.metadata||summary.streams?.history?.metadata||{};
  const knownProfiles=new Set(s.profileIdentities||[]);
  const person=(identity,name)=>knownProfiles.has(identity)?link(name||identity,playerUrl(identity)):(name||identity||'—');
  const name=s.missionName||meta.missionName||'Recorded session';
  heading(name+' · Session',[date(s.startedAt??meta.startedAt??summary.streams?.stats?.metadata?.startedAt),meta.serverName,meta.status,meta.outcome].filter(Boolean).join(' · '));
  if(s.catalogId)record.append(link('View mission: '+name,missionUrl(s.catalogId)));
  if(meta.description)record.append(el('p',meta.description));
  cards(record,[['Recorded players',number(s.recordedPlayers)],['Player kills',number(s.totals.kills)],['Deaths',number(s.totals.deaths)],['Session duration',duration(meta.observedSeconds)]]);
  if(meta.winningFactions?.length)record.append(el('p','Winning factions: '+meta.winningFactions.join(', ')));
  if(meta.commanders?.length){const leaders=el('p');leaders.append('Commanders: ');meta.commanders.forEach((c,i)=>{if(i)leaders.append(' · ');leaders.append(person(c.identity,c.name),c.faction?' ('+c.faction+')':'');});record.append(leaders);}
  const players=section('Players'),body=table(players,['Player','Team / squad / role','Player kills','Deaths','Play time','Performance']);
  await paginate(players,async after=>{const p=await get(path+'/players'+(after?'?after='+enc(after):''));return {items:p.players,next:p.nextAfter};},
    p=>row(body,[link(p.name||p.identity,playerUrl(p.identity)),assignments(p.details)||'—',number(p.counters.kills),number(p.counters.deaths),duration(p.counters.playSeconds),breakdown(p.counters,p.details)]),'No player stats recorded.');
  stats(section('Session totals'),s.totals);
  if(meta.phases?.length){const phases=section('Mission phases'),rows=table(phases,['Phase','Elapsed','Local time']);
    for(const phase of meta.phases)row(rows,[phase.phase,duration(phase.sessionSeconds),date(phase.at)]);}
  if(summary.eventCount){
    const events=section('Session timeline'),rows=table(events,['Elapsed','Event','Player','Target']);

    await paginate(events,async cursor=>{const p=await get(path+'/events?afterSequence='+(cursor??0));return {items:p.events,next:p.nextAfterSequence};},
      e=>row(rows,[duration(e.sessionSeconds),e.type,person(e.actor,e.actorName),person(e.target,e.targetName)]),'No events recorded.');
  }
  if(summary.recordingIds?.length){const replays=section('Playback');for(const [i,r] of summary.recordingIds.entries())replays.append(link('Recording '+(i+1),'/playback?'+new URLSearchParams({server,recording:r})),document.createTextNode(' '));}
  const coverage=section('Recording coverage');for(const [key,v] of Object.entries(summary.streams||{}))coverage.append(el('p',key+' · '+(!v.available?'Not recorded':v.droppedRecords||v.allBatchesPresent===false||v.sourceComplete===false?'Partial data':v.closed?'Closed':'In progress')));
}
(async()=>{try{const path=location.pathname.replace(/\/$/,'');if(path==='/sessions')await showSessions();else if(path.startsWith('/sessions/')){const [, ,server,id]=path.split('/');await showSession(server,id);}else if(params.has('identity'))await showPlayer(params.get('identity'));else await showPlayers();$('status').textContent='';}catch(e){$('status').textContent=e.message;}})();
