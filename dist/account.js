const $=selector=>document.querySelector(selector);

const status=$('#status');

const defaults={displayName:'',timeZone:'local',clockFormat:'auto',videoVolume:25,reduceMotion:false};

let saved=null,me=null,busy=false;

function values(){return {displayName:$('#displayName').value,timeZone:$('#timeZone').value,clockFormat:$('#clockFormat').value,videoVolume:saved?.videoVolume??25,reduceMotion:saved?.reduceMotion??false};}

function fill(value){for(const key of ['displayName','timeZone','clockFormat'])$('#'+key).value=value[key];}

function dirty(){return saved!==null&&JSON.stringify(values())!==JSON.stringify(saved);}

function feedback(message,kind=''){const target=$('#saveStatus');target.textContent=message;target.className='feedback '+kind;}

function updateForm(){const changed=dirty();$('#saveSettings').disabled=busy||!changed;$('#resetSettings').disabled=busy||saved===null;}

function showName(){const name=saved?.displayName||me.name;$('#accountName').textContent=name;$('#initials').textContent=Array.from(name).slice(0,2).join('').toUpperCase();}

function showSection(){const section=['connections','roles','donations'].includes(location.hash.slice(1))?location.hash.slice(1):'preferences';for(const name of ['preferences','connections','roles','donations'])$('#'+name).hidden=name!==section;document.querySelectorAll('[data-section]').forEach(link=>{if(link.dataset.section===section)link.setAttribute('aria-current','page');else link.removeAttribute('aria-current');});}

async function loadSettings(){

 $('#preferenceFields').disabled=true;$('#retrySettings').hidden=true;feedback('Loading settings…');

 try{const response=await fetch('/api/account/settings',{cache:'no-store'});if(!response.ok)throw new Error(response.status===401?'Your session expired. Reload to sign in.':'Settings could not be loaded. Try again.');saved=await response.json();fill(saved);showName();$('#preferenceFields').disabled=false;feedback('');}

 catch(error){feedback(error.message,'error');$('#retrySettings').hidden=false;}

 updateForm();

}

async function loadAccount(){

 $('#retryAccount').hidden=true;status.textContent='Loading your account…';

 try{

  const response=await fetch('/api/me',{cache:'no-store'});

  if(response.status===401){const configResponse=await fetch('/api/auth/status',{cache:'no-store'});if(!configResponse.ok)throw new Error('Sign-in is temporarily unavailable.');const config=await configResponse.json();status.textContent=!config.discordEnabled?'Discord sign-in has not been configured on this server yet.':!config.https?'Open the HTTPS version of this website to sign in.':'Sign in to manage your profile and preferences.';$('#signIn').hidden=!(config.discordEnabled&&config.https);return;}

  if(!response.ok)throw new Error('Your account could not be loaded. Try again.');

  me=await response.json();$('#discordId').textContent=me.discordId;$('#accountId').textContent=me.id;$('#adminLink').hidden=!me.isAdmin;showName();$('#authState').hidden=true;$('#profile').hidden=false;showSection();await Promise.all([loadSettings(),loadSteam(),loadRoles(),loadPatreon(),loadDonationPrivacy()]);

 }catch(error){status.textContent=error.message;$('#retryAccount').hidden=false;}

}

async function csrf(){const response=await fetch('/api/auth/csrf',{cache:'no-store'});if(!response.ok)throw new Error('Your session expired. Reload to sign in.');return (await response.json()).token;}

$('#preferencesForm').addEventListener('input',()=>{updateForm();feedback(dirty()?'Unsaved changes':'');});

$('#preferencesForm').addEventListener('change',()=>{updateForm();feedback(dirty()?'Unsaved changes':'');});

$('#preferencesForm').addEventListener('submit',async event=>{

 event.preventDefault();if(!dirty()||busy)return;busy=true;$('#preferenceFields').disabled=true;updateForm();feedback('Saving…');

 try{const response=await fetch('/api/account/settings',{method:'PUT',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':await csrf()},body:JSON.stringify(values())});if(!response.ok)throw new Error(response.status===401?'Your session expired. Reload to sign in.':response.status===400?'Check your settings, then try saving again.':'Could not save. Your changes are still here—try again.');saved=await response.json();fill(saved);showName();feedback('Changes saved','success');}

 catch(error){feedback(error.message,'error');}

 finally{busy=false;$('#preferenceFields').disabled=false;updateForm();}

});

$('#resetSettings').addEventListener('click',()=>{fill(defaults);updateForm();feedback(dirty()?'Defaults selected. Save changes to apply.':'Already using the defaults.');});

$('#retrySettings').addEventListener('click',loadSettings);$('#retryAccount').addEventListener('click',loadAccount);

$('#signOut').addEventListener('click',async event=>{

 if(dirty()&&!confirm('Sign out and discard your unsaved changes?'))return;

 event.target.disabled=true;

 try{const response=await fetch('/auth/logout',{method:'POST',headers:{'X-CSRF-TOKEN':await csrf()}});if(!response.ok)throw new Error('Sign-out failed. Try again.');saved=null;location.replace('/');}

 catch(error){$('#sessionMessage').textContent=error.message;event.target.disabled=false;}

});

window.addEventListener('beforeunload',event=>{if(dirty()){event.preventDefault();event.returnValue='';}});

window.addEventListener('hashchange',showSection);

window.addEventListener('pageshow',event=>{if(event.persisted)location.reload();});

loadAccount();



async function loadSteam(){

 $('#retrySteam').hidden=true;

 try {

  const response=await fetch('/api/account/steam',{cache:'no-store'});if(!response.ok)throw new Error('Could not load Steam connection.');

  const {enabled,connection}=await response.json();const linked=!!connection.steamId;

  $('#steamState').textContent=linked?'Connected':'Not connected';$('#steamState').classList.toggle('connected',linked);

  

  $('#linkSteam').hidden=linked;$('#linkSteam').disabled=!enabled;$('#unlinkSteam').hidden=!linked;$('#unlinkSteam').disabled=false;



  if(connection.status==='conflict')$('#steamMessage').textContent='Game identity conflict — contact a developer to review it.';
  if(!enabled&&!linked)$('#steamMessage').textContent='Steam linking is not configured on this website yet.';

 }catch(error){$('#steamMessage').textContent=error.message;$('#retrySteam').hidden=false;$('#linkSteam').disabled=true;}

}

$('#retrySteam').addEventListener('click',loadSteam);

$('#linkSteam').addEventListener('click',async()=>{

 if(dirty()&&!confirm('Leave this page without saving your changes?'))return;

 $('#linkSteam').disabled=true;$('#steamMessage').textContent='Opening Steam...';

 try {const response=await fetch('/api/account/steam/link',{method:'POST',headers:{'X-CSRF-TOKEN':await csrf()}});

 if(!response.ok)throw new Error('Could not start Steam linking. Use the configured website address and try again.');

 const data=await response.json();saved=null;location.assign(data.url);

 }catch(error){$('#steamMessage').textContent=error.message;$('#linkSteam').disabled=false;}

});

$('#unlinkSteam').addEventListener('click',async()=>{

 if(!confirm('Disconnect Steam? Your game history is kept, but will no longer be linked to this website account.'))return;

 $('#unlinkSteam').disabled=true;

 try{const response=await fetch('/api/account/steam',{method:'DELETE',headers:{'X-CSRF-TOKEN':await csrf()}});if(!response.ok)throw new Error('Could not disconnect Steam.');$('#steamMessage').textContent='Steam disconnected.';await loadSteam();}

 catch(error){$('#steamMessage').textContent=error.message;$('#unlinkSteam').disabled=false;}

});

const steamResult=new URLSearchParams(location.search).get('steam');

if(steamResult){$('#steamMessage').textContent=({linked:'Steam connected.',cancelled:'Steam linking cancelled.',conflict:'That Steam account is already linked, or this account already has a different Steam account. No connections were changed.',failed:'Steam could not be verified. Try linking again.'})[steamResult]||'';history.replaceState(null,'','/account.html#connections');}


async function loadRoles(){
 const list=$('#roleList');list.textContent='Loading roles...';$('#retryRoles').hidden=true;
 try{
  const response=await fetch('/api/account/roles',{cache:'no-store'});
  if(!response.ok)throw Error('Could not load your roles. Try again.');
  const data=await response.json();
  if(data.status==='unavailable')throw Error('Discord roles are temporarily unavailable.');
  list.replaceChildren();
  if(data.status==='not_member'||!data.roles.length){list.textContent=data.status==='not_member'?'You are not a member of our Discord server.':'No assigned roles.';return;}
  for(const role of data.roles){
   const row=document.createElement('article');row.className='account-role';
   const title=document.createElement('h3');const dot=document.createElement('span');dot.className='role-dot';dot.setAttribute('aria-hidden','true');
   if(Number.isInteger(role.color)&&role.color>0&&role.color<=0xffffff)dot.style.backgroundColor='#'+role.color.toString(16).padStart(6,'0');
   title.append(dot,document.createTextNode(role.name));
   const access=document.createElement('p');access.textContent=role.access.length?role.access.join(' · '):'No additional website access';
   row.append(title,access);list.append(row);
  }
 }catch(error){list.textContent=error.message;$('#retryRoles').hidden=false;}
}
$('#retryRoles').addEventListener('click',loadRoles);

async function loadPatreon(){
 $('#retryPatreon').hidden=true;
 try{
  const response=await fetch('/api/account/patreon',{cache:'no-store'});if(!response.ok)throw Error('Could not load Patreon connection.');
  const {enabled,connection}=await response.json();
  $('#patreonState').textContent=connection.connected?'Connected':enabled?'Not connected':'Not configured';
  $('#patreonState').classList.toggle('connected',connection.connected);
  $('#linkPatreon').hidden=connection.connected;$('#linkPatreon').disabled=!enabled;
  $('#unlinkPatreon').hidden=!connection.connected;$('#unlinkPatreon').disabled=false;
  $('#patreonSupport').textContent=connection.connected?({supporting:'Active supporter',not_supporting:'Not currently supporting',payment_issue:'Payment issue',unverified:'Support status unavailable'})[connection.supportStatus]||'Support status unavailable':'';
 }catch(error){$('#patreonMessage').textContent=error.message;$('#retryPatreon').hidden=false;$('#linkPatreon').disabled=true;}
}
$('#retryPatreon').addEventListener('click',loadPatreon);
$('#linkPatreon').addEventListener('click',async()=>{
 if(dirty()&&!confirm('Leave this page without saving your changes?'))return;
 $('#linkPatreon').disabled=true;$('#patreonMessage').textContent='Opening Patreon...';
 try{const response=await fetch('/api/account/patreon/link',{method:'POST',headers:{'X-CSRF-TOKEN':await csrf()}});if(!response.ok)throw Error('Could not start Patreon linking. Try again.');const data=await response.json();saved=null;location.assign(data.url);}
 catch(error){$('#patreonMessage').textContent=error.message;$('#linkPatreon').disabled=false;}
});
$('#unlinkPatreon').addEventListener('click',async()=>{
 if(!confirm('Disconnect Patreon from this account? This does not cancel your Patreon membership.'))return;
 $('#unlinkPatreon').disabled=true;
 try{const response=await fetch('/api/account/patreon',{method:'DELETE',headers:{'X-CSRF-TOKEN':await csrf()}});if(!response.ok)throw Error('Could not disconnect Patreon.');$('#patreonMessage').textContent='Patreon disconnected.';await loadPatreon();}
 catch(error){$('#patreonMessage').textContent=error.message;$('#unlinkPatreon').disabled=false;}
});
const patreonResult=new URLSearchParams(location.search).get('patreon');
if(patreonResult){$('#patreonMessage').textContent=({linked:'Patreon connected.',cancelled:'Patreon linking cancelled.',conflict:'This Patreon account is already linked, or you already have a different Patreon connection.',failed:'Could not verify Patreon. Try again.'})[patreonResult]||'';history.replaceState(null,'','/account.html#connections');}

let donationPrivacy=null,donationPrivacyBusy=false;
function syncDonationPrivacy(){const anonymous=$('#anonymousDonations').checked;$('#showDonationAmount').disabled=anonymous;if(anonymous)$('#showDonationAmount').checked=false;}
function fillDonationPrivacy(value){donationPrivacy=value;$('#anonymousDonations').checked=value.anonymous;$('#showDonationAmount').checked=value.showAmount;syncDonationPrivacy();}
function donationNotice(message){const status=$('#donationPrivacyStatus');status.textContent=message;status.hidden=!message;}
async function readDonationPrivacy(response){
 if(!response.ok)throw Error('Request failed');
 const value=await response.json();
 if(typeof value?.anonymous!=='boolean'||typeof value?.showAmount!=='boolean')throw Error('Invalid settings');
 return value;
}
async function loadDonationPrivacy(){
 if(donationPrivacyBusy)return;donationPrivacyBusy=true;$('#donationPrivacyFields').disabled=true;donationNotice('');
 try{fillDonationPrivacy(await readDonationPrivacy(await fetch('/api/account/donations',{cache:'no-store'})));$('#donationPrivacyFields').disabled=false;}
 catch{donationNotice('Settings unavailable. Refresh to try again.');}
 finally{donationPrivacyBusy=false;}
}
async function saveDonationPrivacy(){
 if(donationPrivacyBusy||!donationPrivacy)return;
 syncDonationPrivacy();const value={anonymous:$('#anonymousDonations').checked,showAmount:$('#showDonationAmount').checked};
 donationPrivacyBusy=true;$('#donationPrivacyFields').disabled=true;donationNotice('');
 try{
  const response=await fetch('/api/account/donations',{method:'PUT',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':await csrf()},body:JSON.stringify(value)});
  fillDonationPrivacy(await readDonationPrivacy(response));$('#donationPrivacyFields').disabled=false;donationNotice('');
 }catch{
  // A lost response may follow a successful write. Read the saved state before re-enabling controls.
  try{fillDonationPrivacy(await readDonationPrivacy(await fetch('/api/account/donations',{cache:'no-store'})));$('#donationPrivacyFields').disabled=false;donationNotice(donationPrivacy.anonymous===value.anonymous&&donationPrivacy.showAmount===value.showAmount?'':'Couldn’t save. Try again.');}
  catch{fillDonationPrivacy(donationPrivacy);donationNotice('Couldn’t confirm the change. Refresh to check.');}
 }finally{donationPrivacyBusy=false;}
}
$('#anonymousDonations').addEventListener('change',saveDonationPrivacy);
$('#showDonationAmount').addEventListener('change',saveDonationPrivacy);
