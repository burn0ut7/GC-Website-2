// Publish only complete time windows so seeking never crosses unloaded data.
export async function* recordingWindows(info, base, get, signal) {
  const rows = new Map();
  let bytes = 0;
  let start = info.fromMs;
  do {
    const end = Math.min(start + (rows.size ? 30000 : 10000), info.untilMs);
    let cursor = 0;
    do {
      const page = await get(
        `${base}/records?fromMs=${start}&untilMs=${end}&afterSequence=${cursor}&limit=500`,
        signal,
      );
      if (signal.aborted) return;
      bytes += JSON.stringify(page).length;
      if (bytes > 32 * 1024 * 1024)
        throw new Error("Recording exceeds the 32 MB playback limit.");
      for (const row of page.records) rows.set(row.sequence, row);
      if (rows.size > 100000)
        throw new Error("Recording exceeds the playback record limit.");
      if (page.nextAfterSequence != null && page.nextAfterSequence <= cursor)
        throw new Error(
          "The record cursor did not advance. Press Reload to retry.",
        );
      cursor = page.nextAfterSequence;
    } while (cursor != null);
    yield {
      rows: [...rows.values()],
      until: end,
      complete: end === info.untilMs,
    };
    if (end === info.untilMs) break;
    start = end;
  } while (true);
  if (rows.size !== info.recordCount)
    throw new Error("Recording has missing records. Press Reload to retry.");
}
