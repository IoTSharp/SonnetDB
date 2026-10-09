import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, realpath, rmdir, unlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { acceptOwnedIdentity, attemptIndependentSteps, captureOwnedCandidateSnapshot, captureOwnedSnapshot,
  createOwnedStopBoundary, createOwnedStopDiagnostics, observeOwnedStopDiagnostic, ownedIdentityFailure,
  ownedStopPowerShellScript, readOwnedStopHelperResult, recordOwnedIdentityEvent,
  ownedProcessCleanupDiagnostic, validateOwnedIdentity, validateOwnedIdentityAnchor, validateOwnedIdentityLedger,
  verifyOwnedProcessCleanup } from './query-host-evidence.mjs';

const created = '2026-10-07T00:00:00.000Z';
const owner = { pid: 10, parentPid: 1, created, commandLine: 'node runner', parentChain: [{ pid: 1, unavailable: true }] };
const child = (pid, commandLine = 'owned child') => ({ pid, parentPid: 10, created, commandLine,
  parentChain: [{ pid: 10, parentPid: 1, created, commandLine: 'node runner' }, { pid: 1, unavailable: true }] });
const options = { ownerPid: 10, validateText: () => {}, recordEvent: () => {}, clock: () => 1000 };

const stopToken = '0123456789abcdef0123456789abcdef';
const stopPrefix = 'SONNETDB_STOP_DIAGNOSTIC_V1 ';
const stopHelper = child(77, 'owned PowerShell helper');
function stopPacket(observation, candidate = child(11), helper = stopHelper, checkpoint = 'stop_after') {
  return { schema: 'sonnetdb.owned-stop-diagnostic.v1', ...observation.input(), candidatePid: candidate.pid,
    candidateCreated: candidate.created, helperPid: helper.pid, helperCreated: helper.created, checkpoint };
}
function stopTransport(observation, packet = stopPacket(observation)) {
  observeOwnedStopDiagnostic(observation, 'stderr', Buffer.from(`${stopPrefix}${JSON.stringify(packet)}\n`));
}
function stopFixture({ phase = 'stopped', returned, observe = () => {} } = {}) {
  const candidate = child(11); const identities = new Map([[10, owner], [11, candidate], [77, stopHelper]]);
  const calls = []; const events = []; const original = new Error('WB82_SYNTHETIC_Bearer_secret_never_persist');
  const diagnostics = createOwnedStopDiagnostics(identities, { observe, token: () => stopToken });
  const stop = createOwnedStopBoundary({ ownerPid: 10, identities, diagnostics, recordEvent: (value) => {
    calls.push('event'); events.push(value);
  }, validateAnchor: (value, anchor) => {
    calls.push('anchor'); if (phase === 'anchor') throw original;
    validateOwnedIdentityAnchor(value, anchor);
  }, helper: async function (script, input, signal, observation) {
    assert.equal(this, undefined); assert.equal(signal, undefined); assert.equal(script, ownedStopPowerShellScript());
    assert.equal(input.pid, candidate.pid); assert.deepEqual(input.ownershipAnchorIdentity,
      { pid: 10, parentPid: 1, created, commandLine: owner.commandLine });
    calls.push('dispatch'); if (phase === 'dispatch') throw original;
    observeOwnedStopDiagnostic(observation, 'checkpoint', 'helper_identity');
    if (phase === 'identity') throw original;
    observeOwnedStopDiagnostic(observation, 'bindHelper', stopHelper); if (observation) stopTransport(observation);
    return await readOwnedStopHelperResult({ waitTerminal: async () => {
      calls.push('wait'); if (phase === 'timeout' || phase === 'cancel') throw original;
      return { exitCode: phase === 'nonzero' ? 7 : 0, signal: null };
    }, verifyTerminal: (terminal) => { calls.push('terminal'); assert.equal(terminal.exitCode, 0); },
    parseResult: () => {
      calls.push('parse'); if (phase === 'malformed') return JSON.parse('{');
      return returned ?? (phase === 'already_exited' ? { exited: true } : phase === 'result_contract' ? { stopped: false } : { stopped: true });
    } }, observation);
  } });
  return { candidate, identities, calls, events, original, diagnostics, stop };
}

test('stop boundary observes original refusal and already-exited branches without changing callback results or call order', { timeout: 2000 }, async () => {
  const cases = [['anchor', 'anchor_validation'], ['dispatch', 'helper_dispatch'], ['identity', 'helper_identity'],
    ['timeout', 'helper_wait'], ['cancel', 'helper_wait'], ['nonzero', 'helper_terminal'], ['malformed', 'helper_result'],
    ['result_contract', 'unknown'], ['already_exited', 'result_contract']];
  const expires = Date.now() + 1500;
  for (let index = 0; index < cases.length && index < 9; index += 1) {
    assert.ok(Date.now() < expires); const [phase, checkpoint] = cases[index]; const fixture = stopFixture({ phase });
    let thrown; let returned;
    try { returned = await fixture.stop.call({ ignoredReceiver: true }, fixture.candidate, 'ignored original argument'); } catch (error) { thrown = error; }
    const success = phase === 'stopped' || phase === 'already_exited';
    assert.equal(returned, undefined); assert.equal(Boolean(thrown), !success); assert.equal(fixture.events.length, Number(success));
    if (['anchor', 'dispatch', 'identity', 'timeout', 'cancel'].includes(phase)) assert.equal(thrown, fixture.original);
    assert.deepEqual(fixture.calls, phase === 'anchor' ? ['anchor'] : phase === 'dispatch' || phase === 'identity' ? ['anchor', 'dispatch']
      : phase === 'timeout' || phase === 'cancel' ? ['anchor', 'dispatch', 'wait']
        : phase === 'nonzero' ? ['anchor', 'dispatch', 'wait', 'terminal']
          : ['anchor', 'dispatch', 'wait', 'terminal', 'parse', ...(success ? ['event'] : [])]);
    const diagnostic = fixture.diagnostics.summary().attempts[0]; assert.equal(diagnostic.jsCheckpoint, checkpoint);
    assert.equal(diagnostic.helperExit, phase === 'result_contract' ? 'unknown'
      : phase === 'nonzero' ? 'nonzero' : ['malformed', 'already_exited', 'stopped'].includes(phase) ? 'zero' : 'not_observed');
    assert.equal(JSON.stringify(diagnostic).includes(fixture.original.message), false);
    assert.equal(fixture.identities.size, 3);
  }
});

test('helper terminal refusal preserves exit signal output and identity assertion ordering and skips its parser', { timeout: 1000 }, async () => {
  const cases = ['exit', 'signal', 'output', 'identity']; const expires = Date.now() + 500;
  for (let index = 0; index < cases.length && index < 4; index += 1) {
    assert.ok(Date.now() < expires); const stage = cases[index]; const calls = [];
    await assert.rejects(readOwnedStopHelperResult({ waitTerminal: async () => ({ exitCode: stage === 'exit' ? 1 : 0,
      signal: stage === 'signal' ? 'SIGTERM' : null }), verifyTerminal: (terminal) => {
      calls.push('exit'); assert.equal(terminal.exitCode, 0); calls.push('signal'); assert.equal(terminal.signal, null);
      calls.push('output'); assert.equal(stage === 'output', false); calls.push('identity'); assert.equal(stage !== 'identity', true);
    }, parseResult: () => { calls.push('parse'); return { stopped: true }; } }));
    assert.deepEqual(calls, ['exit', 'signal', 'output', 'identity'].slice(0, index + 1));
  }
});

test('missing truncated duplicate forged and invalid checkpoint transport is unknown with no secret persistence', { timeout: 1000 }, () => {
  const marker = 'Bearer WB82_SYNTHETIC_TRANSPORT_SECRET_123456789';
  const cases = ['missing', 'truncated', 'duplicate', 'token', 'helper', 'checkpoint', 'extra', 'duplicate_key', 'attempt'];
  const expires = Date.now() + 500;
  for (let index = 0; index < cases.length && index < 9; index += 1) {
    assert.ok(Date.now() < expires); const candidate = child(11); const identities = new Map([[11, candidate], [77, stopHelper]]);
    const ledger = createOwnedStopDiagnostics(identities, { token: () => stopToken }); const observation = ledger.begin(candidate);
    observation.bindHelper(stopHelper); const packet = stopPacket(observation);
    if (cases[index] === 'token') packet.token = 'f'.repeat(32);
    if (cases[index] === 'attempt') packet.attempt = 2;
    if (cases[index] === 'helper') packet.helperPid = 78;
    if (cases[index] === 'checkpoint') packet.checkpoint = marker;
    if (cases[index] === 'extra') packet.raw = marker;
    let text = `${stopPrefix}${JSON.stringify(packet)}\n`;
    if (cases[index] === 'missing') text = '';
    if (cases[index] === 'truncated') text = text.slice(0, -1);
    if (cases[index] === 'duplicate') text += text;
    if (cases[index] === 'duplicate_key') text = text.replace('{', '{"attempt":1,');
    observation.stderr(Buffer.from(text)); observation.finish();
    const result = ledger.summary().attempts[0]; assert.equal(result.observation, 'unknown'); assert.equal(result.psCheckpoint, 'unknown');
    assert.equal(JSON.stringify(result).includes(marker), false); assert.equal(identities.size, 2);
  }
});

test('diagnostic candidate and helper accessor fields are never invoked or admitted', { timeout: 1000 }, () => {
  const cases = ['candidate', 'helper']; const expires = Date.now() + 500;
  for (let index = 0; index < cases.length && index < 2; index += 1) {
    assert.ok(Date.now() < expires); const candidate = child(11); const identities = new Map([[11, candidate], [77, stopHelper]]);
    const ledger = createOwnedStopDiagnostics(identities, { token: () => stopToken }); let reads = 0;
    const accessor = (value, key) => Object.defineProperty({ ...value }, key, { get() { reads += 1; throw new Error('WB82 getter secret'); } });
    const observation = ledger.begin(cases[index] === 'candidate' ? accessor(candidate, 'pid') : candidate);
    observation.bindHelper(cases[index] === 'helper' ? accessor(stopHelper, 'created') : stopHelper);
    observation.terminal({ exitCode: 0, signal: null });
    observation.finish(); assert.equal(reads, 0); assert.equal(ledger.summary().attempts[0].observation, 'unknown');
    assert.equal(identities.size, 2);
  }
});

test('changing result getters keep their original two authority reads and diagnostic unknown cannot replace success', { timeout: 1000 }, async () => {
  let reads = 0; const returned = { get exited() { return ++reads === 1; }, get stopped() { throw new Error('must not be read'); } };
  const fixture = stopFixture({ returned }); assert.equal(await fixture.stop(fixture.candidate), undefined);
  assert.equal(reads, 2); assert.equal(fixture.events.length, 1); assert.equal(fixture.events[0].alreadyExited, false);
  assert.equal(fixture.diagnostics.summary().attempts[0].observation, 'unknown');
});

test('throwing observers and mutated context methods leave original returns and exceptions intact with unknown records', { timeout: 2000 }, async () => {
  const fixture = stopFixture({ observe: () => { throw new Error('Bearer WB82_SYNTHETIC_OBSERVER_SECRET'); } });
  await fixture.stop(fixture.candidate); assert.equal(fixture.events.length, 1);
  assert.equal(fixture.diagnostics.summary().attempts[0].observation, 'unknown');
  const cases = [['checkpoint', 'getter'], ['checkpoint', 'throw'], ['bindHelper', 'getter'], ['bindHelper', 'throw'],
    ['finish', 'getter'], ['finish', 'throw']]; const expires = Date.now() + 1500;
  for (let index = 0; index < cases.length && index < 6; index += 1) {
    assert.ok(Date.now() < expires); const candidate = child(11); const identities = new Map([[10, owner], [11, candidate], [77, stopHelper]]);
    const diagnostics = createOwnedStopDiagnostics(identities, { token: () => stopToken }); const original = new Error('WB82 original callback');
    const [method, mode] = cases[index]; let getterReads = 0; let helperCalls = 0; let eventCalls = 0;
    const stop = createOwnedStopBoundary({ ownerPid: 10, identities, diagnostics, recordEvent: () => { eventCalls += 1; },
      helper: async (_script, _input, _signal, observation) => {
        helperCalls += 1;
        assert.equal(diagnostics.summary().attempts[0].observation, 'unknown'); // An unfinished context cannot self-certify.
        if (mode === 'getter') Object.defineProperty(observation, method, { get() { getterReads += 1; throw original; } });
        else observation[method] = () => { throw original; };
        observeOwnedStopDiagnostic(observation, 'checkpoint', 'helper_identity');
        observeOwnedStopDiagnostic(observation, 'bindHelper', stopHelper); stopTransport(observation);
        if (index === 5) throw original;
        return await readOwnedStopHelperResult({ waitTerminal: async () => ({ exitCode: 0, signal: null }),
          verifyTerminal: () => {}, parseResult: () => ({ stopped: true }) }, observation);
      } });
    if (index === 5) await assert.rejects(stop(candidate), (error) => error === original);
    else assert.equal(await stop(candidate), undefined);
    assert.equal(getterReads, 0); assert.equal(helperCalls, 1); assert.equal(eventCalls, Number(index !== 5));
    assert.equal(diagnostics.summary().attempts[0].observation, 'unknown'); assert.equal(identities.size, 3);
  }
});

test('descriptor projection rejects secret creation input without reading getters or changing the ledger', { timeout: 1000 }, () => {
  const marker = 'Bearer WB82_SYNTHETIC_CREATION_SECRET_123456789'; const cases = ['creation']; const expires = Date.now() + 500;
  for (let index = 0; index < cases.length && index < 1; index += 1) {
    assert.ok(Date.now() < expires); const candidate = { ...child(11), created: marker };
    const identities = new Map([[11, candidate]]); const ledger = createOwnedStopDiagnostics(identities,
      { token: () => stopToken }); const observation = ledger.begin(candidate);
    observation.checkpoint('anchor_validation'); observation.finish();
    assert.equal(ledger.summary().attempts[0].observation, 'unknown'); assert.equal(JSON.stringify(ledger.summary()).includes(marker), false);
    assert.equal(identities.get(11), candidate);
  }
});

test('stop diagnostics cap at 384 attempts and overflow never suppresses the original callback', { timeout: 2000 }, async () => {
  const fixture = stopFixture(); const expires = Date.now() + 1500;
  for (let index = 0; index < 385; index += 1) { assert.ok(Date.now() < expires); await fixture.stop(fixture.candidate); }
  const summary = fixture.diagnostics.summary(); assert.equal(summary.attempts.length, 384); assert.equal(summary.overflow, true);
  assert.equal(summary.observation, 'unknown'); assert.equal(summary.attempts[383].attempt, 384); assert.equal(fixture.events.length, 385);
  assert.equal(fixture.calls.filter((call) => call === 'dispatch').length, 385); assert.equal(fixture.identities.size, 3);
});

test('controlled PowerShell producer reports six fixed stop checkpoints with OS stop replaced', { timeout: 10000 }, () => {
  const production = ownedStopPowerShellScript();
  const firstLine = '$taskExpected = [Console]::In.ReadLine() | ConvertFrom-Json -DateKind String';
  const osStop = 'Stop-Process -Id ([int]$taskExpected.pid) -Force -ErrorAction Stop';
  assert.equal(production.split(osStop).length, 2); assert.equal(production.split(firstLine).length, 2);
  const controlled = production.replace(firstLine, '$taskExpected = $taskFixtureExpected').replace(osStop,
    'Invoke-Wb82FakeStop -Id ([int]$taskExpected.pid) -Force -ErrorAction Stop').replace('exit 0', 'return');
  assert.equal(controlled.includes('Stop-Process'), false);
  const fixture = `$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
function Invoke-Wb82FakeStop { param([int]$Id, [switch]$Force, [string]$ErrorAction)
 $script:taskFixtureStops++; if ($script:taskFixtureCase -eq 'osrefusal') { throw 'WB82_SYNTHETIC_OS_REFUSAL' }
}
$taskFixtureBlock = { ${controlled} }
$taskFixtureResults = [Collections.Generic.List[object]]::new()
$taskFixtureCases = @('stopped','already_exited','identity','anchor','parent','osrefusal')
$taskFixtureDeadline = [DateTime]::UtcNow.AddSeconds(4)
for ($taskFixtureIndex = 0; $taskFixtureIndex -lt 6; $taskFixtureIndex++) {
 if ([DateTime]::UtcNow -ge $taskFixtureDeadline -or [IO.File]::Exists('D:\\source\\SonnetDB\\artifacts\\wb82-vscode-stop-diagnostics-20261009\\cancel.request')) { throw 'Fixture deadline or cancellation.' }
 $script:taskFixtureCase=$taskFixtureCases[$taskFixtureIndex]; $script:taskFixtureStops=0
 $taskDate=[DateTime]::Parse('2026-10-07T00:00:00.0000000Z').ToUniversalTime(); $taskCreated=$taskDate.ToString('O')
 $taskSelf=[pscustomobject]@{ProcessId=77;ParentProcessId=10;CreationDate=$taskDate;CommandLine='owned PowerShell helper'}
 $taskAnchor=[pscustomobject]@{ProcessId=10;ParentProcessId=1;CreationDate=$taskDate;CommandLine='node runner'}
 $taskCurrentFixture=[pscustomobject]@{ProcessId=11;ParentProcessId=10;CreationDate=$taskDate;CommandLine='owned child'}
 $taskParent=[pscustomobject]@{pid=10;parentPid=1;created=$taskCreated;commandLine='node runner'}
 $taskFixtureExpected=[pscustomobject]@{pid=11;parentPid=10;created=$taskCreated;commandLine='owned child';parentChain=@($taskParent);ownershipAnchorPid=10;ownershipAnchorIdentity=$taskParent;stopDiagnostic=@{attempt=($taskFixtureIndex+1);token='${stopToken}'}}
 $taskHelperLookup=@{10=$taskAnchor;11=$taskCurrentFixture;77=$taskSelf}
 if ($script:taskFixtureCase -eq 'already_exited') {$taskHelperLookup.Remove(11)}
 if ($script:taskFixtureCase -eq 'identity') {$taskCurrentFixture.CommandLine='changed'}
 if ($script:taskFixtureCase -eq 'anchor') {$taskSelf.ParentProcessId=12}
 if ($script:taskFixtureCase -eq 'parent') {$taskFixtureExpected.parentChain=@([pscustomobject]@{pid=12;parentPid=1;created=$taskCreated;commandLine='node runner'})}
 $taskRefused=$false; $taskReturned=$null
 try {$taskFixtureOutput=@(& $taskFixtureBlock); if ($taskFixtureOutput.Count -eq 1) {$taskReturned=$taskFixtureOutput[0] | ConvertFrom-Json}} catch {$taskRefused=$true}
 $taskFixtureResults.Add([ordered]@{case=$script:taskFixtureCase;stops=$script:taskFixtureStops;refused=$taskRefused;returned=$taskReturned})
}
ConvertTo-Json -InputObject ($taskFixtureResults.ToArray()) -Compress -Depth 4`;
  const result = spawnSync('C:\\Program Files\\PowerShell\\7\\pwsh.exe', ['-NoProfile', '-Command', fixture],
    { encoding: 'utf8', timeout: 6000, maxBuffer: 64 * 1024, windowsHide: true });
  assert.equal(result.error, undefined); assert.equal(result.status, 0); assert.equal(result.signal, null);
  const results = JSON.parse(result.stdout.trim()); const lines = result.stderr.trim().split(/\r?\n/u);
  assert.equal(results.length, 6); assert.equal(lines.length, 6); const expires = Date.now() + 1000;
  const checkpoints = ['stop_after', 'already_exited', 'identity_validation', 'anchor_validation', 'parent_chain_validation', 'stop_before'];
  const psCreated = '2026-10-07T00:00:00.0000000Z'; const candidate = { ...child(11), created: psCreated };
  const helper = { ...stopHelper, created: psCreated }; const ledger = createOwnedStopDiagnostics(new Map([[11, candidate], [77, helper]]), { token: () => stopToken });
  for (let index = 0; index < 6; index += 1) {
    assert.ok(Date.now() < expires); const observation = ledger.begin(candidate); observation.bindHelper(helper);
    observation.stderr(Buffer.from(`${lines[index]}\n`)); observation.finish();
    assert.equal(ledger.summary().attempts[index].psCheckpoint, checkpoints[index]);
    assert.equal(results[index].stops, Number(index === 0 || index === 5)); assert.equal(results[index].refused, index >= 2);
    assert.deepEqual(results[index].returned, index === 0 ? { stopped: true } : index === 1 ? { exited: true } : null);
  }
});

test('invalid helper terminal fields stay unknown including missing unsafe enum and getter states', { timeout: 1000 }, () => {
  let reads = 0; const cases = [{ exitCode: -1, signal: null }, { exitCode: 0 }, { exitCode: 0, signal: 'Bearer SECRET' },
    { get exitCode() { reads += 1; throw new Error('WB82 secret'); }, signal: null }];
  const expires = Date.now() + 500;
  for (let index = 0; index < cases.length && index < 4; index += 1) {
    assert.ok(Date.now() < expires); const candidate = child(11); const ledger = createOwnedStopDiagnostics(new Map([[11, candidate]]), { token: () => stopToken });
    const observation = ledger.begin(candidate); observation.terminal(cases[index]); observation.finish();
    assert.equal(ledger.summary().attempts[0].observation, 'unknown'); assert.equal(ledger.summary().attempts[0].helperExit, 'unknown');
  }
  assert.equal(reads, 0);
});

test('diagnostic observation failure preserves cleanup audit first failure and final three mandatory gates', { timeout: 1000 }, async () => {
  const cases = [false, true]; const expires = Date.now() + 500;
  for (let index = 0; index < cases.length && index < 2; index += 1) {
    assert.ok(Date.now() < expires); const fixture = cleanupFixture(); const calls = []; let live = true;
    const diagnostics = createOwnedStopDiagnostics(fixture.request.identities, { token: () => stopToken, observe() { throw new Error('WB82 observer'); } });
    fixture.request.safeLiveIdentities = () => live ? [fixture.accepted] : [];
    fixture.request.stopVerified = createOwnedStopBoundary({ ownerPid: 10, identities: fixture.request.identities, diagnostics,
      helper: async () => { calls.push('stop'); live = false; if (cases[index]) throw new Error('WB82 original stop'); return { stopped: true }; },
      recordEvent: () => { calls.push('event'); } });
    const result = await verifyOwnedProcessCleanup(fixture.request);
    assert.equal(result.proven, !cases[index]); assert.equal(fixture.snapshots(), 3);
    assert.deepEqual(result.diagnostic.finalChecks, { remainingProcesses: 'passed', rootIdentities: 'passed', auditFailures: cases[index] ? 'refused' : 'passed' });
    assert.equal(fixture.failures.length, Number(cases[index])); assert.equal(result.diagnostic.structure.stopAttempts, 1);
    assert.deepEqual(calls, cases[index] ? ['stop'] : ['stop', 'event']);
    assert.deepEqual(result.diagnostic.firstRecoverableFailure, cases[index] ? { subcheck: 'stop-verification', stage: 'round' } : null);
  }
});

test('ambiguous return observation preserves original authority with diagnostic result unknown', { timeout: 1000 }, async () => {
  const cases = [{ exited: true, stopped: true }];
  const expires = Date.now() + 500;
  for (let index = 0; index < cases.length && index < 1; index += 1) {
    assert.ok(Date.now() < expires); const fixture = stopFixture({ returned: cases[index] }); let refused = false;
    try { await fixture.stop(fixture.candidate); } catch { refused = true; }
    assert.equal(refused, false); assert.equal(fixture.events.length, 1);
    assert.equal(fixture.diagnostics.summary().attempts[0].observation, 'unknown');
  }
});

test('runner consumes the injectable stop and helper result boundaries while retaining original evidence and limits', { timeout: 1000 }, async () => {
  const source = await readFile(new URL('./run-query-host-real.mjs', import.meta.url), 'utf8');
  assert.match(source, /const observedStop = createOwnedStopBoundary\(\{ ownerPid: process\.pid, identities, helper, recordEvent: event, diagnostics: stopDiagnostics \}\)/u);
  assert.match(source, /return await observedStop\(identity\)/u); assert.match(source, /return await readOwnedStopHelperResult\(/u);
  assert.match(source, /assert\.equal\(terminal\.exitCode, 0\); assert\.equal\(terminal\.signal, null\); assert\.equal\(exceeded, false\); assert\.ok\(record\.identityRecorded\)/u);
  assert.match(source, /observeOwnedStopDiagnostic\(stopObservation, 'stderr', chunk\)/u);
  assert.match(source, /stopDiagnostics: stopDiagnostics\.summary\(\)/u); assert.match(source, /assert\.ok\(\+\+helpers <= 112\)/u);
  assert.match(source, /const cap = 4 \* 1024 \* 1024/u);
});

const intermediate = { pid: 12, parentPid: 10, created, commandLine: 'complete current intermediate' };
const pendingCandidate = { ...child(11, null), parentPid: 12, parentChain: [intermediate, owner] };
const initialCandidates = () => [owner, intermediate, { ...pendingCandidate }];
const candidateOptions = { ...options, discover: (snapshot, values) => values.set(11, snapshot.find((item) => item.pid === 11)) };

const snapshotDiagnostic = (values = {}) => ({ schema: 'sonnetdb.owned-candidate-snapshot.v1', source: null, subject: null,
  snapshotCount: null, candidateMatches: null, subjectMatches: null, candidateCommandState: null, subjectCommandState: null, ...values });

const transitionDiagnostic = (values = {}) => ({ schema: 'sonnetdb.owned-candidate-transition.v1', availability: 'unknown', subject: null,
  initialSnapshotCount: null, freshSnapshotCount: null, initialSubjectMatches: null, freshSubjectMatches: null,
  initialSubjectCommandState: null, freshSubjectCommandState: null, tupleRelation: 'unknown', ...values });

async function parentTransitionFixture({ fresh = [owner], parent = child(12, null), clock = options.clock, afterAdmission } = {}) {
  const rejected = [11, 13].map((pid) => ({ ...child(pid, null), parentPid: 12, parentChain: [parent, owner] }));
  const identities = new Map([[10, owner]]); const events = []; let refreshes = 0;
  const failures = await captureOwnedCandidateSnapshot([owner, parent, ...rejected], identities, [], { ...options, clock,
    discover: (_snapshot, values) => { values.set(11, rejected[0]); values.set(13, rejected[1]); values.set(12, parent); },
    refreshSnapshot: async () => { refreshes += 1; return typeof fresh === 'function' ? fresh() : fresh; },
    recordEvent: (value) => { recordOwnedIdentityEvent(value, identities, events, options); afterAdmission?.(); } });
  return { failures, identities, events, refreshes };
}

const primaryFailureProjection = (failures) => failures.map(({ candidateTransition: _transition, ...failure }) => failure);

test('one existing fresh batch observes both rejected parents while retaining all three primary failures', { timeout: 1000 }, async () => {
  const fixture = await parentTransitionFixture();
  assert.equal(fixture.refreshes, 1); assert.deepEqual([...fixture.identities.values()], [owner]); assert.deepEqual(fixture.events, []);
  assert.deepEqual(fixture.failures.map((failure) => [failure.pid, failure.subreason]),
    [[11, 'parent_command_missing'], [13, 'parent_command_missing'], [12, 'candidate_snapshot_missing']]);
  const observation = transitionDiagnostic({ availability: 'existing_fresh', subject: 'parent', initialSnapshotCount: 4,
    freshSnapshotCount: 1, initialSubjectMatches: 1, freshSubjectMatches: 0, initialSubjectCommandState: 'missing' });
  assert.deepEqual(fixture.failures[0].candidateTransition, observation); assert.deepEqual(fixture.failures[1].candidateTransition, observation);
  assert.equal(Object.hasOwn(fixture.failures[2], 'candidateTransition'), false);
  assert.deepEqual(fixture.failures[0].candidateSnapshot, snapshotDiagnostic({ source: 'initial', subject: 'parent', snapshotCount: 4,
    candidateMatches: 1, subjectMatches: 1, candidateCommandState: 'missing', subjectCommandState: 'missing' }));
  assert.deepEqual(ownedIdentityFailure({ identityEvidenceFailure: fixture.failures[0] }, 11), fixture.failures[0]);
  const originalParentFailure = (pid) => ({ stage: 'identity', pid, reason: 'incomplete_or_unsafe_identity_preserved',
    subreason: 'parent_command_missing', failedField: 'parentChain.commandLine', completeness: 'missing_command', chainIndex: 0,
    structure: { candidatePid: pid, candidateParentPid: 12, ownerPid: 10, parentChainCount: 2, expectedParentPid: 12, observedParentPid: 12 },
    candidateSnapshot: snapshotDiagnostic({ source: 'initial', subject: 'parent', snapshotCount: 4,
      candidateMatches: 1, subjectMatches: 1, candidateCommandState: 'missing', subjectCommandState: 'missing' }) });
  assert.deepEqual(primaryFailureProjection(fixture.failures), [originalParentFailure(11), originalParentFailure(13),
    { stage: 'identity', pid: 12, reason: 'incomplete_or_unsafe_identity_preserved', subreason: 'candidate_snapshot_missing',
      failedField: 'currentSnapshot.identity', completeness: 'missing_identity', chainIndex: null,
      structure: { candidatePid: 12, candidateParentPid: 10, ownerPid: 10, parentChainCount: 2, expectedParentPid: 12, observedParentPid: null },
      candidateSnapshot: snapshotDiagnostic({ source: 'fresh', subject: 'candidate', snapshotCount: 1, candidateMatches: 0, subjectMatches: 0 }) }]);
});

test('restored parent presence leaves rejected descendants blocking and accepted ledger events equal to an isolated parent capture', { timeout: 2000 }, async () => {
  const cases = [[null, 'missing', 'same', false], [undefined, 'missing', 'same', false], ['', 'empty', 'changed', false],
    ['restored complete parent', 'present', 'changed', true], [123, 'invalid', 'unknown', false]];
  const expires = Date.now() + 1500;
  for (let index = 0; index < cases.length && index < 5; index += 1) {
    assert.ok(Date.now() < expires);
    const [commandLine, state, relation, admitted] = cases[index]; const currentParent = child(12, commandLine);
    // Explicit undefined is a missing command rather than the child helper's default.
    currentParent.commandLine = commandLine;
    const fixture = await parentTransitionFixture({ fresh: [owner, currentParent] });
    assert.equal(fixture.refreshes, 1); assert.equal(fixture.failures.length, admitted ? 2 : 3);
    assert.deepEqual(fixture.failures.slice(0, 2).map((failure) => [failure.pid, failure.subreason]),
      [[11, 'parent_command_missing'], [13, 'parent_command_missing']]);
    assert.deepEqual(fixture.failures[0].candidateTransition, transitionDiagnostic({ availability: 'existing_fresh', subject: 'parent',
      initialSnapshotCount: 4, freshSnapshotCount: 2, initialSubjectMatches: 1, freshSubjectMatches: 1,
      initialSubjectCommandState: 'missing', freshSubjectCommandState: state, tupleRelation: relation }));
    assert.equal(fixture.identities.has(11), false); assert.equal(fixture.identities.has(13), false);
    const parent = child(12, null); const identities = new Map([[10, owner]]); const events = [];
    await captureOwnedCandidateSnapshot([owner, parent], identities, [], { ...options,
      discover: (_snapshot, values) => values.set(12, parent), refreshSnapshot: async () => [owner, currentParent],
      recordEvent: (value) => recordOwnedIdentityEvent(value, identities, events, options) });
    assert.deepEqual([...fixture.identities.values()], [...identities.values()]); assert.deepEqual(fixture.events, events);
    assert.equal(JSON.stringify(fixture.failures).includes('restored complete parent'), false);
    assert.equal(fixture.events.some((event) => Object.hasOwn(event, 'candidateTransition')), false);
  }
});

test('duplicate or changed fresh parent tuples never repair the two original parent refusals', { timeout: 1000 }, async () => {
  const parent = child(12, 'fresh parent');
  const cases = [[parent, parent, 2, 'unknown', 'candidate_snapshot_duplicate'],
    [{ ...parent, created: '2026-10-07T00:01:00.000Z' }, null, 1, 'changed', 'candidate_creation_changed'],
    [{ ...parent, parentPid: 99 }, null, 1, 'changed', 'candidate_parent_changed']];
  const expires = Date.now() + 800;
  for (let index = 0; index < cases.length && index < 3; index += 1) {
    assert.ok(Date.now() < expires);
    const [current, duplicate, matches, relation, refusal] = cases[index];
    const fresh = duplicate ? [owner, current, duplicate] : [owner, current]; const fixture = await parentTransitionFixture({ fresh });
    assert.deepEqual(fixture.failures.map((failure) => failure.subreason), ['parent_command_missing', 'parent_command_missing', refusal]);
    assert.deepEqual([...fixture.identities.values()], [owner]); assert.deepEqual(fixture.events, []); assert.equal(fixture.refreshes, 1);
    assert.deepEqual(fixture.failures[0].candidateTransition, transitionDiagnostic({ availability: 'existing_fresh', subject: 'parent',
      initialSnapshotCount: 4, freshSnapshotCount: fresh.length, initialSubjectMatches: 1, freshSubjectMatches: matches,
      initialSubjectCommandState: 'missing', freshSubjectCommandState: matches === 1 ? 'present' : null, tupleRelation: relation }));
  }
});

test('no fresh batch or a failed fresh lookup leaves the legacy failure shape unannotated', { timeout: 1000 }, async () => {
  const parent = child(12, null); const candidate = { ...child(11, null), parentPid: 12, parentChain: [parent, owner] };
  const identities = new Map([[10, owner]]); let refreshes = 0;
  const failures = await captureOwnedCandidateSnapshot([owner, parent, candidate], identities, [], { ...options,
    discover: (_snapshot, values) => values.set(11, candidate), refreshSnapshot: async () => { refreshes += 1; return []; } });
  assert.equal(refreshes, 0); assert.equal(failures.length, 1); assert.equal(Object.hasOwn(failures[0], 'candidateTransition'), false);
  const cases = [() => { throw new Error('WB54_SYNTHETIC_refresh_error_never_persist'); }, () => Array.from({ length: 4097 }, () => owner)];
  const expires = Date.now() + 800;
  for (let index = 0; index < cases.length && index < 2; index += 1) {
    assert.ok(Date.now() < expires); const fixture = await parentTransitionFixture({ fresh: cases[index] });
    assert.equal(fixture.refreshes, 1); assert.equal(fixture.failures.length, 3);
    assert.deepEqual(fixture.failures.slice(0, 2).map((failure) => failure.subreason), ['parent_command_missing', 'parent_command_missing']);
    assert.equal(fixture.failures.some((failure) => Object.hasOwn(failure, 'candidateTransition')), false);
    assert.deepEqual([...fixture.identities.values()], [owner]); assert.deepEqual(fixture.events, []);
    assert.equal(JSON.stringify(fixture.failures).includes('WB54_SYNTHETIC_refresh_error_never_persist'), false);
  }
});

test('initial failure coordinates bind candidate and anchor roles and leave an unknown subject unannotated', { timeout: 1000 }, async () => {
  const cases = [
    { candidate: { ...child(15), created: 'invalid creation' }, extra: [], role: 'candidate', relation: 'unknown' },
    { candidate: { ...child(15), parentPid: 99, parentChain: [] },
      extra: [{ pid: 99, parentPid: 0, created, commandLine: 'external parent' }], role: 'anchor', relation: 'same' },
    { candidate: { ...child(15), parentChain: null }, extra: [], role: null },
  ];
  const expires = Date.now() + 800;
  for (let index = 0; index < cases.length && index < 3; index += 1) {
    assert.ok(Date.now() < expires); const { candidate, extra, role, relation } = cases[index]; const pending = child(12, null);
    const identities = new Map([[10, owner]]); const events = []; let refreshes = 0;
    const failures = await captureOwnedCandidateSnapshot([owner, candidate, pending, ...extra], identities, [], { ...options,
      discover: (_snapshot, values) => { values.set(15, candidate); values.set(12, pending); },
      refreshSnapshot: async () => { refreshes += 1; return [owner, candidate, child(12)]; },
      recordEvent: (value) => recordOwnedIdentityEvent(value, identities, events, options) });
    assert.equal(failures.length, 1); assert.equal(refreshes, 1); assert.equal(identities.has(15), false); assert.equal(identities.has(12), true);
    if (role === null) assert.equal(Object.hasOwn(failures[0], 'candidateTransition'), false);
    else assert.deepEqual(failures[0].candidateTransition, transitionDiagnostic({ availability: 'existing_fresh', subject: role,
      initialSnapshotCount: 3 + extra.length, freshSnapshotCount: 3, initialSubjectMatches: 1, freshSubjectMatches: 1,
      initialSubjectCommandState: 'present', freshSubjectCommandState: 'present', tupleRelation: relation }));
  }
});

test('observed tuple comparison applies finite creation UInt32 and small text caps without weakening primary admission', { timeout: 1000 }, async () => {
  // Extra fractional zeros preserve a valid ISO creation time while exceeding the diagnostic text cap.
  const paddedCreated = `${created.slice(0, -1)}${'0'.repeat(65 - created.length)}Z`;
  assert.equal(paddedCreated.length, 65); assert.ok(Number.isFinite(Date.parse(paddedCreated)));
  const cases = [
    [child(12, null), child(12, 'x'.repeat(8192)), 'changed', true],
    [child(12, null), child(12, 'x'.repeat(8193)), 'unknown', true],
    [{ ...child(12, null), created: paddedCreated }, { ...child(12), created: paddedCreated }, 'unknown', true],
    [child(12, null), { ...child(12), parentPid: 0x100000000 }, 'unknown', false],
    [child(12, null), { ...child(12), created: 'invalid fresh creation' }, 'unknown', false],
  ];
  const expires = Date.now() + 800;
  for (let index = 0; index < cases.length && index < 5; index += 1) {
    assert.ok(Date.now() < expires); const [parent, current, relation, admitted] = cases[index];
    const fixture = await parentTransitionFixture({ parent, fresh: [owner, current] });
    assert.equal(fixture.failures[0].subreason, 'parent_command_missing'); assert.equal(fixture.failures[0].candidateTransition.tupleRelation, relation);
    assert.equal(fixture.identities.has(12), admitted); assert.equal(fixture.identities.has(11), false); assert.equal(fixture.identities.has(13), false);
    assert.equal(fixture.refreshes, 1); assert.equal(JSON.stringify(fixture.failures).includes('x'.repeat(8192)), false);
    assert.equal(JSON.stringify(fixture.failures).includes(paddedCreated), false);
  }
});

test('expired or throwing observation clocks cannot replace refusals or undo completed admission', { timeout: 1000 }, async () => {
  const modes = ['expired', 'throw']; const expires = Date.now() + 800;
  for (let index = 0; index < modes.length && index < 2; index += 1) {
    assert.ok(Date.now() < expires); let admitted = false; let postAdmissionReads = 0;
    const fixture = await parentTransitionFixture({ fresh: [owner, child(12)], afterAdmission: () => { admitted = true; },
      clock: () => { if (!admitted) return 1000; postAdmissionReads += 1;
        if (modes[index] === 'throw') throw new Error('WB54_SYNTHETIC_clock_error_never_persist'); return 3000; } });
    assert.equal(fixture.failures.length, 2); assert.equal(fixture.identities.has(12), true); assert.equal(fixture.events.length, 1);
    assert.equal(fixture.failures.some((failure) => Object.hasOwn(failure, 'candidateTransition')), false);
    assert.equal(postAdmissionReads, 1); assert.equal(fixture.refreshes, 1);
    assert.equal(JSON.stringify(fixture.failures).includes('WB54_SYNTHETIC_clock_error_never_persist'), false);
  }
});

test('initial and fresh tuple getter errors remain unknown after all primary ledger and events complete', { timeout: 1000 }, async () => {
  const modes = ['initial', 'fresh']; const marker = 'WB54_SYNTHETIC_tuple_getter_never_persist'; const expires = Date.now() + 800;
  for (let index = 0; index < modes.length && index < 2; index += 1) {
    assert.ok(Date.now() < expires); let admitted = false; let lateReads = 0;
    const initial = { ...child(12, null), get commandLine() { if (admitted && modes[index] === 'initial') { lateReads += 1; throw new Error(marker); } return null; } };
    const current = { ...child(12), get commandLine() { if (admitted && modes[index] === 'fresh') { lateReads += 1; throw new Error(marker); } return 'owned child'; } };
    const fixture = await parentTransitionFixture({ parent: initial, fresh: [owner, current], afterAdmission: () => { admitted = true; } });
    assert.deepEqual(fixture.failures.map((failure) => [failure.pid, failure.subreason]), [[11, 'parent_command_missing'], [13, 'parent_command_missing']]);
    assert.deepEqual(fixture.failures.map((failure) => failure.candidateTransition), [transitionDiagnostic(), transitionDiagnostic()]);
    assert.equal(fixture.identities.has(12), true); assert.equal(fixture.events.length, 1); assert.equal(lateReads, 2);
    assert.equal(JSON.stringify(fixture.failures).includes(marker), false); assert.equal(fixture.refreshes, 1);
  }
});

test('a tuple getter consuming the remaining original flush window omits the current observation after admission', { timeout: 1000 }, async () => {
  let admitted = false; let now = 1000; let lateReads = 0;
  const parent = { ...child(12, null), get commandLine() { if (admitted) { lateReads += 1; now = 3000; } return null; } };
  const fixture = await parentTransitionFixture({ parent, fresh: [owner, child(12)], clock: () => now,
    afterAdmission: () => { admitted = true; } });
  const control = await parentTransitionFixture({ fresh: [owner, child(12)] });
  assert.equal(lateReads, 1); assert.equal(now, 3000); assert.equal(fixture.refreshes, 1);
  assert.equal(fixture.failures.length, 2); assert.equal(fixture.failures.some((failure) => Object.hasOwn(failure, 'candidateTransition')), false);
  assert.deepEqual(primaryFailureProjection(fixture.failures), primaryFailureProjection(control.failures));
  assert.deepEqual([...fixture.identities.values()], [...control.identities.values()]); assert.deepEqual(fixture.events, control.events);
});

test('transition projection preserves legacy snapshots and retains only bounded fixed fields through two projections', { timeout: 1000 }, () => {
  const error = { identityEvidenceFailure: { stage: 'identity', reason: 'incomplete_or_unsafe_identity_preserved',
    subreason: 'parent_command_missing', failedField: 'parentChain.commandLine', completeness: 'missing_command', chainIndex: 0,
    structure: { candidatePid: 11, candidateParentPid: 12, ownerPid: 10, parentChainCount: 2, expectedParentPid: 12, observedParentPid: 12 } } };
  const snapshot = snapshotDiagnostic({ source: 'initial', subject: 'parent', snapshotCount: 4,
    candidateMatches: 1, subjectMatches: 1, candidateCommandState: 'missing', subjectCommandState: 'missing' });
  const legacy = ownedIdentityFailure(error, 11, snapshot);
  const transition = transitionDiagnostic({ availability: 'existing_fresh', subject: 'parent', initialSnapshotCount: 4096,
    freshSnapshotCount: 3, initialSubjectMatches: 1, freshSubjectMatches: 1, initialSubjectCommandState: 'missing',
    freshSubjectCommandState: 'present', tupleRelation: 'changed' });
  const marker = 'WB54_SYNTHETIC_extra_tuple_secret_never_persist';
  const failure = ownedIdentityFailure(error, 11, snapshot, { ...transition, commandLine: marker, pid: 12, created: marker, timeUtc: marker, hash: marker });
  assert.deepEqual(failure.candidateTransition, transition); assert.deepEqual(primaryFailureProjection([failure]), [legacy]);
  assert.deepEqual(ownedIdentityFailure({ identityEvidenceFailure: failure }, 11), failure); assert.equal(JSON.stringify(failure).includes(marker), false);
  const bounded = ownedIdentityFailure(error, 11, snapshot, { ...transition, initialSnapshotCount: 4097, freshSnapshotCount: -1,
    initialSubjectMatches: 3, freshSubjectMatches: 2, initialSubjectCommandState: marker, freshSubjectCommandState: 'present', tupleRelation: 'changed' });
  assert.deepEqual(bounded.candidateTransition, transitionDiagnostic({ availability: 'existing_fresh', subject: 'parent', freshSubjectMatches: 2 }));
  assert.deepEqual(primaryFailureProjection([bounded]), [legacy]);
  assert.deepEqual(ownedIdentityFailure(error, 11, snapshot, { ...transition, availability: 'untrusted' }).candidateTransition, transitionDiagnostic());
  assert.deepEqual(ownedIdentityFailure(error, 11, snapshot, { ...transition, schema: 'untrusted' }).candidateTransition, transitionDiagnostic());
});

test('transition fields are read once and throwing detail or field getters preserve the original rejection', { timeout: 1000 }, () => {
  const marker = 'WB54_SYNTHETIC_projection_getter_never_persist'; const reads = {};
  const transition = transitionDiagnostic({ availability: 'existing_fresh', subject: 'candidate', initialSnapshotCount: 3,
    freshSnapshotCount: 2, initialSubjectMatches: 1, freshSubjectMatches: 1, initialSubjectCommandState: 'missing',
    freshSubjectCommandState: 'missing', tupleRelation: 'same' });
  const descriptors = Object.fromEntries(Object.entries(transition).map(([key, value]) => [key, { get: () => {
    reads[key] = (reads[key] ?? 0) + 1; return reads[key] === 1 ? value : marker; } }]));
  const error = { identityEvidenceFailure: { stage: 'identity', reason: 'incomplete_or_unsafe_identity_preserved',
    subreason: 'identity_command_missing', failedField: 'commandLine', completeness: 'missing_command' } };
  const legacy = ownedIdentityFailure(error, 11); const failure = ownedIdentityFailure(error, 11, undefined, Object.defineProperties({}, descriptors));
  assert.deepEqual(failure.candidateTransition, transition); assert.equal(Object.values(reads).every((count) => count === 1), true);
  assert.equal(Object.keys(reads).length, Object.keys(transition).length); assert.deepEqual(primaryFailureProjection([failure]), [legacy]);
  assert.deepEqual(ownedIdentityFailure({ identityEvidenceFailure: failure }, 11), failure);
  const throwing = { schema: transition.schema, get subject() { throw new Error(marker); } };
  const projected = ownedIdentityFailure(error, 11, undefined, throwing);
  assert.deepEqual(projected.candidateTransition, transitionDiagnostic()); assert.deepEqual(primaryFailureProjection([projected]), [legacy]);
  const detail = { ...error.identityEvidenceFailure, get candidateTransition() { throw new Error(marker); } };
  const retained = ownedIdentityFailure({ identityEvidenceFailure: detail }, 11);
  assert.deepEqual(retained.candidateTransition, transitionDiagnostic()); assert.deepEqual(primaryFailureProjection([retained]), [legacy]);
  assert.equal(JSON.stringify([failure, projected, retained]).includes(marker), false);
});

test('maximum candidate batches keep late observation bounded by the original flush deadline', { timeout: 1000 }, async () => {
  const rejected = Array.from({ length: 126 }, (_value, index) => ({ ...child(100 + index), created: 'invalid creation' }));
  const pending = child(12, null); const identities = new Map([[10, owner]]); const events = [];
  let admitted = false; let lateClocks = 0; let refreshes = 0; const expires = Date.now() + 800;
  const failures = await captureOwnedCandidateSnapshot([owner, ...rejected, pending], identities, [], { ...options,
    clock: () => { if (!admitted) return 1000; lateClocks += 1; return lateClocks <= 6 ? 1000 : 3000; },
    discover: (_snapshot, values) => {
      for (let index = 0; index < rejected.length && index < 126; index += 1) { assert.ok(Date.now() < expires); values.set(rejected[index].pid, rejected[index]); }
      values.set(12, pending);
    }, refreshSnapshot: async () => { refreshes += 1; return [owner, ...rejected, child(12)]; },
    recordEvent: (value) => { recordOwnedIdentityEvent(value, identities, events, options); admitted = true; } });
  assert.equal(failures.length, 126); assert.equal(refreshes, 1); assert.equal(identities.size, 2); assert.equal(events.length, 1);
  assert.equal(failures.filter((failure) => Object.hasOwn(failure, 'candidateTransition')).length, 3); assert.equal(lateClocks, 7);
  assert.deepEqual(primaryFailureProjection(failures).map((failure) => [failure.pid, failure.subreason]),
    rejected.map((candidate) => [candidate.pid, 'identity_created_invalid']));
});

test('initial parent command rejection observes presence without initiating a fresh snapshot', { timeout: 1000 }, async () => {
  const cases = [[null, 'parent_command_missing', 'missing'], [undefined, 'parent_command_missing', 'missing'],
    ['', 'parent_command_empty', 'empty'], [123, 'parent_command_invalid', 'invalid']];
  const expires = Date.now() + 800;
  for (let index = 0; index < cases.length && index < 4; index += 1) {
    assert.ok(Date.now() < expires);
    const [commandLine, subreason, state] = cases[index]; const identities = new Map([[10, owner]]); const events = []; let refreshes = 0;
    const failures = await captureOwnedCandidateSnapshot([owner, { ...intermediate, commandLine }, pendingCandidate], identities, [], {
      ...candidateOptions, recordEvent: (value) => events.push(value), refreshSnapshot: async () => { refreshes += 1; return initialCandidates(); } });
    assert.equal(failures.length, 1); assert.equal(failures[0].subreason, subreason); assert.equal(failures[0].chainIndex, 0);
    assert.deepEqual(failures[0].candidateSnapshot, snapshotDiagnostic({ source: 'initial', subject: 'parent', snapshotCount: 3,
      candidateMatches: 1, subjectMatches: 1, candidateCommandState: 'missing', subjectCommandState: state }));
    assert.equal(refreshes, 0); assert.deepEqual([...identities.values()], [owner]); assert.deepEqual(events, []);
  }
});

test('fresh rejection identifies candidate, parent and anchor observations without admitting authority', { timeout: 2000 }, async () => {
  const current = { ...pendingCandidate, commandLine: 'fresh presence only command' };
  const cases = [
    [[owner, intermediate], 'candidate_snapshot_missing', 'candidate', 0, 0, null, null],
    [[owner, intermediate, current, current], 'candidate_snapshot_duplicate', 'candidate', 2, 2, null, null],
    [[owner, current], 'candidate_snapshot_missing', 'parent', 1, 0, 'present', null],
    [[owner, intermediate, intermediate, current], 'candidate_snapshot_duplicate', 'parent', 1, 2, 'present', null],
    [[intermediate, current], 'candidate_snapshot_missing', 'anchor', 1, 0, 'present', null],
    [[owner, owner, intermediate, current], 'candidate_snapshot_duplicate', 'anchor', 1, 2, 'present', null],
    [[owner, { ...intermediate, commandLine: null }, current], 'parent_command_missing', 'parent', 1, 1, 'present', 'missing'],
    [[owner, { ...intermediate, commandLine: '' }, current], 'parent_command_empty', 'parent', 1, 1, 'present', 'empty'],
    [[{ ...owner, commandLine: null }, intermediate, current], 'parent_command_missing', 'anchor', 1, 1, 'present', 'missing'],
    [[{ ...owner, commandLine: 123 }, intermediate, current], 'parent_command_invalid', 'anchor', 1, 1, 'present', 'invalid'],
    [[{ ...owner, commandLine: 'changed anchor' }, intermediate, current], 'ownership_anchor_tuple_mismatch', 'anchor', 1, 1, 'present', 'present'],
    [[owner, intermediate, { ...current, commandLine: null }], 'identity_command_missing', 'candidate', 1, 1, 'missing', 'missing'],
    [[owner, intermediate, { ...current, commandLine: '' }], 'identity_command_empty', 'candidate', 1, 1, 'empty', 'empty'],
    [[owner, intermediate, { ...current, commandLine: 123 }], 'identity_command_invalid', 'candidate', 1, 1, 'invalid', 'invalid'],
  ];
  const expires = Date.now() + 1500;
  for (let index = 0; index < cases.length && index < 14; index += 1) {
    assert.ok(Date.now() < expires);
    const [fresh, subreason, subject, candidateMatches, subjectMatches, candidateCommandState, subjectCommandState] = cases[index];
    const identities = new Map([[10, owner]]); const events = []; let refreshes = 0;
    const failures = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions,
      recordEvent: (value) => events.push(value), refreshSnapshot: async () => { refreshes += 1; return fresh; } });
    assert.equal(failures.length, 1); assert.equal(failures[0].subreason, subreason);
    assert.deepEqual(failures[0].candidateSnapshot, snapshotDiagnostic({ source: 'fresh', subject, snapshotCount: fresh.length,
      candidateMatches, subjectMatches, candidateCommandState, subjectCommandState }));
    assert.equal(refreshes, 1); assert.deepEqual([...identities.values()], [owner]); assert.deepEqual(events, []);
    assert.equal(JSON.stringify(failures).includes('fresh presence only command'), false);
  }
});

test('snapshot diagnostics leave accepted ledger and command-recheck events unchanged', { timeout: 1000 }, async () => {
  const identities = new Map([[10, owner]]); const events = []; let refreshes = 0;
  const failures = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions,
    refreshSnapshot: async () => { refreshes += 1; return [owner, intermediate, { ...pendingCandidate, commandLine: 'accepted fresh command' }]; },
    recordEvent: (value) => recordOwnedIdentityEvent(value, identities, events, options) });
  assert.deepEqual(failures, []); assert.equal(refreshes, 1); assert.equal(identities.size, 2); assert.equal(events.length, 1);
  assert.equal(events[0].candidateCommandRechecked, true); assert.equal(events[0].initialCommandState, 'missing');
  assert.deepEqual(events[0].identityLedgerRef, { pid: 11, created });
  assert.equal(Object.hasOwn(events[0], 'candidateSnapshot'), false); assert.equal(Object.hasOwn(identities.get(11), 'candidateSnapshot'), false);
});

test('a parent rejection without exact safe coordinates leaves the observed subject unknown', { timeout: 1000 }, async () => {
  const candidate = { ...child(11), parentChain: null }; const identities = new Map([[10, owner]]); const events = []; let refreshes = 0;
  const failures = await captureOwnedCandidateSnapshot([owner, candidate], identities, [], { ...options,
    discover: (_snapshot, values) => values.set(11, candidate), recordEvent: (value) => events.push(value),
    refreshSnapshot: async () => { refreshes += 1; return []; } });
  assert.equal(failures.length, 1); assert.equal(failures[0].subreason, 'parent_chain_missing');
  assert.equal(failures[0].chainIndex, null); assert.equal(failures[0].structure.expectedParentPid, null);
  assert.deepEqual(failures[0].candidateSnapshot, snapshotDiagnostic({ source: 'initial', snapshotCount: 2,
    candidateMatches: 1, candidateCommandState: 'present' }));
  assert.equal(refreshes, 0); assert.deepEqual([...identities.values()], [owner]); assert.deepEqual(events, []);
});

test('candidate and subject observations reuse one command getter read for the same rejected PID', { timeout: 1000 }, async () => {
  const marker = 'WB52_SYNTHETIC_duplicate_getter_probe_must_not_run'; let commandReads = 0;
  const candidate = { ...child(11), created: 'invalid initial creation', get commandLine() {
    commandReads += 1; if (commandReads > 2) throw new Error(marker); return 'presence only';
  } };
  const identities = new Map([[10, owner]]); const events = []; let refreshes = 0;
  const failures = await captureOwnedCandidateSnapshot([owner, candidate], identities, [], { ...options,
    discover: (_snapshot, values) => values.set(11, candidate), recordEvent: (value) => events.push(value),
    refreshSnapshot: async () => { refreshes += 1; return []; } });
  assert.equal(failures.length, 1); assert.equal(failures[0].subreason, 'identity_created_invalid'); assert.equal(commandReads, 2);
  assert.deepEqual(failures[0].candidateSnapshot, snapshotDiagnostic({ source: 'initial', subject: 'candidate', snapshotCount: 2,
    candidateMatches: 1, subjectMatches: 1, candidateCommandState: 'present', subjectCommandState: 'present' }));
  assert.equal(JSON.stringify(failures).includes(marker), false); assert.equal(refreshes, 0);
  assert.deepEqual([...identities.values()], [owner]); assert.deepEqual(events, []);
});

test('optional snapshot projection retains legacy shape and bounded fixed fields through a second projection', { timeout: 1000 }, () => {
  const error = { identityEvidenceFailure: { stage: 'identity', reason: 'incomplete_or_unsafe_identity_preserved',
    subreason: 'parent_command_missing', failedField: 'parentChain.commandLine', completeness: 'missing_command', chainIndex: 0,
    structure: { candidatePid: 11, candidateParentPid: 12, ownerPid: 10, parentChainCount: 2, expectedParentPid: 12, observedParentPid: 12 } } };
  const legacy = ownedIdentityFailure(error, 11); assert.equal(Object.hasOwn(legacy, 'candidateSnapshot'), false);
  const observation = snapshotDiagnostic({ source: 'initial', subject: 'parent', snapshotCount: 4096, candidateMatches: 2,
    subjectMatches: 0, candidateCommandState: 'present', subjectCommandState: 'missing', commandLine: 'omitted raw text' });
  const failure = ownedIdentityFailure(error, 11, observation);
  assert.deepEqual(failure.candidateSnapshot, snapshotDiagnostic({ source: 'initial', subject: 'parent', snapshotCount: 4096,
    candidateMatches: 2, subjectMatches: 0, candidateCommandState: 'present', subjectCommandState: 'missing' }));
  assert.deepEqual(ownedIdentityFailure({ identityEvidenceFailure: failure }, 11), failure);
  const { candidateSnapshot: _observation, ...original } = failure; assert.deepEqual(original, legacy);
  assert.deepEqual(ownedIdentityFailure(error, 11, snapshotDiagnostic({ source: 'untrusted', subject: 'untrusted', snapshotCount: 4097,
    candidateMatches: 3, subjectMatches: -1, candidateCommandState: 'untrusted', subjectCommandState: 'untrusted' })).candidateSnapshot,
  snapshotDiagnostic());
  assert.deepEqual(ownedIdentityFailure(error, 11, null).candidateSnapshot, snapshotDiagnostic());
});

test('changing snapshot getters are read once and throwing or unknown observations preserve the original rejection', { timeout: 1000 }, () => {
  const marker = 'WB52_SYNTHETIC_getter_secret_never_persist'; const reads = {};
  const once = (key, value) => () => { reads[key] = (reads[key] ?? 0) + 1; return reads[key] === 1 ? value : marker; };
  const observation = Object.defineProperties({}, {
    schema: { get: once('schema', 'sonnetdb.owned-candidate-snapshot.v1') }, source: { get: once('source', 'fresh') },
    subject: { get: once('subject', 'candidate') }, snapshotCount: { get: once('snapshotCount', 3) },
    candidateMatches: { get: once('candidateMatches', 1) }, subjectMatches: { get: once('subjectMatches', 1) },
    candidateCommandState: { get: once('candidateCommandState', 'missing') }, subjectCommandState: { get: once('subjectCommandState', 'missing') },
  });
  const error = { identityEvidenceFailure: { stage: 'identity', reason: 'incomplete_or_unsafe_identity_preserved',
    subreason: 'identity_command_missing', failedField: 'commandLine', completeness: 'missing_command' } };
  const original = ownedIdentityFailure(error, 11); const failure = ownedIdentityFailure(error, 11, observation);
  assert.deepEqual(reads, { schema: 1, source: 1, subject: 1, snapshotCount: 1, candidateMatches: 1, subjectMatches: 1,
    candidateCommandState: 1, subjectCommandState: 1 });
  assert.equal(JSON.stringify(failure).includes(marker), false);
  assert.deepEqual(ownedIdentityFailure({ identityEvidenceFailure: failure }, 11), failure);
  const cases = [{ schema: 'unknown', source: marker }, { schema: 'sonnetdb.owned-candidate-snapshot.v1',
    get source() { throw new Error(marker); } }, new Proxy({}, { get() { throw new Error(marker); } })];
  const expires = Date.now() + 500;
  for (let index = 0; index < cases.length && index < 3; index += 1) {
    assert.ok(Date.now() < expires);
    const projected = ownedIdentityFailure(error, 11, cases[index]);
    assert.deepEqual(projected.candidateSnapshot, snapshotDiagnostic());
    const { candidateSnapshot: _observation, ...retained } = projected; assert.deepEqual(retained, original);
    assert.equal(JSON.stringify(projected).includes(marker), false);
  }
  const detail = { ...error.identityEvidenceFailure, get candidateSnapshot() { throw new Error(marker); } };
  const projected = ownedIdentityFailure({ identityEvidenceFailure: detail }, 11);
  assert.equal(projected.subreason, original.subreason); assert.deepEqual(projected.candidateSnapshot, snapshotDiagnostic());
});

test('snapshot presence observes credential rejections without retaining any command, error or extra hash fields', { timeout: 1000 }, async () => {
  const marker = 'WB52_SYNTHETIC_CREDENTIAL_/+never-persist!'; const candidate = child(11, marker);
  const identities = new Map([[10, owner]]); const events = []; let refreshes = 0;
  const failures = await captureOwnedCandidateSnapshot([owner, candidate], identities, [], { ...options,
    discover: (_snapshot, values) => values.set(11, candidate), recordEvent: (value) => events.push(value),
    validateText: (text) => { if (text.includes(marker)) throw new Error(marker); },
    refreshSnapshot: async () => { refreshes += 1; return []; } });
  assert.equal(failures.length, 1); assert.equal(failures[0].stage, 'unsafeText');
  assert.equal(failures[0].reason, 'unsafe_identity_text_preserved');
  assert.deepEqual(failures[0].candidateSnapshot, snapshotDiagnostic({ source: 'initial', snapshotCount: 2,
    candidateMatches: 1, candidateCommandState: 'present' }));
  const projected = ownedIdentityFailure({ message: marker, identityEvidenceFailure: failures[0] }, 11,
    { ...failures[0].candidateSnapshot, raw: marker, commandLine: marker, hash: marker, created: marker });
  assert.equal(JSON.stringify(projected).includes(marker), false); assert.deepEqual(projected, failures[0]);
  assert.equal(refreshes, 0); assert.deepEqual([...identities.values()], [owner]); assert.deepEqual(events, []);
});

test('runner preserves candidate snapshot failures through the existing bounded audit and terminal evidence pipeline', { timeout: 1000 }, async () => {
  const source = await readFile(new URL('./run-query-host-real.mjs', import.meta.url), 'utf8');
  assert.match(source, /const failures = await captureOwnedCandidateSnapshot\(current, identities, roots,/u);
  assert.match(source, /refreshSnapshot: \(\{ signal \}\) => snapshot\(final, signal\)/u);
  assert.match(source, /index < failures\.length && index < 128; index \+= 1\) noteAuditFailure\(failures\[index\]\)/u);
  assert.match(source, /if \(auditFailures\.length < 128\) auditFailures\.push\(\{ atUtc: new Date\(\)\.toISOString\(\), \.\.\.value \}\)/u);
  assert.match(source, /await evidence\('process-events\.json',[\s\S]*?acceptedIdentities: \[\.\.\.identities\.values\(\)\], auditFailures, auditFailureCount, auditFailureOverflow/u);
});

function cleanupFixture() {
  const accepted = child(11); const identities = new Map([[10, owner], [11, accepted]]);
  const failures = []; const stopped = []; const roots = [{ identity: accepted }];
  let now = 1000; let snapshots = 0;
  const request = { deadline: 100_000, ownerPid: 10, identities, helperStarts: [], clock: () => now,
    snapshot: async () => { snapshots += 1; return []; }, audit: async () => {},
    safeLiveIdentities: (values) => values.flatMap((value) => identities.has(value.pid) ? [identities.get(value.pid)] : []),
    stopVerified: async (value) => { validateOwnedIdentity(value, 10); stopped.push(value.pid); },
    noteAuditFailure: (value) => failures.push(value), delay: async () => {},
    verifyRoots: () => assert.ok(roots.every((root) => root.identity)),
    verifyAuditFailures: () => assert.equal(failures.some((failure) => failure.stage !== 'event'), false),
    observe: () => ({ acceptedIdentityCount: identities.size, rootCount: roots.length,
      rootIdentityCount: roots.filter((root) => Boolean(root.identity)).length, storedAuditFailureCount: failures.length,
      blockingAuditFailureCount: failures.filter((failure) => failure.stage !== 'event').length, auditFailureOverflow: 0 }) };
  return { request, accepted, failures, stopped, roots, snapshots: () => snapshots, advance: (milliseconds) => { now += milliseconds; } };
}

test('owned cleanup success retains exact helper filtering, depth-first accepted stops and its original proof gates', { timeout: 1000 }, async () => {
  const fixture = cleanupFixture(); const descendant = { ...child(12), parentPid: 11,
    parentChain: [fixture.accepted, ...fixture.accepted.parentChain] };
  fixture.request.identities.set(12, descendant);
  const helper = child(13); fixture.request.identities.set(13, helper);
  fixture.request.helperStarts.push({ pid: 13, closed: true }); let calls = 0;
  fixture.request.snapshot = async () => ++calls === 1 ? [owner, fixture.accepted, descendant, helper] : [owner, helper];
  const result = await verifyOwnedProcessCleanup(fixture.request);
  assert.equal(result.proven, true); assert.deepEqual(fixture.stopped, [12, 11]);
  assert.equal(result.diagnostic.firstRecoverableFailure, null); assert.equal(result.diagnostic.terminalFailure, null);
  assert.equal(result.diagnostic.structure.stopAttempts, 2); assert.equal(result.diagnostic.structure.finalLiveCount, 1);
  assert.equal(result.diagnostic.structure.remainingCount, 0); assert.equal(result.diagnostic.structure.roundsAttempted, 2);
  assert.equal(result.diagnostic.structure.finalSnapshotCount, 2);
  assert.deepEqual(result.diagnostic.finalChecks, { remainingProcesses: 'passed', rootIdentities: 'passed', auditFailures: 'passed' });
});

test('terminal residual, missing root and blocking audit each execute their mandatory checks while retaining the first refusal', { timeout: 1000 }, async () => {
  const fixture = cleanupFixture(); const calls = []; const before = structuredClone([...fixture.request.identities]);
  const verifyRoots = fixture.request.verifyRoots; const verifyAuditFailures = fixture.request.verifyAuditFailures;
  let liveCalls = 0; fixture.request.safeLiveIdentities = () => ++liveCalls === 2 ? [fixture.accepted] : [];
  fixture.roots[0].identity = null; fixture.failures.push({ stage: 'identity', reason: 'existing blocking refusal' });
  fixture.request.verifyRoots = () => { calls.push('roots'); verifyRoots(); };
  fixture.request.verifyAuditFailures = () => { calls.push('audit'); verifyAuditFailures(); };
  const result = await verifyOwnedProcessCleanup(fixture.request);
  assert.equal(result.proven, false); assert.deepEqual(calls, ['roots', 'audit']);
  assert.deepEqual(result.diagnostic.terminalFailure, { subcheck: 'remaining-processes', stage: 'final' });
  assert.equal(result.diagnostic.firstRecoverableFailure, null);
  assert.deepEqual(result.diagnostic.finalChecks, { remainingProcesses: 'refused', rootIdentities: 'refused', auditFailures: 'refused' });
  assert.equal(liveCalls, 2); assert.equal(fixture.snapshots(), 2); assert.deepEqual(fixture.stopped, []);
  assert.deepEqual([...fixture.request.identities], before); assert.equal(fixture.failures.length, 1);
  assert.equal(result.diagnostic.structure.remainingCount, 1); assert.equal(result.diagnostic.structure.stopAttempts, 0);
  assert.equal(result.diagnostic.structure.roundsAttempted, 1); assert.equal(result.diagnostic.structure.blockingAuditFailureCount, 1);
});

test('a residual refusal still executes passing root and audit checks without creating a stop or clearing the residue', { timeout: 1000 }, async () => {
  const fixture = cleanupFixture(); const calls = [];
  const verifyRoots = fixture.request.verifyRoots; const verifyAuditFailures = fixture.request.verifyAuditFailures;
  let liveCalls = 0; fixture.request.safeLiveIdentities = () => ++liveCalls === 2 ? [fixture.accepted] : [];
  fixture.request.verifyRoots = () => { calls.push('roots'); verifyRoots(); };
  fixture.request.verifyAuditFailures = () => { calls.push('audit'); verifyAuditFailures(); };
  const result = await verifyOwnedProcessCleanup(fixture.request);
  assert.equal(result.proven, false); assert.deepEqual(calls, ['roots', 'audit']);
  assert.deepEqual(result.diagnostic.finalChecks, { remainingProcesses: 'refused', rootIdentities: 'passed', auditFailures: 'passed' });
  assert.deepEqual(result.diagnostic.terminalFailure, { subcheck: 'remaining-processes', stage: 'final' });
  assert.equal(result.diagnostic.structure.remainingCount, 1); assert.equal(fixture.snapshots(), 2);
  assert.equal(liveCalls, 2); assert.deepEqual(fixture.stopped, []); assert.deepEqual(fixture.failures, []);
});

test('a refused root check cannot skip a blocking audit check or replace its first terminal failure', { timeout: 1000 }, async () => {
  const fixture = cleanupFixture(); const calls = [];
  const verifyRoots = fixture.request.verifyRoots; const verifyAuditFailures = fixture.request.verifyAuditFailures;
  fixture.roots[0].identity = null; fixture.failures.push({ stage: 'identity', reason: 'existing blocking refusal' });
  fixture.request.verifyRoots = () => { calls.push('roots'); verifyRoots(); };
  fixture.request.verifyAuditFailures = () => { calls.push('audit'); verifyAuditFailures(); };
  const result = await verifyOwnedProcessCleanup(fixture.request);
  assert.equal(result.proven, false); assert.deepEqual(calls, ['roots', 'audit']);
  assert.deepEqual(result.diagnostic.finalChecks, { remainingProcesses: 'passed', rootIdentities: 'refused', auditFailures: 'refused' });
  assert.deepEqual(result.diagnostic.terminalFailure, { subcheck: 'root-identities', stage: 'final' });
  assert.equal(fixture.failures.length, 1); assert.equal(result.diagnostic.structure.remainingCount, 0);
  assert.equal(fixture.snapshots(), 2); assert.deepEqual(fixture.stopped, []);
});

test('a final snapshot or live failure leaves mandatory checks not reached rather than inventing a refusal or invoking callbacks', { timeout: 1000 }, async () => {
  const fixture = cleanupFixture(); const calls = []; let snapshots = 0;
  fixture.request.snapshot = async () => { if (++snapshots === 2) throw new Error('synthetic snapshot deadline'); return []; };
  fixture.request.verifyRoots = () => { calls.push('roots'); };
  fixture.request.verifyAuditFailures = () => { calls.push('audit'); };
  const result = await verifyOwnedProcessCleanup(fixture.request);
  assert.equal(result.proven, false); assert.deepEqual(calls, []); assert.equal(snapshots, 2);
  assert.deepEqual(result.diagnostic.finalChecks, { remainingProcesses: 'not-reached', rootIdentities: 'not-reached', auditFailures: 'not-reached' });
  assert.deepEqual(result.diagnostic.terminalFailure, { subcheck: 'final-snapshot', stage: 'final' });
  assert.equal(result.diagnostic.structure.remainingCount, null); assert.deepEqual(fixture.stopped, []);
  const failedLive = cleanupFixture(); const liveCalls = []; let observations = 0;
  failedLive.request.safeLiveIdentities = () => { if (++observations === 2) throw new Error('synthetic final live deadline'); return []; };
  failedLive.request.verifyRoots = () => { liveCalls.push('roots'); };
  failedLive.request.verifyAuditFailures = () => { liveCalls.push('audit'); };
  const liveResult = await verifyOwnedProcessCleanup(failedLive.request);
  assert.equal(liveResult.proven, false); assert.deepEqual(liveCalls, []); assert.equal(observations, 2);
  assert.deepEqual(liveResult.diagnostic.finalChecks, { remainingProcesses: 'not-reached', rootIdentities: 'not-reached', auditFailures: 'not-reached' });
  assert.deepEqual(liveResult.diagnostic.terminalFailure, { subcheck: 'final-live', stage: 'final' });
  assert.equal(failedLive.snapshots(), 2); assert.deepEqual(failedLive.stopped, []);
});

test('earlier recoverable failure and fallback stop accounting survive three independent terminal refusals', { timeout: 1000 }, async () => {
  const fixture = cleanupFixture(); const calls = []; let snapshots = 0;
  const verifyRoots = fixture.request.verifyRoots; const verifyAuditFailures = fixture.request.verifyAuditFailures;
  fixture.request.snapshot = async () => { if (++snapshots === 1) throw new Error('synthetic round snapshot failure'); return [fixture.accepted]; };
  fixture.request.safeLiveIdentities = () => [fixture.accepted]; fixture.roots[0].identity = null;
  fixture.request.verifyRoots = () => { calls.push('roots'); verifyRoots(); };
  fixture.request.verifyAuditFailures = () => { calls.push('audit'); verifyAuditFailures(); };
  const result = await verifyOwnedProcessCleanup(fixture.request);
  assert.equal(result.proven, false); assert.deepEqual(calls, ['roots', 'audit']);
  assert.deepEqual(result.diagnostic.firstRecoverableFailure, { subcheck: 'round-snapshot', stage: 'round' });
  assert.deepEqual(result.diagnostic.terminalFailure, { subcheck: 'remaining-processes', stage: 'final' });
  assert.deepEqual(result.diagnostic.finalChecks, { remainingProcesses: 'refused', rootIdentities: 'refused', auditFailures: 'refused' });
  assert.equal(snapshots, 4); assert.deepEqual(fixture.stopped, [11, 11, 11]); assert.equal(fixture.failures.length, 1);
  assert.equal(result.diagnostic.structure.roundsAttempted, 3); assert.equal(result.diagnostic.structure.stopAttempts, 3);
  assert.equal(result.diagnostic.structure.roundSnapshotFailures, 1); assert.equal(result.diagnostic.structure.stopFailures, 0);
});

test('synchronous terminal callback exceptions retain fixed refusals and execute the later callback without persisting raw text', { timeout: 1000 }, async () => {
  const fixture = cleanupFixture(); const marker = 'Bearer WB56_SYNTHETIC_DEADLINE_SECRET_123456789'; const calls = [];
  fixture.request.verifyRoots = () => { calls.push('roots'); throw new Error(`synthetic deadline ${marker}`); };
  fixture.request.verifyAuditFailures = () => { calls.push('audit'); throw new TypeError(marker); };
  const result = await verifyOwnedProcessCleanup(fixture.request);
  assert.equal(result.proven, false); assert.deepEqual(calls, ['roots', 'audit']);
  assert.deepEqual(result.diagnostic.terminalFailure, { subcheck: 'root-identities', stage: 'final' });
  assert.deepEqual(result.diagnostic.finalChecks, { remainingProcesses: 'passed', rootIdentities: 'refused', auditFailures: 'refused' });
  assert.equal(JSON.stringify(result).includes(marker), false); assert.equal(JSON.stringify(result).includes('synthetic deadline'), false);
  assert.equal(fixture.snapshots(), 2); assert.deepEqual(fixture.stopped, []); assert.deepEqual(fixture.failures, []);
});

test('terminal check projection reads changing getters once and remains idempotent with only fixed keys and states', { timeout: 1000 }, () => {
  const marker = 'Bearer WB56_SYNTHETIC_GETTER_SECRET_123456789'; const reads = { checks: 0, remaining: 0, roots: 0, audit: 0 };
  const checks = {
    get remainingProcesses() { return ++reads.remaining === 1 ? 'refused' : marker; },
    get rootIdentities() { return ++reads.roots === 1 ? 'passed' : marker; },
    get auditFailures() { return ++reads.audit === 1 ? 'not-reached' : marker; }, raw: marker };
  const result = ownedProcessCleanupDiagnostic({ observation: 'complete', firstRecoverableFailure: null,
    terminalFailure: { subcheck: 'remaining-processes', stage: 'final' }, structure: { remainingCount: 1 },
    get finalChecks() { return ++reads.checks === 1 ? checks : { raw: marker }; } });
  assert.deepEqual(reads, { checks: 1, remaining: 1, roots: 1, audit: 1 });
  assert.deepEqual(result.finalChecks, { remainingProcesses: 'refused', rootIdentities: 'passed', auditFailures: 'not-reached' });
  assert.deepEqual(ownedProcessCleanupDiagnostic(result), result); assert.equal(JSON.stringify(result).includes(marker), false);
  assert.deepEqual(Object.keys(result.finalChecks), ['remainingProcesses', 'rootIdentities', 'auditFailures']);
  assert.equal(result.schema, 'sonnetdb.owned-process-cleanup.v1');
});

test('terminal check getter failures and invalid states become unknown without degrading prior cleanup labels or counts', { timeout: 1000 }, () => {
  const marker = 'Bearer WB56_SYNTHETIC_PROJECTOR_SECRET_123456789';
  const base = { observation: 'complete', firstRecoverableFailure: { subcheck: 'round-audit', stage: 'round' },
    terminalFailure: { subcheck: 'remaining-processes', stage: 'final' }, structure: { remainingCount: 1, blockingAuditFailureCount: 7 } };
  const partial = ownedProcessCleanupDiagnostic({ ...base, finalChecks: {
    get remainingProcesses() { throw new Error(marker); }, rootIdentities: 'unknown', auditFailures: marker } });
  const throwing = ownedProcessCleanupDiagnostic({ ...base, get finalChecks() { throw new Error(marker); } });
  assert.deepEqual(partial.finalChecks, { remainingProcesses: 'unknown', rootIdentities: 'unknown', auditFailures: 'unknown' });
  assert.deepEqual(throwing, partial); assert.equal(partial.observation, 'complete');
  assert.deepEqual(partial.firstRecoverableFailure, base.firstRecoverableFailure);
  assert.deepEqual(partial.terminalFailure, base.terminalFailure);
  assert.equal(partial.structure.remainingCount, 1); assert.equal(partial.structure.blockingAuditFailureCount, 7);
  assert.equal(JSON.stringify(partial).includes(marker), false); assert.deepEqual(ownedProcessCleanupDiagnostic(partial), partial);
});

test('owned cleanup terminal throws identify every fixed check without persisting error or process text', { timeout: 2000 }, async () => {
  const marker = 'WB49_SYNTHETIC_RAW_ERROR_Bearer_credential_never_persist';
  const cases = [
    ['cleanup-budget', (f) => { f.request.clock = () => { throw new Error(marker); }; }],
    ['round-live', (f) => { f.request.safeLiveIdentities = () => { throw new Error(marker); }; }],
    ['live-count', (f) => { f.request.safeLiveIdentities = () => Array.from({ length: 129 }, () => f.accepted); }],
    ['stop-round-budget', (f) => { f.request.safeLiveIdentities = () => { f.advance(45_000); return [f.accepted]; }; }],
    ['round-delay', (f) => { f.request.safeLiveIdentities = () => [f.accepted]; f.request.delay = async () => { throw new Error(marker); }; }],
    ['final-snapshot', (f) => { let calls = 0; f.request.snapshot = async () => { if (++calls === 2) throw new Error(marker); return []; }; }],
    ['final-live', (f) => { let calls = 0; f.request.safeLiveIdentities = () => { if (++calls === 2) throw new Error(marker); return []; }; }],
    ['remaining-processes', (f) => { let calls = 0; f.request.safeLiveIdentities = () => ++calls === 2 ? [f.accepted] : []; }],
    ['root-identities', (f) => { f.roots[0].identity = null; }],
    ['audit-failures', (f) => { f.failures.push({ stage: 'identity', reason: marker }); }],
  ];
  const expires = Date.now() + 1500;
  for (let index = 0; index < cases.length && index < 10; index += 1) {
    assert.ok(Date.now() < expires); const [subcheck, inject] = cases[index]; const fixture = cleanupFixture(); inject(fixture);
    const result = await verifyOwnedProcessCleanup(fixture.request);
    assert.equal(result.proven, false, subcheck); assert.equal(result.diagnostic.terminalFailure.subcheck, subcheck);
    assert.equal(result.diagnostic.terminalFailure.stage, subcheck === 'cleanup-budget' ? 'initial'
      : ['round-live', 'live-count', 'stop-round-budget', 'round-delay'].includes(subcheck) ? 'round' : 'final');
    assert.equal(JSON.stringify(result.diagnostic).includes(marker), false);
    assert.equal(JSON.stringify(result.diagnostic).includes('owned child'), false);
    if (subcheck === 'stop-round-budget' || subcheck === 'live-count') assert.deepEqual(fixture.stopped, []);
  }
});

test('recoverable snapshot, audit and verified-stop failures retain their first check and unchanged audit accounting', { timeout: 2000 }, async () => {
  const cases = [
    ['round-snapshot', (f) => { let calls = 0; f.request.snapshot = async () => { if (++calls === 1) throw new Error('raw snapshot'); return []; }; }],
    ['round-audit', (f) => { let calls = 0; f.request.audit = async () => { if (++calls === 1) { f.request.noteAuditFailure({ stage: 'identity' }); throw new Error('raw audit'); } }; }],
    ['stop-verification', (f) => { let calls = 0; f.request.safeLiveIdentities = () => ++calls === 1 ? [f.accepted] : [];
      f.request.stopVerified = async () => { throw new Error('raw stop'); }; }],
    ['final-audit', (f) => { let calls = 0; f.request.audit = async () => { if (++calls === 2) { f.request.noteAuditFailure({ stage: 'identity' }); throw new Error('raw final audit'); } }; }],
  ];
  const expires = Date.now() + 1500;
  for (let index = 0; index < cases.length && index < 4; index += 1) {
    assert.ok(Date.now() < expires); const [subcheck, inject] = cases[index]; const fixture = cleanupFixture(); inject(fixture);
    const result = await verifyOwnedProcessCleanup(fixture.request);
    assert.equal(result.proven, false); assert.equal(result.diagnostic.firstRecoverableFailure.subcheck, subcheck);
    assert.equal(result.diagnostic.terminalFailure.subcheck, 'audit-failures');
    assert.equal(fixture.failures.length, 1); assert.equal(result.diagnostic.structure.storedAuditFailureCount, 1);
    assert.deepEqual(fixture.stopped, subcheck === 'round-snapshot' ? [11] : []);
  }
});

test('owned cleanup bounds remain three rounds, 128 identities per round, forty-five seconds and the thirty-five second reserve', { timeout: 1000 }, async () => {
  const fixture = cleanupFixture(); fixture.request.safeLiveIdentities = () => [fixture.accepted];
  const result = await verifyOwnedProcessCleanup(fixture.request);
  assert.equal(result.proven, false); assert.equal(result.diagnostic.structure.roundsAttempted, 3);
  assert.equal(result.diagnostic.structure.stopAttempts, 3); assert.equal(result.diagnostic.terminalFailure.subcheck, 'remaining-processes');
  const reserved = cleanupFixture(); reserved.request.deadline = 36_000;
  const final = await verifyOwnedProcessCleanup(reserved.request);
  assert.equal(final.proven, true); assert.equal(final.diagnostic.structure.roundsAttempted, 0); assert.equal(reserved.snapshots(), 1);
  const maximum = cleanupFixture(); maximum.request.safeLiveIdentities = () => Array.from({ length: 128 }, () => maximum.accepted);
  const bounded = await verifyOwnedProcessCleanup(maximum.request);
  assert.equal(bounded.diagnostic.structure.stopAttempts, 384); assert.equal(bounded.diagnostic.structure.roundsAttempted, 3);
});

test('closed helper exact PID does not exclude its accepted descendant or weaken the independent final-live gate', { timeout: 1000 }, async () => {
  const fixture = cleanupFixture(); fixture.request.helperStarts.push({ pid: 11, closed: true });
  const descendant = { ...child(12), parentPid: 11, parentChain: [fixture.accepted, ...fixture.accepted.parentChain] };
  fixture.request.identities.set(12, descendant); fixture.request.safeLiveIdentities = () => [fixture.accepted, descendant];
  const result = await verifyOwnedProcessCleanup(fixture.request);
  assert.deepEqual(fixture.stopped, [12, 12, 12]); assert.equal(result.proven, false);
  assert.equal(result.diagnostic.structure.finalLiveCount, 2); assert.equal(result.diagnostic.structure.remainingCount, 1);
});

test('diagnostic observation failure stays unknown and cannot alter passed or failed cleanup authority', { timeout: 1000 }, async () => {
  const passed = cleanupFixture(); const failed = cleanupFixture(); failed.roots[0].identity = null;
  const observe = () => { throw new Error('Bearer SYNTHETIC_OBSERVATION_SECRET_12345'); };
  passed.request.observe = observe; failed.request.observe = observe;
  const results = await Promise.all([verifyOwnedProcessCleanup(passed.request), verifyOwnedProcessCleanup(failed.request)]);
  assert.equal(results[0].proven, true); assert.equal(results[1].proven, false);
  assert.deepEqual(results[0].diagnostic, results[1].diagnostic);
  assert.equal(results[0].diagnostic.observation, 'unknown');
  assert.deepEqual(results[0].diagnostic.terminalFailure, { subcheck: 'unknown', stage: 'unknown' });
  assert.deepEqual(results[0].diagnostic.finalChecks, { remainingProcesses: 'unknown', rootIdentities: 'unknown', auditFailures: 'unknown' });
  assert.ok(Object.values(results[0].diagnostic.structure).every((value) => value === null));
});

test('event-only audit preserves process proof while audit overflow still rejects its original OR gate', { timeout: 1000 }, async () => {
  const eventOnly = cleanupFixture(); eventOnly.failures.push({ stage: 'event', reason: 'secondary_event_failed' });
  const observed = await verifyOwnedProcessCleanup(eventOnly.request);
  assert.equal(observed.proven, true); assert.equal(observed.diagnostic.terminalFailure, null);
  assert.equal(observed.diagnostic.structure.storedAuditFailureCount, 1);
  assert.equal(observed.diagnostic.structure.blockingAuditFailureCount, 0);
  assert.deepEqual(observed.diagnostic.finalChecks, { remainingProcesses: 'passed', rootIdentities: 'passed', auditFailures: 'passed' });
  // The runner's independent auditFailures.length outcome gate still retains this event failure.
  assert.equal(eventOnly.failures.length, 1);
  const overflow = cleanupFixture(); const auditFailureOverflow = true; const originalObserve = overflow.request.observe;
  overflow.request.verifyAuditFailures = () => assert.equal(auditFailureOverflow
    || overflow.failures.some((failure) => failure.stage !== 'event'), false);
  overflow.request.observe = () => ({ ...originalObserve(), auditFailureOverflow: Number(auditFailureOverflow) });
  const rejected = await verifyOwnedProcessCleanup(overflow.request);
  assert.equal(rejected.proven, false); assert.equal(rejected.diagnostic.terminalFailure.subcheck, 'audit-failures');
  assert.equal(rejected.diagnostic.structure.auditFailureOverflow, 1);
  assert.deepEqual(rejected.diagnostic.finalChecks, { remainingProcesses: 'passed', rootIdentities: 'passed', auditFailures: 'refused' });
});

test('cleanup projection accepts only fixed paired labels and bounded integers, reading changing getters once', { timeout: 1000 }, () => {
  const marker = 'Bearer SYNTHETIC_GETTER_SECRET_123456789'; const reads = { subcheck: 0, stage: 0, stops: 0 };
  const result = ownedProcessCleanupDiagnostic({ observation: 'complete', firstRecoverableFailure: null,
    terminalFailure: { get subcheck() { return ++reads.subcheck === 1 ? 'final-snapshot' : marker; },
      get stage() { return ++reads.stage === 1 ? 'final' : marker; }, raw: marker },
    structure: { roundsAttempted: 4, roundSnapshotFailures: -1, roundAuditFailures: NaN,
      get stopAttempts() { return ++reads.stops === 1 ? 384 : marker; }, stopFailures: 385, acceptedIdentityCount: 129,
      finalSnapshotCount: 4097, finalLiveCount: 129, remainingCount: 0, rootCount: 3, rootIdentityCount: 2,
      storedAuditFailureCount: 129, blockingAuditFailureCount: 128, auditFailureOverflow: 2, commandLine: marker } });
  assert.deepEqual(reads, { subcheck: 1, stage: 1, stops: 1 });
  assert.equal(result.structure.stopAttempts, 384); assert.equal(result.structure.rootIdentityCount, 2);
  assert.equal(result.structure.remainingCount, 0); assert.equal(result.structure.blockingAuditFailureCount, 128);
  assert.equal(result.structure.roundsAttempted, null); assert.equal(result.structure.auditFailureOverflow, null);
  assert.equal(JSON.stringify(result).includes(marker), false);
  const mismatch = ownedProcessCleanupDiagnostic({ observation: 'complete', structure: {},
    firstRecoverableFailure: { subcheck: 'final-snapshot', stage: 'round' }, terminalFailure: null });
  assert.deepEqual(mismatch.firstRecoverableFailure, { subcheck: 'unknown', stage: 'unknown' });
  assert.equal(mismatch.terminalFailure, null);
  const throwing = ownedProcessCleanupDiagnostic({ observation: 'complete', firstRecoverableFailure: null, terminalFailure: null,
    get structure() { throw new Error(marker); } });
  assert.equal(throwing.observation, 'unknown'); assert.equal(JSON.stringify(throwing).includes(marker), false);
});

test('unaccepted missing or credential-bearing identities never enter fallback stops and later steps still execute', { timeout: 1000 }, async () => {
  const fixture = cleanupFixture(); const marker = 'WB49_REJECTED_CREDENTIAL_123456789';
  const missing = child(12, null); const credential = child(13, marker);
  const failures = captureOwnedSnapshot([], fixture.request.identities, [], { ...options,
    discover: (_snapshot, values) => { values.set(12, missing); values.set(13, credential); },
    validateText: (text) => { if (text.includes(marker)) throw new Error(marker); } });
  assert.equal(fixture.request.identities.has(12), false); assert.equal(fixture.request.identities.has(13), false);
  let calls = 0; fixture.request.snapshot = async () => { if (++calls === 1) throw new Error(marker); return []; };
  const completed = []; let diagnosis;
  const steps = await attemptIndependentSteps([{ name: 'owned-processes', run: async () => {
    const cleanup = await verifyOwnedProcessCleanup(fixture.request); diagnosis = cleanup.diagnostic; assert.equal(cleanup.proven, true);
  } }, { name: 'reserved-ports', run: async () => { completed.push('ports'); } },
  { name: 'helper-handles', run: async () => { completed.push('helpers'); } },
  { name: 'terminal', run: async () => { completed.push('terminal'); } }], { deadline: 2000, clock: () => 1000 });
  assert.deepEqual(fixture.stopped, [11]); assert.deepEqual(completed, ['ports', 'helpers', 'terminal']);
  assert.equal(steps[0].ok, false); assert.equal(diagnosis.firstRecoverableFailure.subcheck, 'round-snapshot');
  assert.equal(JSON.stringify([diagnosis, failures]).includes(marker), false);
  assert.equal(JSON.stringify(diagnosis).includes(createHash('sha256').update(marker).digest('hex').toUpperCase()), false);
});

test('one exact fresh batch restores missing and empty own commands with a ledger-bound fixed observation', { timeout: 1000 }, async () => {
  const first = child(11, null); const second = child(13, ''); const initial = [owner, first, second];
  const identities = new Map([[10, owner]]); const events = []; let refreshes = 0;
  const failures = await captureOwnedCandidateSnapshot(initial, identities, [], { ...options,
    discover: (_snapshot, values) => { values.set(11, first); values.set(13, second); },
    refreshSnapshot: async () => { refreshes += 1; return [owner, child(11, 'fresh complete command'), child(13, 'fresh second command')]; },
    recordEvent: (value) => recordOwnedIdentityEvent(value, identities, events, options) });
  assert.deepEqual(failures, []); assert.equal(refreshes, 1);
  assert.equal(identities.get(11).commandLine, 'fresh complete command');
  assert.deepEqual(identities.get(11).parentChain, [{ pid: 10, parentPid: 1, created, commandLine: 'node runner' }]);
  assert.deepEqual(events.map((value) => [value.candidateCommandRechecked, value.initialCommandState]), [[true, 'missing'], [true, 'empty']]);
  assert.deepEqual(events[0].identityLedgerRef, { pid: 11, created });
  assert.equal(Object.hasOwn(events[0], 'commandLine'), false);
});

test('fresh absence, PID reuse, parent changes, duplicates and remaining missing commands never become authority', { timeout: 1000 }, async () => {
  const freshChild = { ...pendingCandidate, commandLine: 'fresh complete command' };
  const cases = [
    [[owner, intermediate], 'candidate_snapshot_missing'],
    [[owner, intermediate, { ...freshChild, created: '2026-10-07T00:01:00.000Z' }], 'candidate_creation_changed'],
    [[owner, intermediate, { ...freshChild, parentPid: 10 }], 'candidate_parent_changed'],
    [[owner, intermediate, freshChild, { ...freshChild }], 'candidate_snapshot_duplicate'],
    [[owner, intermediate, { ...freshChild, commandLine: null }], 'identity_command_missing'],
    [[owner, intermediate, { ...freshChild, commandLine: '' }], 'identity_command_empty'],
    [[owner, intermediate, { ...freshChild, commandLine: 123 }], 'identity_command_invalid'],
  ];
  const expires = Date.now() + 800;
  for (let index = 0; index < cases.length && index < 7; index += 1) {
    assert.ok(Date.now() < expires); const identities = new Map([[10, owner]]); let refreshes = 0;
    const failures = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions,
      refreshSnapshot: async () => { refreshes += 1; return cases[index][0]; } });
    assert.equal(failures.length, 1); assert.equal(failures[0].subreason, cases[index][1]);
    assert.equal(refreshes, 1); assert.equal(identities.has(11), false);
    assert.equal(JSON.stringify(failures).includes('fresh complete command'), false);
  }
});

test('fresh authority requires every original complete parent and the exact live ledger anchor', { timeout: 1000 }, async () => {
  const freshChild = { ...pendingCandidate, commandLine: 'fresh complete command' };
  const cases = [
    [[owner, freshChild], 'candidate_snapshot_missing'],
    [[owner, { ...intermediate, commandLine: 'changed parent' }, freshChild], 'candidate_parent_tuple_changed'],
    [[owner, { ...intermediate, created: '2026-10-06T00:00:00.000Z' }, freshChild], 'candidate_parent_tuple_changed'],
    [[owner, { ...intermediate, commandLine: null }, freshChild], 'parent_command_missing'],
    [[intermediate, freshChild], 'candidate_snapshot_missing'],
    [[{ ...owner, commandLine: 'changed anchor' }, intermediate, freshChild], 'ownership_anchor_tuple_mismatch'],
    [[owner, { ...owner }, intermediate, freshChild], 'candidate_snapshot_duplicate'],
  ];
  const expires = Date.now() + 800;
  for (let index = 0; index < cases.length && index < 7; index += 1) {
    assert.ok(Date.now() < expires); const identities = new Map([[10, owner]]);
    const failures = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions,
      refreshSnapshot: async () => cases[index][0] });
    assert.equal(failures[0].subreason, cases[index][1]); assert.equal(identities.has(11), false);
  }
});

test('invalid initial tuples and incomplete initial authority cannot trigger a command refresh', { timeout: 1000 }, async () => {
  const cases = [
    { ...pendingCandidate, pid: 0 }, { ...pendingCandidate, parentPid: -1 },
    { ...pendingCandidate, created: 'invalid original creation' }, { ...pendingCandidate, commandLine: 123 },
  ];
  const expires = Date.now() + 800;
  for (let index = 0; index < cases.length && index < 4; index += 1) {
    assert.ok(Date.now() < expires); const value = cases[index]; const identities = new Map([[10, owner]]); let refreshes = 0;
    const failures = await captureOwnedCandidateSnapshot([owner, intermediate, value], identities, [], { ...options,
      discover: (_snapshot, values) => values.set(value.pid, value), refreshSnapshot: async () => { refreshes += 1; return []; } });
    assert.ok(failures.length > 0); assert.equal(refreshes, 0); assert.equal(identities.size, 1);
  }
  const identities = new Map([[10, owner]]); let refreshes = 0;
  const duplicate = await captureOwnedCandidateSnapshot([...initialCandidates(), { ...pendingCandidate }], identities, [], {
    ...candidateOptions, refreshSnapshot: async () => { refreshes += 1; return initialCandidates(); } });
  assert.equal(duplicate[0].subreason, 'candidate_snapshot_duplicate'); assert.equal(refreshes, 0); assert.equal(identities.size, 1);
  const failures = await captureOwnedCandidateSnapshot([owner, { ...intermediate, commandLine: null }, pendingCandidate], identities, [], {
    ...candidateOptions, refreshSnapshot: async () => { refreshes += 1; return initialCandidates(); } });
  assert.equal(failures[0].subreason, 'parent_command_missing'); assert.equal(refreshes, 0);
});

test('current authority normalization permits diagnostic depth while retaining the original true twelve-hop limit', { timeout: 1000 }, async () => {
  const external = Array.from({ length: 12 }, (_value, index) => ({ pid: index + 101, parentPid: index === 11 ? 0 : index + 102,
    created, commandLine: `external ${index}` }));
  const own = { ...owner, parentPid: 101 }; const value = { ...child(11), parentChain: [own, ...external] };
  const identities = new Map([[10, own]]);
  const failures = await captureOwnedCandidateSnapshot([own, value, ...external], identities, [], { ...candidateOptions });
  assert.deepEqual(failures, []); assert.equal(identities.get(11).parentChain.length, 1);
  assert.equal(identities.get(11).externalAncestorCount, 12);
  const limitParents = Array.from({ length: 11 }, (_value, index) => ({ pid: index + 20, parentPid: index === 10 ? 10 : index + 21,
    created, commandLine: 'complete exact-limit parent' }));
  const exactLimit = { ...child(11), parentPid: 20, parentChain: [...limitParents, owner] };
  const limitLedger = new Map([[10, owner]]);
  const limitFailures = await captureOwnedCandidateSnapshot([owner, exactLimit, ...limitParents], limitLedger, [], { ...candidateOptions });
  assert.deepEqual(limitFailures, []); assert.equal(limitLedger.get(11).parentChain.length, 12);
  assert.deepEqual(limitLedger.get(11).parentChain,
    exactLimit.parentChain.map(({ pid, parentPid, created: time, commandLine }) => ({ pid, parentPid, created: time, commandLine })));
  const parents = Array.from({ length: 12 }, (_value, index) => ({ pid: index + 20, parentPid: index === 11 ? 10 : index + 21,
    created, commandLine: 'complete deep parent' }));
  const tooDeep = { ...child(11), parentPid: 20, parentChain: [...parents, owner] }; const deepLedger = new Map([[10, owner]]);
  const rejected = await captureOwnedCandidateSnapshot([owner, tooDeep, ...parents], deepLedger, [], { ...candidateOptions });
  assert.equal(rejected[0].subreason, 'parent_chain_length_exceeded'); assert.equal(deepLedger.has(11), false);
  assert.throws(() => validateOwnedIdentity(tooDeep, 10), (error) => {
    const failure = ownedIdentityFailure(error, 11);
    assert.equal(failure.subreason, 'parent_chain_length_exceeded'); assert.equal(failure.failedField, 'parentChain');
    assert.equal(failure.completeness, 'over_limit'); return true;
  });
});

test('current snapshot cycles and discovered discontinuity stay fixed failures without retry', { timeout: 1000 }, async () => {
  const cases = [
    [[owner, { ...intermediate, parentPid: 11 }, pendingCandidate], 'parent_chain_cycle'],
    [[owner, intermediate, { ...pendingCandidate, parentChain: [owner] }], 'candidate_parent_tuple_changed'],
  ];
  const expires = Date.now() + 800;
  for (let index = 0; index < cases.length && index < 2; index += 1) {
    assert.ok(Date.now() < expires); const identities = new Map([[10, owner]]); let refreshes = 0;
    const failures = await captureOwnedCandidateSnapshot(cases[index][0], identities, [], { ...candidateOptions,
      refreshSnapshot: async () => { refreshes += 1; return initialCandidates(); } });
    assert.equal(failures[0].subreason, cases[index][1]); assert.equal(refreshes, 0); assert.equal(identities.has(11), false);
  }
});

test('raw initial and fresh external credential text is rejected before omission or hashing', { timeout: 1000 }, async () => {
  const marker = 'WB46_RAW_EXTERNAL_SYNTHETIC_SECRET';
  const validateText = (text) => { if (text.includes(marker)) throw new Error(marker); };
  const rawInitial = { ...pendingCandidate, parentChain: [intermediate, owner, { pid: 1, commandLine: marker }] };
  const identities = new Map([[10, owner]]); let refreshes = 0;
  const first = await captureOwnedCandidateSnapshot([owner, intermediate, rawInitial], identities, [], { ...candidateOptions, validateText,
    refreshSnapshot: async () => { refreshes += 1; return []; } });
  assert.equal(first[0].reason, 'unsafe_identity_text_preserved'); assert.equal(refreshes, 0); assert.equal(identities.has(11), false);
  const second = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions, validateText,
    refreshSnapshot: async () => [owner, intermediate, { ...pendingCandidate, commandLine: 'fresh owned command' },
      { pid: 1, parentPid: 0, created, commandLine: marker }] });
  assert.equal(second[0].reason, 'unsafe_identity_text_preserved'); assert.equal(identities.has(11), false);
  assert.equal(JSON.stringify([first, second, [...identities.values()]]).includes(marker), false);
});

test('partial discovery and secondary event failure retain complete fresh primary authority independently', { timeout: 1000 }, async () => {
  const identities = new Map([[10, owner]]); let now = 1000;
  const failures = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions, clock: () => now,
    discover: (snapshot, values) => { values.set(11, snapshot.find((value) => value.pid === 11)); now += 3000; throw new Error('partial traversal'); },
    refreshSnapshot: async () => [owner, intermediate, { ...pendingCandidate, commandLine: 'complete fresh primary authority' }],
    recordEvent: () => { throw new Error('secondary sink failure'); } });
  assert.deepEqual(failures.map((value) => value.reason), ['partial_discovery_failed', 'secondary_event_failed']);
  assert.equal(identities.get(11).commandLine, 'complete fresh primary authority');
  assert.deepEqual(identities.get(11).parentChain.map((value) => value.pid), [12, 10]);
});

test('snapshot count and preparation deadlines stay bounded and never trigger blind refresh', { timeout: 1000 }, async () => {
  let refreshes = 0; const identities = new Map([[10, owner]]);
  const oversized = await captureOwnedCandidateSnapshot(Array.from({ length: 4097 }, () => owner), identities, [], {
    ...candidateOptions, refreshSnapshot: async () => { refreshes += 1; return []; } });
  assert.equal(oversized[0].subreason, 'candidate_snapshot_count_exceeded'); assert.equal(refreshes, 0);
  const freshOversized = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions,
    refreshSnapshot: async () => { refreshes += 1; return Array.from({ length: 4097 }, () => owner); } });
  assert.equal(freshOversized[0].subreason, 'candidate_snapshot_count_exceeded'); assert.equal(refreshes, 1); assert.equal(identities.has(11), false);
  let ticks = 0;
  const expired = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions,
    clock: () => ticks++ === 0 ? 1000 : 4000, refreshSnapshot: async () => { refreshes += 1; return []; } });
  assert.equal(expired[0].subreason, 'candidate_snapshot_deadline'); assert.equal(identities.has(11), false); assert.equal(refreshes, 1);
});

test('a hung or late fresh callback is cancelled once and cannot admit after a terminal timeout', { timeout: 1000 }, async () => {
  const identities = new Map([[10, owner]]); let release; let signal; let calls = 0;
  const failures = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions, refreshMilliseconds: 20,
    refreshSnapshot: (request) => { calls += 1; signal = request.signal; return new Promise((resolve) => { release = resolve; }); } });
  assert.equal(calls, 1); assert.equal(signal.aborted, true); assert.equal(failures[0].subreason, 'candidate_refresh_failed');
  assert.equal(identities.has(11), false);
  release([owner, intermediate, { ...pendingCandidate, commandLine: 'late command must not be admitted' }]);
  await Promise.resolve(); await Promise.resolve();
  assert.equal(identities.has(11), false); assert.equal(failures[0].subreason, 'candidate_refresh_failed');
});

test('the cumulative one-second intake flush preserves its partial primary ledger when the sink consumes its remainder', { timeout: 1000 }, async () => {
  const identities = new Map([[10, owner]]); const first = child(11); const second = child(13); let now = 1000; let refreshes = 0;
  const failures = await captureOwnedCandidateSnapshot([owner, first, second], identities, [], { ...options, clock: () => now,
    discover: (_snapshot, values) => { values.set(11, first); values.set(13, second); },
    refreshSnapshot: async () => { refreshes += 1; return []; }, recordEvent: () => { now += 1000; } });
  assert.equal(identities.get(11).commandLine, 'owned child'); assert.equal(identities.has(13), false);
  assert.equal(failures[0].subreason, 'candidate_snapshot_deadline'); assert.equal(refreshes, 0);
});

test('partial discovery retains accepted identities and a traversal failure', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]);
  const events = [];
  const failures = captureOwnedSnapshot([], identities, [], { ...options, recordEvent: (value) => events.push(value),
    discover: (_snapshot, values) => { values.set(11, child(11)); throw new Error('injected traversal failure'); } });
  assert.equal(identities.get(11).commandLine, 'owned child');
  assert.equal(events[0].pid, 11);
  assert.deepEqual(failures, [{ stage: 'discovery', reason: 'partial_discovery_failed' }]);
});

test('expired discovery still flushes its safe partial ledger under a fresh bounded clock', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]); let now = 1000;
  const failures = captureOwnedSnapshot([], identities, [], { ...options, clock: () => now,
    discover: (_snapshot, values) => { values.set(11, child(11)); now += 3000; throw new Error('injected discovery deadline'); } });
  assert.equal(identities.get(11).pid, 11);
  assert.equal(failures[0].reason, 'partial_discovery_failed');
});

test('secondary event failure preserves the full safety ledger for independent cleanup', { timeout: 1000 }, async () => {
  const identities = new Map([[10, owner]]);
  const failures = captureOwnedSnapshot([], identities, [], { ...options, recordEvent: () => { throw new Error('injected log failure'); },
    discover: (_snapshot, values) => values.set(11, child(11)) });
  assert.equal(identities.get(11).parentChain[0].commandLine, 'node runner');
  assert.deepEqual(failures, [{ stage: 'event', pid: 11, reason: 'secondary_event_failed' }]);
  const stopped = [];
  const steps = await attemptIndependentSteps([{ name: 'audit', run: async () => { throw new Error('injected audit failure'); } },
    { name: 'safe-stop', run: async () => { validateOwnedIdentity(identities.get(11), 10); stopped.push(11); } }], { deadline: 2000, clock: () => 1000 });
  assert.deepEqual(stopped, [11]);
  assert.equal(steps[0].ok, false);
  assert.equal(steps[1].ok, true);
  // A successful cleanup never converts an earlier audit failure into PASS.
  assert.equal(failures.length === 0 && steps.every((step) => step.ok), false);
});

test('incomplete descendants remain unaccepted and cannot authorize cleanup', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]);
  const failures = captureOwnedSnapshot([], identities, [], { ...options,
    discover: (_snapshot, values) => values.set(11, child(11, null)) });
  assert.equal(identities.has(11), false);
  assert.deepEqual(failures, [{ stage: 'identity', pid: 11, reason: 'incomplete_or_unsafe_identity_preserved',
    subreason: 'identity_command_missing', failedField: 'commandLine', completeness: 'missing_command', chainIndex: null,
    structure: { candidatePid: 11, candidateParentPid: 10, ownerPid: 10, parentChainCount: 2, expectedParentPid: null, observedParentPid: null } }]);
});

test('a missing command before the ownership anchor cannot authorize cleanup', { timeout: 1000 }, () => {
  assert.throws(() => validateOwnedIdentity({ ...child(11), parentPid: 12, parentChain: [
    { pid: 12, parentPid: 10, created, commandLine: null },
    { pid: 10, parentPid: 1, created, commandLine: 'node runner' },
  ] }, 10));
});

test('external ancestors after the full ownership anchor remain diagnostic only', { timeout: 1000 }, () => {
  const external = { ...child(11), parentChain: [
    { pid: 10, parentPid: 1, created, commandLine: 'node runner' },
    { pid: 1, parentPid: 90, created, commandLine: null },
  ] };
  assert.doesNotThrow(() => validateOwnedIdentity(external, 10));
  const ledger = new Map();
  acceptOwnedIdentity(external, ledger, { ...options, eventName: 'helper' });
  assert.equal(ledger.get(11).ownershipAnchorPid, 10);
  assert.equal(ledger.get(11).externalAncestorsDiagnosticOnly, true);
  assert.equal(ledger.get(11).parentChain.length, 1);
  assert.equal(ledger.get(11).parentChain[0].commandLine, 'node runner');
  assert.equal(ledger.get(11).externalAncestorCount, 1);
  assert.equal(ledger.get(11).externalAncestorsOmitted, true);
  assert.equal(ledger.get(11).externalAncestorsSha256,
    createHash('sha256').update(JSON.stringify(external.parentChain.slice(1))).digest('hex').toUpperCase());
  assert.doesNotThrow(() => validateOwnedIdentity({ ...owner, parentChain: [
    { pid: 1, parentPid: 90, created, commandLine: null },
  ] }, 10));
});

test('PID reuse and unanchored parent chains cannot replace accepted ownership', { timeout: 1000 }, () => {
  const identities = new Map([[11, child(11)]]);
  assert.throws(() => acceptOwnedIdentity({ ...child(11), created: '2026-10-07T00:01:00.000Z' }, identities, { ...options, eventName: 'helper' }));
  assert.equal(identities.get(11).created, created);
  assert.throws(() => validateOwnedIdentity({ ...child(12), parentPid: 90, parentChain: [{ pid: 90, unavailable: true }] }, 10));
});

test('terminal writers remain independent after detail and manifest failures', { timeout: 1000 }, async () => {
  const attempted = [];
  const names = ['process-events', 'child-output', 'cleanup', 'result', 'manifest'];
  const results = await attemptIndependentSteps(names.map((name) => ({ name, run: async () => {
    attempted.push(name); if (name === 'process-events' || name === 'manifest') throw new Error('injected terminal failure');
  } })), { deadline: 2000, clock: () => 1000 });
  assert.deepEqual(attempted, names);
  assert.equal(results.find((item) => item.name === 'cleanup').ok, true);
  assert.equal(results.find((item) => item.name === 'result').ok, true);
  assert.equal(results.every((item) => item.ok), false);
});

test('bounded steps honour cancellation represented by the closed deadline', { timeout: 1000 }, async () => {
  const results = await attemptIndependentSteps([{ name: 'cancelled', run: async () => { assert.fail('must not execute'); } }],
    { deadline: 1000, clock: () => 1000 });
  assert.deepEqual(results, [{ name: 'cancelled', attempted: false, ok: false, reason: 'step_deadline' }]);
});

test('normalization retains every full tuple before and including the exact anchor', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]);
  const value = { ...child(11, 'x'.repeat(8000)), parentPid: 12, parentChain: [
    { pid: 12, parentPid: 10, created, commandLine: 'y'.repeat(9000) },
    { pid: 10, parentPid: 1, created, commandLine: 'node runner' },
    { pid: 1, parentPid: 90, created, commandLine: 'external diagnostic'.repeat(1000) },
  ] };
  const accepted = acceptOwnedIdentity(value, identities, { ...options, validateLedger: validateOwnedIdentityLedger, eventName: 'helper' });
  assert.equal(accepted.commandLine, value.commandLine);
  assert.deepEqual(accepted.parentChain, value.parentChain.slice(0, 2));
  assert.equal(accepted.externalAncestorCount, 1);
  assert.equal(JSON.stringify(accepted).includes('external diagnostic'), false);
  assert.doesNotThrow(() => validateOwnedIdentityAnchor(accepted, owner));
  const acceptedOwner = acceptOwnedIdentity(owner, identities, { ...options, eventName: 'runner' });
  assert.deepEqual(acceptedOwner.parentChain, []);
  assert.doesNotThrow(() => validateOwnedIdentity(acceptedOwner, 10));
});

test('unsafe original external text is rejected before omission with a fixed safe reason', { timeout: 1000 }, () => {
  const marker = 'SENTINEL credential must never enter evidence';
  const identities = new Map([[10, owner]]);
  const value = { ...child(11), parentChain: [...child(11).parentChain.slice(0, 1),
    { pid: 1, parentPid: 0, created, commandLine: marker }] };
  assert.throws(() => acceptOwnedIdentity(value, identities, { ...options, eventName: 'helper',
    validateText: (text) => { if (text.includes(marker)) throw new Error(marker); } }), (error) => {
    assert.deepEqual(ownedIdentityFailure(error, 11), { stage: 'unsafeText', pid: 11, reason: 'unsafe_identity_text_preserved' });
    assert.equal(error.message.includes(marker), false); return true;
  });
  assert.equal(identities.has(11), false);
});

test('the 256 KiB ledger rejects a complete oversized addition without losing accepted identities', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]);
  const first = acceptOwnedIdentity(child(11, 'a'.repeat(131072)), identities,
    { ...options, validateLedger: validateOwnedIdentityLedger, eventName: 'helper' });
  assert.throws(() => acceptOwnedIdentity(child(12, 'b'.repeat(131072)), identities,
    { ...options, validateLedger: validateOwnedIdentityLedger, eventName: 'helper' }), (error) => {
    assert.deepEqual(ownedIdentityFailure(error, 12), { stage: 'ledger', pid: 12, reason: 'ledger_budget_exceeded' }); return true;
  });
  assert.equal(identities.get(11), first);
  assert.equal(first.commandLine.length, 131072);
  assert.equal(identities.has(12), false);
  assert.ok(Buffer.byteLength(JSON.stringify([...identities.values()], null, 2)) <= 256 * 1024);
});

test('the unchanged 128 identity cap refuses a new complete identity with its own reason', { timeout: 1000 }, () => {
  const identities = new Map(Array.from({ length: 128 }, (_value, index) => [index + 20, child(index + 20)]));
  assert.throws(() => acceptOwnedIdentity(child(200), identities, { ...options, eventName: 'helper' }), (error) => {
    assert.deepEqual(ownedIdentityFailure(error, 200), { stage: 'identity', pid: 200, reason: 'identity_count_exceeded' }); return true;
  });
  assert.equal(identities.size, 128);
});

test('secondary events bind the exact full command and creation to the authoritative ledger', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]); const events = [];
  const accepted = acceptOwnedIdentity(child(11, 'full command '.repeat(4000)), identities, { ...options, eventName: 'helper',
    recordEvent: (value) => recordOwnedIdentityEvent({ ...value, command: ['unretained extra command'] }, identities, events, options) });
  const event = events[0];
  assert.deepEqual(event.identityLedgerRef, { pid: accepted.pid, created: accepted.created });
  assert.equal(event.parentPid, accepted.parentPid);
  assert.equal(event.commandLineSha256, createHash('sha256').update(accepted.commandLine).digest('hex').toUpperCase());
  assert.equal(event.parentChainLedgerCreated, accepted.created);
  assert.equal(Object.hasOwn(event, 'commandLine'), false);
  assert.equal(Object.hasOwn(event, 'command'), false);
  assert.equal(Object.hasOwn(event, 'parentChain'), false);
  assert.equal(accepted.commandLine.length, 52000);
  assert.ok(Buffer.byteLength(JSON.stringify(event)) < 1024);
  assert.throws(() => recordOwnedIdentityEvent({ ...accepted, created: '2026-10-07T00:01:00.000Z' }, identities, events, options), (error) => {
    assert.equal(ownedIdentityFailure(error, 11).reason, 'event_identity_reference_mismatch'); return true;
  });
  assert.throws(() => recordOwnedIdentityEvent({ ...accepted, parentChain: [{ ...accepted.parentChain[0], commandLine: 'changed parent' }] }, identities, events, options), (error) => {
    assert.equal(ownedIdentityFailure(error, 11).reason, 'event_identity_reference_mismatch'); return true;
  });
  assert.equal(events.length, 1);
});

test('secondary event byte and count caps remain failures while the accepted full ledger survives', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]); const events = [{ padding: 'x'.repeat(192 * 1024) }];
  const failures = captureOwnedSnapshot([], identities, [], { ...options,
    discover: (_snapshot, values) => values.set(11, child(11)),
    recordEvent: (value) => recordOwnedIdentityEvent(value, identities, events, options) });
  assert.deepEqual(failures, [{ stage: 'event', pid: 11, reason: 'event_budget_exceeded' }]);
  assert.equal(identities.get(11).commandLine, 'owned child');
  assert.equal(events.length, 1);
  const countEvents = Array.from({ length: 256 }, () => ({}));
  assert.throws(() => recordOwnedIdentityEvent(identities.get(11), identities, countEvents, options), (error) => {
    assert.equal(ownedIdentityFailure(error, 11).reason, 'event_count_exceeded'); return true;
  });
  assert.equal(countEvents.length, 256);
});

test('a missing or changed anchor cannot bind ownership and changed pre-anchor tuples cannot replace it', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]);
  const value = { ...child(11), parentPid: 12, parentChain: [
    { pid: 12, parentPid: 10, created, commandLine: 'full intermediate parent' },
    { pid: 10, parentPid: 1, created, commandLine: 'node runner' },
  ] };
  const accepted = acceptOwnedIdentity(value, identities, { ...options, eventName: 'helper' });
  assert.throws(() => validateOwnedIdentityAnchor(accepted, { ...owner, commandLine: 'changed owner' }));
  assert.throws(() => validateOwnedIdentityAnchor({ ...value, parentChain: value.parentChain.slice(0, 1) }, owner));
  assert.throws(() => acceptOwnedIdentity({ ...value, parentChain: [{ ...value.parentChain[0], commandLine: 'changed parent' }, value.parentChain[1]] },
    identities, { ...options, eventName: 'helper' }), (error) => {
    assert.equal(ownedIdentityFailure(error, 11).reason, 'recorded_identity_changed_replacement_preserved'); return true;
  });
  const differentAnchor = { ...child(13), parentChain: [{ ...child(13).parentChain[0], created: '2026-10-06T00:00:00.000Z' }] };
  assert.throws(() => acceptOwnedIdentity(differentAnchor, identities, { ...options, eventName: 'helper' }), (error) => {
    assert.equal(ownedIdentityFailure(error, 13).reason, 'ownership_anchor_changed_preserved'); return true;
  });
  assert.equal(identities.get(11), accepted);
  assert.equal(identities.has(13), false);
});

test('unsafe secondary event text is not retained and still leaves complete cleanup authority', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]); const events = [];
  assert.throws(() => acceptOwnedIdentity(child(11), identities, { ...options, eventName: 'helper',
    recordEvent: (value) => recordOwnedIdentityEvent({ ...value, unsafe: 'event marker' }, identities, events,
      { ...options, validateText: (text) => { assert.equal(text.includes('event marker'), false); } }) }), (error) => {
    assert.deepEqual(ownedIdentityFailure(error, 11), { stage: 'event', pid: 11, reason: 'unsafe_event_text_preserved' }); return true;
  });
  assert.equal(events.length, 0);
  assert.equal(identities.get(11).commandLine, 'owned child');
});

test('own tuple rejections identify a fixed guard without admitting any candidate', { timeout: 1000 }, () => {
  const cases = [
    [null, 'identity_missing', 'identity'],
    [{ ...child(11), pid: 0 }, 'identity_pid_invalid', 'pid'],
    [{ ...child(11), parentPid: -1 }, 'identity_parent_pid_invalid', 'parentPid'],
    [{ ...child(11), created: null }, 'identity_created_missing', 'created'],
    [{ ...child(11), created: 'invalid synthetic creation' }, 'identity_created_invalid', 'created'],
    [{ ...child(11), commandLine: undefined }, 'identity_command_missing', 'commandLine'],
    [{ ...child(11), commandLine: {} }, 'identity_command_invalid', 'commandLine'],
    [{ ...child(11), commandLine: '' }, 'identity_command_empty', 'commandLine'],
    [{ ...child(11), commandLine: 'x'.repeat(131073) }, 'identity_command_length_exceeded', 'commandLine'],
    [{ ...child(11), parentChain: null }, 'parent_chain_missing', 'parentChain'],
    [{ ...child(11), parentChain: [] }, 'parent_chain_empty', 'parentChain'],
    [{ ...child(11), parentChain: Array.from({ length: 13 }, () => ({})) }, 'parent_chain_length_exceeded', 'parentChain'],
  ];
  const expires = Date.now() + 800;
  assert.ok(cases.length <= 12);
  for (let index = 0; index < cases.length && index < 12; index += 1) {
    assert.ok(Date.now() < expires);
    const [value, subreason, failedField] = cases[index]; const identities = new Map([[10, owner]]);
    assert.throws(() => acceptOwnedIdentity(value, identities, { ...options, eventName: 'helper' }), (error) => {
      const failure = ownedIdentityFailure(error, value?.pid);
      assert.equal(failure.subreason, subreason); assert.equal(failure.failedField, failedField);
      assert.equal(failure.chainIndex, null); return true;
    });
    assert.equal(identities.size, 1);
  }
});

test('parent tuple rejections retain only the guard and bounded chain coordinates', { timeout: 1000 }, () => {
  const cases = [
    [null, 'parent_identity_missing', 'parentChain.identity'],
    [{ pid: 90 }, 'parent_chain_discontinuous', 'parentChain.pid'],
    [{ pid: 10, unavailable: true }, 'parent_unavailable_not_terminal', 'parentChain.unavailable'],
    [{ ...child(11).parentChain[0], parentPid: -1 }, 'parent_parent_pid_invalid', 'parentChain.parentPid'],
    [{ ...child(11).parentChain[0], created: null }, 'parent_created_missing', 'parentChain.created'],
    [{ ...child(11).parentChain[0], created: 'invalid synthetic creation' }, 'parent_created_invalid', 'parentChain.created'],
    [{ ...child(11).parentChain[0], commandLine: null }, 'parent_command_missing', 'parentChain.commandLine'],
    [{ ...child(11).parentChain[0], commandLine: 123 }, 'parent_command_invalid', 'parentChain.commandLine'],
    [{ ...child(11).parentChain[0], commandLine: '' }, 'parent_command_empty', 'parentChain.commandLine'],
    [{ ...child(11).parentChain[0], commandLine: 'x'.repeat(131073) }, 'parent_command_length_exceeded', 'parentChain.commandLine'],
    [{ ...child(11).parentChain[0], created: '2026-10-07T00:01:00.000Z' }, 'parent_created_after_identity', 'parentChain.created'],
  ];
  const expires = Date.now() + 800;
  assert.ok(cases.length <= 12);
  for (let index = 0; index < cases.length && index < 12; index += 1) {
    assert.ok(Date.now() < expires);
    const [parent, subreason, failedField] = cases[index]; const identities = new Map([[10, owner]]);
    const value = { ...child(11), parentChain: [parent, child(11).parentChain[1]] };
    assert.throws(() => acceptOwnedIdentity(value, identities, { ...options, eventName: 'helper' }), (error) => {
      const failure = ownedIdentityFailure(error, 11);
      assert.equal(failure.subreason, subreason); assert.equal(failure.failedField, failedField);
      assert.equal(failure.chainIndex, 0); assert.equal(failure.structure.expectedParentPid, 10);
      assert.equal(failure.structure.observedParentPid, parent?.pid ?? null); return true;
    });
    assert.equal(identities.has(11), false);
  }
});

test('cycle and absent exact anchor remain rejections with separate fixed reasons', { timeout: 1000 }, () => {
  const cyclic = { ...child(11), parentPid: 12, parentChain: [
    { pid: 12, parentPid: 11, created, commandLine: 'intermediate' },
    { pid: 11, parentPid: 10, created, commandLine: 'owned child' },
  ] };
  assert.throws(() => validateOwnedIdentity(cyclic, 10), (error) => {
    const failure = ownedIdentityFailure(error, 11);
    assert.equal(failure.subreason, 'parent_chain_cycle'); assert.equal(failure.chainIndex, 1); return true;
  });
  assert.throws(() => validateOwnedIdentity({ ...child(11), parentPid: 90, parentChain: [{ pid: 90, unavailable: true }] }, 10), (error) => {
    const failure = ownedIdentityFailure(error, 11);
    assert.equal(failure.subreason, 'ownership_anchor_not_reached'); assert.equal(failure.completeness, 'missing_anchor'); return true;
  });
  assert.throws(() => validateOwnedIdentityAnchor(child(11), { ...owner, commandLine: 'changed owner' }), (error) => {
    assert.equal(ownedIdentityFailure(error, 11).subreason, 'ownership_anchor_tuple_mismatch'); return true;
  });
});

test('the original one second parent deadline is deterministic and never admits a candidate', { timeout: 1000 }, () => {
  let calls = 0; const identities = new Map([[10, owner]]);
  assert.throws(() => acceptOwnedIdentity(child(11), identities, { ...options, clock: () => calls++ === 0 ? 1000 : 2000, eventName: 'helper' }), (error) => {
    const failure = ownedIdentityFailure(error, 11);
    assert.equal(failure.subreason, 'parent_chain_deadline'); assert.equal(failure.chainIndex, 0);
    assert.equal(failure.completeness, 'deadline'); return true;
  });
  assert.equal(calls, 2); assert.equal(identities.has(11), false);
});

test('owner ledger revalidation keeps its distinct bounded deadline failure', { timeout: 1000 }, () => {
  const identities = new Map([[11, child(11)]]); let calls = 0;
  assert.throws(() => acceptOwnedIdentity(owner, identities, { ...options, clock: () => ++calls < 3 ? 1000 : 2000, eventName: 'runner' }), (error) => {
    const failure = ownedIdentityFailure(error, 10);
    assert.equal(failure.reason, 'ownership_anchor_changed_preserved');
    assert.equal(failure.subreason, 'ownership_scan_deadline'); return true;
  });
  assert.equal(calls, 3); assert.equal(identities.has(10), false);
});

test('malicious failure metadata is rebuilt from fixed labels and UInt32 numeric coordinates', { timeout: 1000 }, () => {
  const marker = 'SYNTHETIC secret raw error must be omitted';
  const failure = ownedIdentityFailure({ message: marker, identityEvidenceFailure: {
    stage: 'identity', reason: 'incomplete_or_unsafe_identity_preserved', subreason: 'identity_command_missing',
    failedField: 'commandLine', completeness: 'missing_command', chainIndex: 1, raw: marker,
    structure: { candidatePid: marker, candidateParentPid: 0x100000000, ownerPid: 10, parentChainCount: 4097,
      expectedParentPid: -1, observedParentPid: NaN, commandLine: marker },
  } }, 0x100000000);
  assert.deepEqual(failure, { stage: 'identity', pid: null, reason: 'incomplete_or_unsafe_identity_preserved',
    subreason: 'identity_command_missing', failedField: 'commandLine', completeness: 'missing_command', chainIndex: 1,
    structure: { candidatePid: null, candidateParentPid: null, ownerPid: 10, parentChainCount: null, expectedParentPid: null, observedParentPid: null } });
  assert.equal(JSON.stringify(failure).includes(marker), false);
  const spoofed = ownedIdentityFailure({ identityEvidenceFailure: { stage: 'identity', reason: 'incomplete_or_unsafe_identity_preserved',
    subreason: 'identity_command_missing', failedField: marker, completeness: 'missing_command', raw: marker } }, 11);
  assert.deepEqual(spoofed, { stage: 'identity', pid: 11, reason: 'incomplete_or_unsafe_identity_preserved' });
  assert.deepEqual(ownedIdentityFailure(new Error(marker), 11), { stage: 'identity', pid: 11, reason: 'incomplete_or_unsafe_identity_preserved' });
  assert.deepEqual(ownedIdentityFailure({ identityEvidenceFailure: { stage: 'event', reason: 'secondary_event_failed',
    subreason: 'identity_command_missing', failedField: 'commandLine', completeness: 'missing_command' } }, 11),
  { stage: 'event', pid: 11, reason: 'secondary_event_failed' });
});

test('numeric projection limits do not change the original safe-integer identity admission', { timeout: 1000 }, () => {
  const largePid = 0x100000000; const value = { ...child(largePid), parentPid: 10 };
  assert.doesNotThrow(() => validateOwnedIdentity(value, 10));
  assert.throws(() => validateOwnedIdentity({ ...value, commandLine: null }, 10), (error) => {
    const failure = ownedIdentityFailure(error, largePid);
    assert.equal(failure.pid, null); assert.equal(failure.structure.candidatePid, null); return true;
  });
});

test('changing or throwing fake-error getters cannot bypass the safe projection', { timeout: 1000 }, () => {
  const marker = 'SYNTHETIC getter secret must never persist';
  const reads = { stage: 0, reason: 0, subreason: 0, chainIndex: 0, parentChainCount: 0 };
  const detail = { failedField: 'commandLine', completeness: 'missing_command',
    get stage() { return ++reads.stage === 1 ? 'identity' : marker; },
    get reason() { return ++reads.reason === 1 ? 'incomplete_or_unsafe_identity_preserved' : marker; },
    get subreason() { return ++reads.subreason === 1 ? 'identity_command_missing' : marker; },
    get chainIndex() { return ++reads.chainIndex === 1 ? 0 : marker; },
    structure: { candidatePid: 11, candidateParentPid: 10, ownerPid: 10, expectedParentPid: 10, observedParentPid: 10,
      get parentChainCount() { return ++reads.parentChainCount === 1 ? 2 : marker; } },
  };
  const projected = ownedIdentityFailure({ identityEvidenceFailure: detail }, 11);
  assert.equal(JSON.stringify(projected).includes(marker), false);
  assert.deepEqual(reads, { stage: 1, reason: 1, subreason: 1, chainIndex: 1, parentChainCount: 1 });
  assert.equal(projected.chainIndex, 0); assert.equal(projected.structure.parentChainCount, 2);
  assert.deepEqual(ownedIdentityFailure({ get identityEvidenceFailure() { throw new Error(marker); } }, 11),
    { stage: 'identity', pid: 11, reason: 'incomplete_or_unsafe_identity_preserved' });
  assert.deepEqual(ownedIdentityFailure({ identityEvidenceFailure: { stage: 'identity', reason: 'incomplete_or_unsafe_identity_preserved',
    get subreason() { throw new Error(marker); } } }, 11),
  { stage: 'identity', pid: 11, reason: 'incomplete_or_unsafe_identity_preserved' });
});

test('credential variants cannot enter a persisted rejection or become cleanup authority', { timeout: 2500 }, async (context) => {
  const marker = 'WB45_SYNTHETIC_CREDENTIAL_/+never-persist!';
  const variants = [marker, Buffer.from(marker).toString('base64'), Buffer.from(marker).toString('base64url'),
    Buffer.from(marker).toString('hex'), Buffer.from(marker).toString('hex').toUpperCase(), encodeURIComponent(marker)];
  const directory = await mkdtemp(path.join(tmpdir(), 'sonnetdb-wb45-identity-test-'));
  let canonical; let file; let ownedDirectoryVerified = false; let directoryRemoved = false;
  try {
    canonical = await realpath(directory); const canonicalTemp = await realpath(tmpdir());
    file = path.join(canonical, 'safe-rejection.json');
    assert.equal(path.dirname(canonical), canonicalTemp); assert.ok(path.basename(canonical).startsWith('sonnetdb-wb45-identity-test-'));
    ownedDirectoryVerified = true;
    const expires = Date.now() + 1500;
    assert.equal(variants.length, 6);
    for (let index = 0; index < variants.length && index < 6; index += 1) {
      assert.ok(Date.now() < expires); const identities = new Map([[10, owner]]); const events = []; let rejection;
      assert.throws(() => acceptOwnedIdentity({ ...child(11), parentChain: [...child(11).parentChain.slice(0, 1),
        { pid: 1, parentPid: 0, created, commandLine: variants[index] }] }, identities, { ...options,
        eventName: 'helper', recordEvent: (value) => events.push(value), validateText: (text) => {
          if (variants.some((variant) => text.includes(variant))) throw new Error(marker);
        } }), (error) => { rejection = ownedIdentityFailure(error, 11); return true; });
      assert.equal(rejection.reason, 'unsafe_identity_text_preserved'); assert.equal(identities.has(11), false); assert.equal(events.length, 0);
      await writeFile(file, JSON.stringify({ rejection, identities: [...identities.values()], events }), 'utf8');
      const persisted = await readFile(file, 'utf8');
      assert.equal(variants.some((variant) => persisted.includes(variant)), false);
    }
  } finally {
    try {
      if (ownedDirectoryVerified) {
        await unlink(file).catch((error) => { if (error.code !== 'ENOENT') throw error; });
        await rmdir(canonical); directoryRemoved = true;
      }
    } finally {
      context.diagnostic(`WB45 synthetic credential temporary directory ${directoryRemoved ? 'removed' : 'retained; ownership/cleanup verification incomplete'}: ${canonical ?? directory}`);
    }
  }
});
