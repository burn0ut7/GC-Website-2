import test from "node:test";
import assert from "node:assert/strict";
import { recordingWindows } from "../dist/playback-loader.mjs";
test("first complete window is playable before later requests; boundaries deduplicate", async () => {
  let calls = 0;
  const signal = new AbortController().signal;
  const get = async (url) => {
    calls++;
    const start = Number(
      new URL(url, "http://test").searchParams.get("fromMs"),
    );
    return {
      records:
        start === 0
          ? [{ sequence: 1, timeMs: 10000 }]
          : [
              { sequence: 1, timeMs: 10000 },
              { sequence: 2, timeMs: 20000 },
            ],
      nextAfterSequence: null,
    };
  };
  const stream = recordingWindows(
    { fromMs: 0, untilMs: 20000, recordCount: 2 },
    "/records",
    get,
    signal,
  );
  const first = await stream.next();
  assert.equal(first.value.until, 10000);
  assert.equal(calls, 1);
  assert.equal(first.value.complete, false);
  const second = await stream.next();
  assert.equal(second.value.rows.length, 2);
  assert.equal(second.value.complete, true);
  assert.equal((await stream.next()).done, true);
});
test("stalled pagination and missing records fail explicitly", async () => {
  const signal = new AbortController().signal;
  const info = { fromMs: 0, untilMs: 1000, recordCount: 1 };
  await assert.rejects(async () => {
    for await (const p of recordingWindows(
      info,
      "/",
      async () => ({ records: [], nextAfterSequence: 0 }),
      signal,
    )) {
    }
  }, /cursor/);
  await assert.rejects(async () => {
    for await (const p of recordingWindows(
      info,
      "/",
      async () => ({ records: [], nextAfterSequence: null }),
      signal,
    )) {
    }
  }, /missing/);
});
test("cancelled selections publish no data", async () => {
  const abort = new AbortController();
  const get = async () => {
    abort.abort();
    return { records: [], nextAfterSequence: null };
  };
  const stream = recordingWindows(
    { fromMs: 0, untilMs: 1000, recordCount: 0 },
    "/",
    get,
    abort.signal,
  );
  assert.equal((await stream.next()).done, true);
});
