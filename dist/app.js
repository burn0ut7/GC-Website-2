const app=document.querySelector('#app');
function shell(content){
 return `<div class="field-shell"><header class="quiet-header"><a href="#home" class="quiet-brand">Global Conflicts</a></header><main>${content}</main><footer><span>Global Conflicts</span><a href="/account.html">Your account</a><a href="https://discord.globalconflicts.net" target="_blank" rel="noopener">Discord ↗</a></footer></div>`;
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
   event.target.setVolume(25);
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
 wireLanding();
 updateSessionTimes();
 window.scrollTo(0,0);
}
function home(){
 return `<section class="field-opening"><div class="opening-copy"><h1>One life.<br><span>Arma Reforger.</span></h1><p>An international Arma community with 15+ years of tactical teamwork and good times—no ranks, no attendance requirements, just join and play when you can.</p><p class="opening-time"><span data-session-days></span> · <span data-session-time></span></p><a class="btn discord-button" href="https://discord.globalconflicts.net" target="_blank" rel="noopener"><img class="discord-icon" src="discord.svg" alt="" width="20" height="20">Join us on Discord ↗</a></div><figure class="opening-media"><img src="squad.jpg" alt="Two soldiers advancing together beside a building, from the Global Conflicts community archive" fetchpriority="high"></figure><button class="scroll-invitation" id="exploreBelow">How to play <span aria-hidden="true">↓</span></button></section><section id="howWePlay" class="field-story"><div class="story-video" id="introPlayer"><button id="playIntro" aria-label="Play the Global Conflicts introduction"><img src="intro-thumbnail.jpg" alt="Global Conflicts guide to joining and playing Arma Reforger"><span class="story-play" aria-hidden="true">▶</span></button></div></section><section class="field-join-section"><div><h2>Your next<br>session starts here.</h2><p class="large-session-time"><span data-session-days></span><br><span data-session-time></span></p></div><div class="join-copy"><p>Discord is where we meet, plan, and play. Get your server details and mods, ask questions, and find your squad.</p><p>Bring a working microphone and a mature attitude. We’ll help you get started.</p><a class="btn discord-button" href="https://discord.globalconflicts.net" target="_blank" rel="noopener"><img class="discord-icon" src="discord.svg" alt="" width="20" height="20">Join us on Discord ↗</a></div></section>`;
}
function wireLanding(){
 document.querySelector('#exploreBelow')?.addEventListener('click',()=>document.querySelector('#howWePlay').scrollIntoView({behavior:window.matchMedia('(prefers-reduced-motion: reduce)').matches?'instant':'smooth'}));
 document.querySelector('#playIntro')?.addEventListener('click',()=>{document.querySelector('#introPlayer').innerHTML='<div class="video-embed"><iframe src="https://www.youtube-nocookie.com/embed/_SW1tEkcfRU?enablejsapi=1&amp;autoplay=0" title="Global Conflicts Guide: How to Join and Play Arma Reforger With Us" allow="accelerometer; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share" referrerpolicy="strict-origin-when-cross-origin" allowfullscreen></iframe></div>';setVideoStartingVolume(document.querySelector('#introPlayer iframe'))})
}
window.addEventListener('hashchange',render);render();

setInterval(updateSessionTimes, 60000);
document.addEventListener('visibilitychange',()=>{if(!document.hidden)updateSessionTimes()});
