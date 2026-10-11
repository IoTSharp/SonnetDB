import assert from 'node:assert/strict';
import test from 'node:test';
import { compactNativeProcessEvidence, encodeNativeEvidence, NativeEvidenceError, NativeEvidenceMaxBytes, persistNativeTerminalEvidence } from './studio-native-evidence.mjs';

function memoryWriter(failName) {
  const files = new Map();
  const attempts = [];
  return { files, attempts, write: async (name, value) => {
    attempts.push(name);
    if (name === failName) throw new Error('Injected independent writer failure.');
    files.set(name, encodeNativeEvidence(value));
  } };
}

function terminalInput(writer, overrides = {}) {
  return { write: writer.write, normalExit: { normalExit: true, accepted: true, method: 'CloseMainWindow', studioExitCode: 0, studioExitSignal: null,
    studioIdentityExited: true, oldServerIdentityExited: true, newServerIdentityExited: true, allFourPortsReleased: true },
    cleanup: { cleanupProven: true, allFourPortsReleased: true, fallbackActions: [], helperReclaims: [], errors: [] }, result: { passed: true, fatal: null },
    deadline: Date.now() + 5000, ...overrides };
}

test('oversize detail leaves normal exit and cleanup and a failed result independently persisted', { timeout: 5000 }, async () => {
  const writer = memoryWriter();
  const output = await persistNativeTerminalEvidence(terminalInput(writer, { details: [{ name: 'process-events.json', value: { oversized: 'x'.repeat(NativeEvidenceMaxBytes) } }] }));
  assert.equal(output.passed, false);
  assert.equal(output.result.passed, false);
  assert.equal(JSON.parse(writer.files.get('normal-exit.json')).normalExit, true);
  assert.equal(JSON.parse(writer.files.get('cleanup.json')).cleanupProven, true);
  const result = JSON.parse(writer.files.get('result.json'));
  assert.equal(result.passed, false);
  assert.equal(result.evidenceWrites.find((item) => item.name === 'process-events.json').failure, 'oversize');
});

test('a cleanup writer failure does not suppress normal exit detail or failed result', { timeout: 5000 }, async () => {
  const writer = memoryWriter('cleanup.json');
  const output = await persistNativeTerminalEvidence(terminalInput(writer, { details: [{ name: 'bridge-responses.json', value: [] }] }));
  assert.equal(output.passed, false);
  assert.deepEqual(writer.attempts, ['normal-exit.json', 'cleanup.json', 'bridge-responses.json', 'result.json']);
  assert.equal(JSON.parse(writer.files.get('result.json')).passed, false);
  assert.equal(writer.files.has('normal-exit.json'), true);
});

test('a failed result writer retains the other essential records and cannot return PASS', { timeout: 5000 }, async () => {
  const writer = memoryWriter('result.json');
  const output = await persistNativeTerminalEvidence(terminalInput(writer));
  assert.equal(output.passed, false);
  assert.equal(output.result.passed, false);
  assert.equal(output.outcomes.at(-1).persisted, false);
  assert.equal(writer.files.has('normal-exit.json'), true);
  assert.equal(writer.files.has('cleanup.json'), true);
  assert.equal(writer.files.has('result.json'), false);
});

test('complete successful terminal writes preserve a proved zero-exit lifecycle', { timeout: 5000 }, async () => {
  const writer = memoryWriter();
  const output = await persistNativeTerminalEvidence(terminalInput(writer));
  assert.equal(output.passed, true);
  assert.equal(JSON.parse(writer.files.get('result.json')).passed, true);
  assert.deepEqual(output.outcomes.map((item) => item.persisted), [true, true, true]);
});

test('credential rejection retains failed result without passing the rejected detail to the writer', { timeout: 5000 }, async () => {
  const writer = memoryWriter();
  const secret = 'private-native-credential';
  const output = await persistNativeTerminalEvidence(terminalInput(writer, { secrets: [secret], details: [{ name: 'bridge-responses.json', value: { accidental: secret } }] }));
  assert.equal(output.passed, false);
  assert.equal(writer.attempts.includes('bridge-responses.json'), false);
  assert.equal(JSON.parse(writer.files.get('result.json')).passed, false);
  for (const text of writer.files.values()) assert.equal(text.includes(secret), false);
  assert.throws(() => encodeNativeEvidence({ token: 'a'.repeat(48) }), NativeEvidenceError);
  assert.throws(() => encodeNativeEvidence({}, Array.from({ length: 17 }, (_, index) => `secret-${index}`)), /credential-budget/u);
});

test('normal close failure is recorded as false and cannot be converted into PASS by a requested result', { timeout: 5000 }, async () => {
  const writer = memoryWriter();
  const output = await persistNativeTerminalEvidence(terminalInput(writer, { normalExit: { normalExit: false, attempted: true, discovery: { mainWindowHandle: 0 } } }));
  assert.equal(output.passed, false);
  assert.equal(JSON.parse(writer.files.get('normal-exit.json')).normalExit, false);
  assert.equal(JSON.parse(writer.files.get('result.json')).passed, false);
});

test('fallback cleanup cannot masquerade as normal lifecycle acceptance', { timeout: 5000 }, async () => {
  const writer = memoryWriter();
  const output = await persistNativeTerminalEvidence(terminalInput(writer, { cleanup: { cleanupProven: true, allFourPortsReleased: true, fallbackActions: [{ processId: 42, method: 'KillSingleVerifiedProcess' }], helperReclaims: [], errors: [] } }));
  assert.equal(output.passed, false);
  assert.equal(JSON.parse(writer.files.get('result.json')).passed, false);
});

test('an abnormal exit cannot pass even when a caller labels normal exit true', { timeout: 5000 }, async () => {
  const writer = memoryWriter();
  const input = terminalInput(writer);
  input.normalExit.studioExitCode = 42;
  const output = await persistNativeTerminalEvidence(input);
  assert.equal(output.passed, false);
  assert.equal(JSON.parse(writer.files.get('result.json')).passed, false);
});

test('a writer timeout is bounded and still attempts the independent result', { timeout: 5000 }, async () => {
  const writer = memoryWriter();
  const write = async (name, value) => name === 'cleanup.json' ? new Promise(() => {}) : writer.write(name, value);
  const output = await persistNativeTerminalEvidence(terminalInput(writer, { write, perWriteTimeoutMs: 10 }));
  assert.equal(output.passed, false);
  assert.equal(output.outcomes.find((item) => item.name === 'cleanup.json').failure, 'writer-timeout');
  assert.equal(JSON.parse(writer.files.get('result.json')).passed, false);
});

test('shared ancestry is retained once with full commands and unambiguous process references', { timeout: 5000 }, () => {
  const parent = { processId: 10, parentProcessId: 0, creationTimeUtc: '2026-10-06T21:00:00.0000000Z', commandLine: 'p'.repeat(40_000), executablePath: 'parent.exe' };
  const child = { processId: 11, parentProcessId: 10, creationTimeUtc: '2026-10-06T21:00:01.0000000Z', commandLine: 'studio.exe --owned', executablePath: 'studio.exe', parentChain: [parent] };
  const events = Array.from({ length: 24 }, (_, index) => ({ event: `observed-${index}`, identities: [child] }));
  const compact = compactNativeProcessEvidence({ runnerIdentity: parent, studioIdentity: child, events, helpers: [], streamCounts: {} });
  assert.equal(compact.schemaVersion, 2);
  assert.equal(compact.identities.length, 2);
  assert.equal(compact.identities.find((item) => item.processId === 10).commandLine, parent.commandLine);
  assert.equal(compact.identities.find((item) => item.processId === 11).parentChainKeys[0], compact.runnerIdentityKey);
  assert.ok(Buffer.byteLength(encodeNativeEvidence(compact)) < NativeEvidenceMaxBytes);
  assert.ok(Buffer.byteLength(JSON.stringify({ events })) > NativeEvidenceMaxBytes);
  assert.throws(() => compactNativeProcessEvidence({ runnerIdentity: child, studioIdentity: { ...child, commandLine: 'replacement' }, events: [], helpers: [], streamCounts: {} }), /identity-conflict/u);
});

test('expired evidence deadlines do not invoke writers or invent successful records', { timeout: 5000 }, async () => {
  const writer = memoryWriter();
  const output = await persistNativeTerminalEvidence(terminalInput(writer, { deadline: Date.now() - 1 }));
  assert.equal(output.passed, false);
  assert.equal(writer.attempts.length, 0);
  assert.equal(output.outcomes.length, 3);
  assert.ok(output.outcomes.every((item) => item.persisted === false && item.failure === 'deadline'));
});

test('invalid JSON detail is rejected without suppressing the failed result', { timeout: 5000 }, async () => {
  const writer = memoryWriter();
  const output = await persistNativeTerminalEvidence(terminalInput(writer, { details: [{ name: 'process-events.json', value: () => undefined }] }));
  assert.equal(output.passed, false);
  assert.equal(output.outcomes.find((item) => item.name === 'process-events.json').failure, 'serialization');
  assert.equal(JSON.parse(writer.files.get('result.json')).passed, false);
});

test('detail deadline exhaustion reserves a failed result write', { timeout: 5000 }, async () => {
  let clock = 0;
  const writer = memoryWriter();
  const write = async (name, value) => {
    if (name.startsWith('detail-')) clock += Math.min(1900, 12_000 - clock);
    return writer.write(name, value);
  };
  const details = Array.from({ length: 8 }, (_, index) => ({ name: `detail-${index}.json`, value: { index } }));
  const output = await persistNativeTerminalEvidence(terminalInput(writer, { write, details, now: () => clock, deadline: 15_000 }));
  assert.equal(clock, 12_000);
  assert.equal(output.passed, false);
  assert.equal(output.outcomes.find((item) => item.name === 'detail-7.json').failure, 'deadline');
  assert.equal(writer.attempts.includes('detail-7.json'), false);
  assert.equal(JSON.parse(writer.files.get('result.json')).passed, false);
  assert.equal(output.outcomes.at(-1).persisted, true);
});

test('an existing fatal cannot be overridden by a requested PASS', { timeout: 5000 }, async () => {
  const writer = memoryWriter();
  const output = await persistNativeTerminalEvidence(terminalInput(writer, { result: { passed: true, fatal: { stage: 'native', message: 'Existing failure.' } } }));
  assert.equal(output.passed, false);
  assert.equal(JSON.parse(writer.files.get('result.json')).passed, false);
});

test('cleanup errors cannot be overridden by a caller claiming cleanup is proved', { timeout: 5000 }, async () => {
  const writer = memoryWriter();
  const input = terminalInput(writer);
  input.cleanup.errors.push('An owned process remains alive.');
  const output = await persistNativeTerminalEvidence(input);
  assert.equal(output.passed, false);
  assert.equal(JSON.parse(writer.files.get('result.json')).passed, false);
});

test('empty commands and executable paths cannot become complete identity evidence', { timeout: 5000 }, () => {
  const identity = { processId: 10, parentProcessId: 0, creationTimeUtc: '2026-10-06T21:00:00Z', commandLine: 'parent.exe', executablePath: 'parent.exe' };
  const evidence = { runnerIdentity: identity, studioIdentity: null, events: [], helpers: [], streamCounts: {} };
  assert.throws(() => compactNativeProcessEvidence({ ...evidence, runnerIdentity: { ...identity, commandLine: ' ' } }), /identity/u);
  assert.throws(() => compactNativeProcessEvidence({ ...evidence, runnerIdentity: { ...identity, executablePath: '' } }), /identity/u);
});
