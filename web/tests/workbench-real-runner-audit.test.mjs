import assert from 'node:assert/strict';
import { lstat, mkdtemp, readFile, realpath, rmdir, unlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';
import { auditOwnedProcessSnapshot, discoverOwnedProcessIdentities, formatOwnedProcessFailure, liveOwnedProcessIdentities, writeOwnedProcessEvents } from '../e2e/run-workbench-real.mjs';

const root = () => ({ pid: 101, parentPid: 100, created: '2026-10-06T06:00:00.000Z', commandLine: 'owned-server --isolated-task' });
const child = () => ({ pid: 102, parentPid: 101, created: '2026-10-06T06:00:00.001Z', commandLine: 'owned-console-host' });
const ownership = () => {
  const parent = root(); const descendant = child();
  const snapshot = [parent, descendant, { pid: 100, parentPid: 0, created: '2026-10-06T05:59:00.000Z', commandLine: 'task-runner ' + 'long-command-'.repeat(300) }];
  return { snapshot, identities: new Map([[parent.pid, parent]]), roots: [{ pid: parent.pid, exitedAtUtc: null }] };
};

async function evidence(action) {
  const prefix = path.resolve(tmpdir(), 'sonnetdb-workbench-owned-audit-');
  const folder = await mkdtemp(prefix);
  const absolute = await realpath(folder);
  assert.equal(absolute.toLowerCase(), path.resolve(folder).toLowerCase());
  assert.ok(path.basename(absolute).startsWith('sonnetdb-workbench-owned-audit-'));
  assert.equal(path.dirname(absolute).toLowerCase(), path.resolve(tmpdir()).toLowerCase());
  assert.equal((await lstat(folder)).isSymbolicLink(), false);
  const file = path.join(absolute, 'process-identities.jsonl');
  try { await action(file); }
  finally {
    // 两个确切的本测试对象；不递归删除临时目录或其他测试证据。
    await unlink(file).catch((error) => { if (error.code !== 'ENOENT') throw error; });
    await rmdir(absolute);
  }
}

test('slow identity logging does not consume the three-second traversal budget and keeps full disk parent chains', { timeout: 6000 }, async () => {
  await evidence(async (file) => {
    const state = ownership();
    const discovered = discoverOwnedProcessIdentities(state.snapshot, state.identities, state.roots);
    assert.equal(discovered.length, 1);
    assert.equal(state.identities.has(102), true);
    const summary = await writeOwnedProcessEvents(file, discovered.map((identity) => ({ event: 'descendant-discovered', ...identity })), {
      append: async (...args) => { await delay(3100, undefined, { signal: args[2].signal }); await writeFile(...args); },
    });
    assert.equal(discoverOwnedProcessIdentities(state.snapshot, state.identities, state.roots).length, 0);
    const saved = JSON.parse((await readFile(file, 'utf8')).trim());
    assert.equal(saved.pid, 102); assert.equal(saved.created, child().created); assert.equal(saved.commandLine, child().commandLine);
    assert.equal(saved.parentChain[0].pid, 101); assert.ok(saved.parentChain[1].commandLine.length > 3000);
    assert.ok(summary.length < 200); assert.ok(!summary.includes('commandLine')); assert.ok(!summary.includes('long-command'));
  });
});

test('genuine traversal deadline exhaustion still rejects before adopting an unchecked process', { timeout: 2000 }, () => {
  const state = ownership(); const times = [0, 0, 3000]; let index = 0;
  assert.throws(() => discoverOwnedProcessIdentities(state.snapshot, state.identities, state.roots, () => times[Math.min(index++, times.length - 1)]), /traversal exceeded three seconds/u);
  assert.equal(state.identities.has(102), false);
});

test('log failure rejects success while already-registered identities remain available for exact cleanup verification', { timeout: 2000 }, async () => {
  await evidence(async (file) => {
    const state = ownership(); const discovered = discoverOwnedProcessIdentities(state.snapshot, state.identities, state.roots);
    await assert.rejects(writeOwnedProcessEvents(file, discovered.map((identity) => ({ event: 'descendant-discovered', ...identity })), {
      append: async () => { throw Object.assign(new Error('owned log write EACCES'), { code: 'EACCES' }); },
    }), /EACCES/u);
    const candidates = liveOwnedProcessIdentities(state.snapshot, state.identities);
    assert.deepEqual(candidates.map((identity) => identity.pid), [101, 102]);
    assert.equal(candidates[1].commandLine, child().commandLine); assert.equal(candidates[1].parentChain[0].pid, 101);
  });
});

test('PID reuse rejects cleanup and cannot make a replacement parent authorize a new descendant', { timeout: 2000 }, () => {
  const state = ownership(); const replacement = { ...root(), created: '2026-10-06T06:01:00.000Z', commandLine: 'unrelated-user-process' };
  const snapshot = [replacement, { ...child(), created: '2026-10-06T06:01:00.001Z' }];
  assert.equal(discoverOwnedProcessIdentities(snapshot, state.identities, state.roots).length, 0);
  assert.equal(state.identities.has(102), false);
  assert.throws(() => liveOwnedProcessIdentities(snapshot, state.identities), /was reused; preserve the replacement process/u);
  assert.equal(state.identities.get(101).commandLine, root().commandLine);
});

test('normal logging obeys cancellation while final cleanup logging has an independent bounded path', { timeout: 2000 }, async () => {
  await evidence(async (file) => {
    const cancellation = new AbortController(); cancellation.abort(new Error('task was cancelled'));
    const values = [{ event: 'owned-final-audit', ...root(), parentChain: [{ pid: 100, unavailable: true }] }];
    await assert.rejects(writeOwnedProcessEvents(file, values, { signal: cancellation.signal }), /task was cancelled/u);
    const summary = await writeOwnedProcessEvents(file, values, { signal: cancellation.signal, final: true });
    assert.equal(JSON.parse(summary).events, 1);
    assert.equal(JSON.parse((await readFile(file, 'utf8')).trim()).parentChain[0].pid, 100);
  });
});

test('an in-flight log cancellation rejects promptly without losing the independently recorded ownership ledger', { timeout: 2000 }, async () => {
  await evidence(async (file) => {
    const state = ownership(); const discovered = discoverOwnedProcessIdentities(state.snapshot, state.identities, state.roots);
    const cancellation = new AbortController();
    const pending = writeOwnedProcessEvents(file, discovered.map((identity) => ({ event: 'descendant-discovered', ...identity })), {
      signal: cancellation.signal, append: async () => new Promise(() => {}),
    });
    cancellation.abort(new Error('cancel pending log'));
    await assert.rejects(pending, /cancel pending log/u);
    assert.equal(state.identities.has(102), true); assert.equal(liveOwnedProcessIdentities(state.snapshot, state.identities).length, 2);
  });
});

test('partially discovered identities persist at discovery time after a real traversal deadline failure', { timeout: 2000 }, async () => {
  await evidence(async (file) => {
    const state = ownership(); const start = Date.parse('2026-10-06T06:00:10.000Z'); let clock = start;
    class AdvanceAfterRegistration extends Map {
      set(pid, identity) { super.set(pid, identity); if (pid === 102) clock += 3000; return this; }
    }
    const identities = new AdvanceAfterRegistration([[101, root()]]);
    await assert.rejects(auditOwnedProcessSnapshot(state.snapshot, identities, state.roots, file, { clock: () => clock }), /traversal exceeded three seconds/u);
    assert.equal(identities.has(102), true);
    const saved = JSON.parse((await readFile(file, 'utf8')).trim());
    assert.equal(saved.pid, 102); assert.equal(saved.timestampUtc, new Date(start).toISOString()); assert.equal(saved.discoveredAtUtc, saved.timestampUtc);
    assert.equal(saved.parentChain[0].pid, 101); assert.equal(liveOwnedProcessIdentities(state.snapshot, identities).length, 2);
  });
});

test('partial traversal and subsequent disk failure preserve both causes and the cleanup identities', { timeout: 2000 }, async () => {
  await evidence(async (file) => {
    const state = ownership(); let clock = 0;
    class AdvanceAfterRegistration extends Map {
      set(pid, identity) { super.set(pid, identity); if (pid === 102) clock += 3000; return this; }
    }
    const identities = new AdvanceAfterRegistration([[101, root()]]);
    await assert.rejects(auditOwnedProcessSnapshot(state.snapshot, identities, state.roots, file, {
      clock: () => clock, append: async () => { throw new Error('disk-write-failed'); },
    }), (error) => error instanceof AggregateError && error.errors.length === 2
      && /traversal exceeded/u.test(error.errors[0].message) && error.errors[1].message === 'disk-write-failed');
    assert.equal(identities.has(102), true); assert.equal(liveOwnedProcessIdentities(state.snapshot, identities).length, 2);
  });
});

test('top-level failed event persists both concrete safe causes without stack, argv or credentials', { timeout: 2000 }, async () => {
  await evidence(async (file) => {
    const state = ownership(); let clock = 0;
    class AdvanceAfterRegistration extends Map {
      set(pid, identity) { super.set(pid, identity); if (pid === 102) clock += 3000; return this; }
    }
    let failure;
    try {
      await auditOwnedProcessSnapshot(state.snapshot, new AdvanceAfterRegistration([[101, root()]]), state.roots, file, {
        clock: () => clock, append: async () => { throw Object.assign(new Error('credential-secret argv-secret stack-secret'), { code: 'EACCES' }); },
      });
    } catch (error) { failure = error; }
    assert.ok(failure instanceof AggregateError);
    // 与真实 runner 顶层 catch 复用同一格式化和最终落盘路径。
    await writeOwnedProcessEvents(file, [{ event: 'workbench-failed', ...formatOwnedProcessFailure(failure) }], { final: true });
    const text = (await readFile(file, 'utf8')).trim(); const saved = JSON.parse(text);
    assert.equal(saved.causes.length, 2);
    assert.equal(saved.causes[0].message, 'Owned process traversal exceeded three seconds.');
    assert.equal(saved.causes[1].message, 'Owned identity logging failed (EACCES).');
    assert.ok(saved.causes.every((cause) => cause.message.length <= 256));
    assert.ok(!text.includes('credential-secret')); assert.ok(!text.includes('argv-secret')); assert.ok(!text.includes('stack-secret'));
  });
});

test('failed-event cause budgets reject unknown detail and retain no more than two reasons', { timeout: 2000 }, () => {
  const fields = formatOwnedProcessFailure(new AggregateError([
    new Error('token=password credential-secret ' + 'x'.repeat(1000)),
    Object.assign(new Error('argv-secret'), { code: 'ENOSPC' }),
    new Error('third-secret'),
  ], 'stack-secret'));
  assert.equal(fields.causes.length, 2); assert.ok(fields.message.length <= 256);
  assert.equal(fields.causes[1].message, 'Owned identity logging failed (ENOSPC).');
  assert.ok(!JSON.stringify(fields).includes('secret')); assert.ok(!JSON.stringify(fields).includes('password'));
});
