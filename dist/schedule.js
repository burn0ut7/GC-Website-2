// GC sessions follow Central European civil time, including daylight saving.
const GC_SCHEDULE = Object.freeze({ timeZone: 'Europe/Berlin', hour: 21, weekdays: [6, 0] });

function scheduleParts(date, timeZone) {
 const parts = new Intl.DateTimeFormat('en-GB', {
  timeZone, year: 'numeric', month: '2-digit', day: '2-digit',
  hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23'
 }).formatToParts(date);
 return Object.fromEntries(parts.filter(p => p.type !== 'literal').map(p => [p.type, Number(p.value)]));
}

function sessionInstant(calendarDate) {
 const wall = Date.UTC(calendarDate.getUTCFullYear(), calendarDate.getUTCMonth(), calendarDate.getUTCDate(), GC_SCHEDULE.hour);
 let instant = wall;
 // Resolve the offset on the session date, rather than using today's DST offset.
 for (let attempt = 0; attempt < 3; attempt++) {
  const p = scheduleParts(new Date(instant), GC_SCHEDULE.timeZone);
  const represented = Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, p.second);
  instant += wall - represented;
 }
 return new Date(instant);
}

function upcomingSessions(now = new Date()) {
 const p = scheduleParts(now, GC_SCHEDULE.timeZone);
 const today = Date.UTC(p.year, p.month - 1, p.day);
 return GC_SCHEDULE.weekdays.map(weekday => {
  for (let offset = 0; offset <= 7; offset++) {
   const day = new Date(today + offset * 86400000);
   if (day.getUTCDay() !== weekday) continue;
   const instant = sessionInstant(day);
   if (instant >= now) return instant;
  }
 }).sort((a, b) => a - b);
}

function sessionDisplay(now = new Date(), zone) {
 const sessions = upcomingSessions(now);
 let detected = zone;
 try {
  if (detected === undefined) detected = Intl.DateTimeFormat().resolvedOptions().timeZone;
  if (!detected) throw new Error('No local timezone');
  new Intl.DateTimeFormat('en', { timeZone: detected }).format(now);
 } catch { detected = null; }
 const timeZone = detected || 'UTC';
 const dayFormat = new Intl.DateTimeFormat('en', { timeZone, weekday: 'long' });
 const timeFormat = new Intl.DateTimeFormat(undefined, { timeZone, hour: 'numeric', minute: '2-digit', ...(detected ? { timeZoneName: 'short' } : {}) });
 const rows = sessions.map(date => ({ day: dayFormat.format(date), time: timeFormat.format(date) }));
 const sameTime = rows[0].time === rows[1].time;
 return {
  days: sameTime ? rows.map(row => row.day).join(' & ') : 'Upcoming sessions',
  time: (sameTime ? rows[0].time : rows.map(row => `${row.day} ${row.time}`).join(' · ')) + (detected ? '' : ' UTC'),
  detail: sessions.map(date => new Intl.DateTimeFormat(undefined, {
   timeZone, dateStyle: 'full', timeStyle: 'short'
  }).format(date)).join(' / ')
 };
}

function updateSessionTimes() {
 const display = sessionDisplay();
 document.querySelectorAll('[data-session-days]').forEach(element => { element.textContent = display.days; });
 document.querySelectorAll('[data-session-time]').forEach(element => { element.textContent = display.time; element.title = display.detail; });
}
