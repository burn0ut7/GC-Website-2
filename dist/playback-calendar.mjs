export function dayKey(date) {
  return `${date.getFullYear()}-${String(date.getMonth()+1).padStart(2,"0")}-${String(date.getDate()).padStart(2,"0")}`;
}
export function dayQuery(value) {
  if (!value) return "";
  const [y,m,d] = value.split("-").map(Number);
  return `&startedFrom=${new Date(y,m-1,d).getTime()/1000}&startedUntil=${new Date(y,m-1,d+1).getTime()/1000}`;
}
export function recordingCalendar(root, changed) {
  const trigger = document.getElementById('calendarToggle');
  function position() {
    const box = trigger.getBoundingClientRect();
    const width = root.offsetWidth, height = root.offsetHeight;
    root.style.left = `${Math.max(8, Math.min(box.left, innerWidth-width-8))}px`;
    root.style.top = `${Math.max(8, Math.min(box.bottom+6, innerHeight-height-8))}px`;
  }
  root.addEventListener('toggle', () => {
    if (root.matches(':popover-open')) {
      position();
      (root.querySelector('[data-day][aria-pressed="true"]') ?? root.querySelector('[data-day]'))?.focus();
    }
  });
  window.addEventListener('resize', () => {if(root.matches(':popover-open'))position();});
  document.addEventListener('scroll', () => {if(root.matches(':popover-open'))position();}, true);
  function choose() {
    root.hidePopover();
    trigger.focus();
    changed();
  }
  let value = "", month = new Date(), busy = false;
  root.innerHTML = `<div class="calendar-heading"><button type="button" aria-label="Previous month">‹</button><span aria-live="polite"></span><button type="button" aria-label="Next month">›</button></div><div class="calendar-week" aria-hidden="true">${["M","T","W","T","F","S","S"].map(d=>`<span>${d}</span>`).join("")}</div><div class="calendar-days" role="group" aria-label="Choose recording day"></div><button type="button" class="calendar-all">All dates</button>`;
  const heading = root.querySelector('.calendar-heading'), days = root.querySelector('.calendar-days'), all = root.querySelector('.calendar-all');
  function render() {
    heading.querySelector('span').textContent = month.toLocaleDateString(undefined,{month:"long",year:"numeric"});
    days.replaceChildren();
    const y=month.getFullYear(), m=month.getMonth(), offset=(new Date(y,m,1).getDay()+6)%7;
    for(let i=0;i<offset;i++) days.append(document.createElement('span'));
    for(let d=1;d<=new Date(y,m+1,0).getDate();d++) {
      const date=new Date(y,m,d), key=dayKey(date), button=document.createElement('button');
      button.type='button';button.textContent=d;button.dataset.day=key;
      button.setAttribute('aria-label',date.toLocaleDateString(undefined,{weekday:'long',year:'numeric',month:'long',day:'numeric'}));
      button.setAttribute('aria-pressed',String(value===key));
      button.onclick=()=>{value=key;render();choose();};
      days.append(button);
    }
    all.setAttribute('aria-pressed',String(!value));
    root.querySelectorAll('button').forEach(button=>button.disabled=busy);
    if (root.matches(':popover-open')) position();
  }
  heading.firstElementChild.onclick=()=>{month=new Date(month.getFullYear(),month.getMonth()-1,1);render();};
  heading.lastElementChild.onclick=()=>{month=new Date(month.getFullYear(),month.getMonth()+1,1);render();};
  all.onclick=()=>{value="";render();choose();};
  function select(key) {
    value=key;
    if(key) {const [y,m,d]=key.split('-').map(Number);month=new Date(y,m-1,d);}
    render();
  }
  render();
  return {get value(){return value;},select,query:()=>dayQuery(value),selectTimestamp(seconds){const date=new Date(seconds*1000);if(Number.isFinite(date.getTime()))select(dayKey(date));},setBusy(on){busy=on;root.querySelectorAll('button').forEach(button=>button.disabled=on);}};
}
