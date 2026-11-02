const status=document.querySelector('#status');
let selected=null, cursor=0, activeRange=null;
async function get(path){
 const response=await fetch(path,{cache:'no-store'});
 if(response.status===401)throw new Error('Your session has ended. Sign in from Your account.');
 if(response.status===403)throw new Error('Your account does not have access to this inspector.');
 if(!response.ok)throw new Error(`Data is unavailable (${response.status}).`);
 return response.json();
}
function cell(row,value){const td=document.createElement('td');td.textContent=value??'—';row.append(td);return td;}
async function refresh(){
 try{
  const data=await get('/api/admin/data');
  document.querySelector('#scope').textContent=data.scope;
  const streams=document.querySelector('#streams');streams.replaceChildren();
  for(const item of data.streams){const row=document.createElement('tr');cell(row,item.stream);cell(row,item.status);cell(row,item.batches);cell(row,new Date(item.lastReceived).toLocaleString());streams.append(row);}
  const batches=document.querySelector('#batches');batches.replaceChildren();
  for(const item of data.recent){
   const row=document.createElement('tr');cell(row,new Date(item.receivedAt).toLocaleString());cell(row,`${item.stream} / ${item.recordingId}`);cell(row,item.revision);cell(row,item.error?`${item.status}: ${item.error}`:item.status);
   const action=cell(row,'');if(item.stream==='telemetry'){const button=document.createElement('button');button.textContent='Open';button.addEventListener('click',()=>openRecording(item));action.append(button);}else action.textContent='Archive only';batches.append(row);
  }
  status.textContent=data.recent.length?'Updated. No data is changed by this page.':'No batches have been received by this website database yet.';
 }catch(error){status.textContent=error.message;document.querySelector('#streams').replaceChildren();document.querySelector('#batches').replaceChildren();document.querySelector('#recording').hidden=true;}
}
async function openRecording(item){
 try{
  const route=`/api/admin/recordings/${item.serverId}/${item.recordingId}`;
  const summary=await get(route);selected=route;cursor=0;activeRange=null;
  document.querySelector('#summary').textContent=JSON.stringify(summary,null,2);
  document.querySelector('#from').value=summary.fromMs??0;
  document.querySelector('#until').value=Math.min(summary.untilMs??1000,(summary.fromMs??0)+1000);
  document.querySelector('#records').textContent='Choose a window to inspect its records.';
  document.querySelector('#next').hidden=true;document.querySelector('#recording').hidden=false;
  document.querySelector('#recording').scrollIntoView({block:'start'});
 }catch(error){status.textContent=error.message;}
}
async function readRecords(){
 document.querySelector('#next').disabled=true;
 try{
  const result=await get(`${selected}/records?fromMs=${encodeURIComponent(activeRange.from)}&untilMs=${encodeURIComponent(activeRange.until)}&afterSequence=${cursor}`);
  document.querySelector('#records').textContent=JSON.stringify(result,null,2);
  cursor=result.nextAfterSequence??0;document.querySelector('#next').hidden=!cursor;
 }catch(error){status.textContent=error.message;document.querySelector('#records').textContent='';document.querySelector('#next').hidden=true;}
 finally{document.querySelector('#next').disabled=false;}
}
document.querySelector('#refresh').addEventListener('click',refresh);
document.querySelector('#range').addEventListener('submit',event=>{event.preventDefault();cursor=0;activeRange={from:Number(document.querySelector('#from').value),until:Number(document.querySelector('#until').value)};readRecords();});
document.querySelector('#next').addEventListener('click',readRecords);
refresh();
