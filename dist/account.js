const status = document.querySelector('#status');
async function loadAccount(){
 try{
  const response=await fetch('/api/me',{cache:'no-store'});
  if(response.status===401){
   const configResponse=await fetch('/api/auth/status',{cache:'no-store'});
   if(!configResponse.ok)throw new Error('Sign-in is temporarily unavailable.');
   const config=await configResponse.json();
   status.textContent=!config.discordEnabled?'Discord sign-in has not been configured on this server yet.':!config.https?'Open the HTTPS version of this website to sign in.':'Sign in with Discord to create or return to your website account.';
   document.querySelector('#signIn').hidden=!(config.discordEnabled&&config.https);return;
  }
  if(!response.ok)throw new Error('Your account could not be loaded. Please try again later.');
  const me=await response.json();
  status.textContent='You’re signed in.';
  document.querySelector('#discordName').textContent=me.name;
  document.querySelector('#discordId').textContent=me.discordId;
  document.querySelector('#accountId').textContent=me.id;
  document.querySelector('#adminLink').hidden=!me.isAdmin;
  document.querySelector('#profile').hidden=false;
 }catch(error){status.textContent=error.message;}
}
document.querySelector('#signOut').addEventListener('click',async event=>{
 event.target.disabled=true;
 try{
  const csrf=await fetch('/api/auth/csrf',{cache:'no-store'});
  if(!csrf.ok)throw new Error('Please reload the page and try again.');
  const {token}=await csrf.json();
  const result=await fetch('/auth/logout',{method:'POST',headers:{'X-CSRF-TOKEN':token}});
  if(!result.ok)throw new Error('Sign-out failed. Please try again.');
  location.reload();
 }catch(error){status.textContent=error.message;event.target.disabled=false;}
});
loadAccount();
