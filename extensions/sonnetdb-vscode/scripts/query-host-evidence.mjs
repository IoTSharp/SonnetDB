import assert from 'node:assert/strict';

/** Validate complete process identity before accepting it into the cleanup ledger. */
export function validateOwnedIdentity(identity, ownerPid) {
  assert.ok(identity && Number.isSafeInteger(identity.pid) && identity.pid > 0);
  assert.ok(Number.isSafeInteger(identity.parentPid) && identity.parentPid >= 0);
  assert.ok(typeof identity.created === 'string' && Number.isFinite(Date.parse(identity.created)));
  assert.ok(typeof identity.commandLine === 'string' && identity.commandLine.length > 0 && identity.commandLine.length <= 131072);
  assert.ok(Array.isArray(identity.parentChain) && identity.parentChain.length > 0 && identity.parentChain.length <= 12);
  let expectedPid = identity.parentPid;
  let ownerSeen = identity.pid === ownerPid;
  const seen = new Set([identity.pid]);
  const expires = Date.now() + 1_000;
  for (let index = 0; index < identity.parentChain.length && index < 12; index += 1) {
    // Ancestors beyond the complete ownership anchor are diagnostic metadata, not cleanup authority.
    if (ownerSeen) break;
    assert.ok(Date.now() < expires);
    const parent = identity.parentChain[index];
    assert.equal(parent.pid, expectedPid);
    assert.ok(!seen.has(parent.pid));
    seen.add(parent.pid);
    if (parent.unavailable) {
      assert.equal(index, identity.parentChain.length - 1);
      break;
    }
    assert.ok(Number.isSafeInteger(parent.parentPid) && parent.parentPid >= 0);
    assert.ok(typeof parent.created === 'string' && Number.isFinite(Date.parse(parent.created)));
    assert.ok(typeof parent.commandLine === 'string' && parent.commandLine.length > 0 && parent.commandLine.length <= 131072);
    assert.ok(Date.parse(parent.created) <= Date.parse(identity.created));
    if (parent.pid === ownerPid) ownerSeen = true;
    expectedPid = parent.parentPid;
  }
  assert.equal(ownerSeen, true);
}

/** Keep every accepted identity in a full ledger even if its secondary event sink fails. */
export function acceptOwnedIdentity(identity, identities, { ownerPid, validateText, validateLedger, recordEvent, eventName, clock = Date.now }) {
  validateOwnedIdentity(identity, ownerPid);
  assert.ok(identities.size < 128 || identities.has(identity.pid));
  const previous = identities.get(identity.pid);
  assert.ok(!previous || (previous.created === identity.created && previous.commandLine === identity.commandLine && previous.parentPid === identity.parentPid));
  validateText(JSON.stringify(identity));
  const accepted = { ...identity, ownershipAnchorPid: ownerPid, externalAncestorsDiagnosticOnly: true,
    recordedAtUtc: previous?.recordedAtUtc ?? new Date(clock()).toISOString() };
  validateLedger?.(accepted, identities);
  // The ledger is the primary identity log; recordEvent is an independently fallible secondary sink.
  identities.set(accepted.pid, accepted);
  recordEvent({ event: eventName, ...accepted });
  return accepted;
}

/** Retain safe partial discovery and report traversal, identity and event failures separately. */
export function captureOwnedSnapshot(snapshot, identities, roots, {
  discover, ownerPid, validateText, validateLedger, recordEvent, clock = Date.now,
}) {
  assert.ok(Array.isArray(snapshot) && snapshot.length <= 4096 && identities.size <= 128 && roots.length <= 2);
  const candidates = new Map(identities);
  const failures = [];
  try { discover(snapshot, candidates, roots, clock); }
  catch { failures.push({ stage: 'discovery', reason: 'partial_discovery_failed' }); }
  // Discovery's own deadline cannot consume the separate bounded ledger flush.
  const expires = clock() + 1_000;
  let count = 0;
  for (const candidate of candidates.values()) {
    assert.ok(++count <= 128 && clock() < expires);
    if (identities.has(candidate.pid)) continue;
    try {
      acceptOwnedIdentity(candidate, identities, { ownerPid, validateText, validateLedger, recordEvent, eventName: 'owned-descendant', clock });
    } catch {
      failures.push({ stage: identities.has(candidate.pid) ? 'event' : 'identity', pid: Number.isSafeInteger(candidate.pid) ? candidate.pid : null,
        reason: identities.has(candidate.pid) ? 'secondary_event_failed' : 'incomplete_or_unsafe_identity_preserved' });
    }
  }
  assert.ok(failures.length <= 128);
  return failures;
}

/** Attempt each bounded cleanup or terminal step even when an earlier step failed. */
export async function attemptIndependentSteps(steps, { clock = Date.now, deadline, maximumSteps = 8 }) {
  assert.ok(Array.isArray(steps) && steps.length <= maximumSteps && maximumSteps <= 16);
  const results = [];
  for (let index = 0; index < steps.length && index < maximumSteps; index += 1) {
    const step = steps[index];
    if (clock() >= deadline) { results.push({ name: step.name, attempted: false, ok: false, reason: 'step_deadline' }); continue; }
    try { await step.run(); results.push({ name: step.name, attempted: true, ok: true }); }
    catch { results.push({ name: step.name, attempted: true, ok: false, reason: 'step_failed' }); }
  }
  return results;
}
