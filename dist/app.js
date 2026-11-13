const app=document.querySelector('#app');
function shell(content){
 return `<div class="field-shell"><header class="quiet-header"><a href="#home" class="quiet-brand">Global Conflicts</a></header><main>${content}</main><footer><span>Global Conflicts</span><a href="/account.html">Account</a><a href="https://discord.globalconflicts.net" target="_blank" rel="noopener">Discord ↗</a></footer></div>`;
}
async function loadAccountPreferences(){
 const shell=document.querySelector('.field-shell');
 if(!shell)return;
 window.gcPreferences=undefined;
 updateSessionTimes();
 try{
  const response=await fetch('/api/me',{cache:'no-store'});
  if(!shell.isConnected||!response.ok)return;
  const settings=await fetch('/api/account/settings',{cache:'no-store'});
  if(settings.ok&&shell.isConnected){window.gcPreferences=await settings.json();updateSessionTimes();}
 }catch{/* Keep default preferences when the account is unavailable. */}
}
let youtubeApiPromise;
let youtubePlayers=[];
function loadYouTubeApi(){
 if(window.YT?.Player)return Promise.resolve(window.YT);
 if(!youtubeApiPromise)youtubeApiPromise=new Promise((resolve,reject)=>{
  window.onYouTubeIframeAPIReady=()=>resolve(window.YT);
  const script=document.createElement('script');
  script.src='https://www.youtube.com/iframe_api';script.async=true;
  script.onerror=()=>{youtubeApiPromise=null;script.remove();reject(new Error('YouTube API unavailable'))};
  document.head.append(script);
 });
 return youtubeApiPromise;
}
function setVideoStartingVolume(iframe){
 loadYouTubeApi().then(YT=>{
  if(!iframe?.isConnected)return;
  const player=new YT.Player(iframe,{events:{onReady:event=>{
   if(!iframe.isConnected)return;
   event.target.setVolume(window.gcPreferences?.videoVolume??25);
  }}});
  youtubePlayers.push(player);
 }).catch(()=>{/* The native player remains usable. */});
}
function render(){
 youtubePlayers.forEach(player=>player.destroy());youtubePlayers=[];
 if(location.hash!=='#home')history.replaceState(null,'','#home');
 document.body.className='field page-home';
 document.title='Global Conflicts — One-life Arma Reforger';
 app.innerHTML=shell(home());
 window.mountNavigation?.();
 wireLanding();
 loadAccountPreferences();
 updateSessionTimes();
 window.scrollTo(0,0);
}
function home(){
 return `<section class="field-opening"><div class="opening-copy"><h1>One life.<br><span>Arma Reforger.</span></h1><p>An international Arma community with 15+ years of tactical teamwork and good times—no ranks, no attendance requirements, just join and play.</p><p class="opening-time"><span data-session-days></span> · <span data-session-time></span></p><a class="btn discord-button" href="https://discord.globalconflicts.net" target="_blank" rel="noopener"><img class="discord-icon" src="discord.svg" alt="" width="20" height="20">Join us on Discord ↗</a></div><figure class="opening-media"><img src="squad.jpg" alt="Two soldiers advancing together beside a building, from the Global Conflicts community archive" fetchpriority="high"></figure><button class="scroll-invitation" id="exploreBelow">How to play <span aria-hidden="true">↓</span></button></section><section id="howWePlay" class="field-story"><div class="story-video" id="introPlayer"><button id="playIntro" aria-label="Play the Global Conflicts introduction"><img src="intro-thumbnail.jpg" alt="Global Conflicts guide to joining and playing Arma Reforger"><span class="story-play" aria-hidden="true">▶</span></button></div></section><section class="field-join-section"><div><h2>Your next<br>session starts:</h2><div class="session-countdown" data-session-countdown role="timer" aria-live="off" aria-label="Time until the next session"><span class="clock-unit" aria-hidden="true"><span class="clock-digits" data-countdown-unit="days">00</span><span class="clock-label">Days</span></span><span class="clock-separator" aria-hidden="true">:</span><span class="clock-unit" aria-hidden="true"><span class="clock-digits" data-countdown-unit="hours">00</span><span class="clock-label">Hours</span></span><span class="clock-separator" aria-hidden="true">:</span><span class="clock-unit" aria-hidden="true"><span class="clock-digits" data-countdown-unit="minutes">00</span><span class="clock-label">Minutes</span></span><span class="clock-separator" aria-hidden="true">:</span><span class="clock-unit" aria-hidden="true"><span class="clock-digits" data-countdown-unit="seconds">00</span><span class="clock-label">Seconds</span></span></div><p class="large-session-time"><time data-next-session-date></time></p></div><div class="join-copy"><p>Discord is where our community comes together to chat, ask questions, and help each other out.</p><a class="btn discord-button" href="https://discord.globalconflicts.net" target="_blank" rel="noopener"><img class="discord-icon" src="discord.svg" alt="" width="20" height="20">Join us on Discord ↗</a></div></section>`;
}
function wireLanding(){
 document.querySelector('#exploreBelow')?.addEventListener('click',()=>document.querySelector('#howWePlay').scrollIntoView({behavior:window.gcPreferences?.reduceMotion||window.matchMedia('(prefers-reduced-motion: reduce)').matches?'instant':'smooth'}));
 document.querySelector('#playIntro')?.addEventListener('click',()=>{document.querySelector('#introPlayer').innerHTML='<div class="video-embed"><iframe src="https://www.youtube-nocookie.com/embed/_SW1tEkcfRU?enablejsapi=1&amp;autoplay=0" title="Global Conflicts Guide: How to Join and Play Arma Reforger With Us" allow="accelerometer; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share" referrerpolicy="strict-origin-when-cross-origin" allowfullscreen></iframe></div>';setVideoStartingVolume(document.querySelector('#introPlayer iframe'))})
}
window.addEventListener('hashchange',render);render();

setInterval(updateSessionTimes, 60000);
setInterval(()=>{if(!document.hidden)updateSessionCountdown();}, 1000);
document.addEventListener('visibilitychange',()=>{if(!document.hidden)updateSessionTimes()});
window.addEventListener('pageshow',event=>{if(event.persisted)loadAccountPreferences()});
