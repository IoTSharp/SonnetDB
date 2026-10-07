import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';

const rejectionReasons = new Map([
  ['identity', new Set(['incomplete_or_unsafe_identity_preserved', 'identity_count_exceeded',
    'recorded_identity_changed_replacement_preserved', 'ownership_anchor_changed_preserved'])],
  ['unsafeText', new Set(['unsafe_identity_text_preserved'])],
  ['ledger', new Set(['ledger_budget_exceeded'])],
  ['event', new Set(['secondary_event_failed', 'event_count_exceeded', 'event_budget_exceeded',
    'unsafe_event_text_preserved', 'event_identity_reference_mismatch'])],
]);

/** Create a rejection containing only a fixed safe stage and reason. */
export function identityEvidenceRejection(stage, reason) {
  assert.ok(rejectionReasons.get(stage)?.has(reason));
  const error = new Error('Owned identity evidence rejected.');
  error.name = 'OwnedIdentityEvidenceError';
  error.identityEvidenceFailure = { stage, reason };
  return error;
}

/** Project failures without retaining raw error messages or unsafe identity text. */
export function ownedIdentityFailure(error, pid) {
  const detail = error?.identityEvidenceFailure;
  const failure = rejectionReasons.get(detail?.stage)?.has(detail?.reason) ? detail
    : { stage: 'identity', reason: 'incomplete_or_unsafe_identity_preserved' };
  return { stage: failure.stage, pid: Number.isSafeInteger(pid) && pid > 0 ? pid : null, reason: failure.reason };
}

function sameTuple(left, right) {
  return left?.pid === right?.pid && left?.parentPid === right?.parentPid
    && left?.created === right?.created && left?.commandLine === right?.commandLine;
}

/** Validate complete process identity before accepting it into the cleanup ledger. */
export function validateOwnedIdentity(identity, ownerPid) {
  assert.ok(Number.isSafeInteger(ownerPid) && ownerPid > 0);
  assert.ok(identity && Number.isSafeInteger(identity.pid) && identity.pid > 0);
  assert.ok(Number.isSafeInteger(identity.parentPid) && identity.parentPid >= 0);
  assert.ok(typeof identity.created === 'string' && Number.isFinite(Date.parse(identity.created)));
  assert.ok(typeof identity.commandLine === 'string' && identity.commandLine.length > 0 && identity.commandLine.length <= 131072);
  assert.ok(Array.isArray(identity.parentChain) && identity.parentChain.length <= 12
    && (identity.pid === ownerPid || identity.parentChain.length > 0));
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

/** Bind the complete ownership anchor tuple to its authoritative ledger identity. */
export function validateOwnedIdentityAnchor(identity, ownerIdentity) {
  validateOwnedIdentity(identity, ownerIdentity?.pid);
  assert.ok(sameTuple(identity.pid === ownerIdentity.pid ? identity
    : identity.parentChain.find((parent) => parent.pid === ownerIdentity.pid), ownerIdentity));
}

/** Enforce the unchanged 256 KiB authoritative ledger budget before accepting a new identity. */
export function validateOwnedIdentityLedger(identity, ledger) {
  const next = new Map(ledger); next.set(identity.pid, identity);
  if (Buffer.byteLength(JSON.stringify([...next.values()], null, 2)) > 256 * 1024) throw identityEvidenceRejection('ledger', 'ledger_budget_exceeded');
}

/** Retain bounded secondary events with exact references to the full authoritative identity. */
export function recordOwnedIdentityEvent(value, identities, events, { validateText, clock = Date.now }) {
  if (events.length >= 256) throw identityEvidenceRejection('event', 'event_count_exceeded');
  try { validateText(JSON.stringify(value)); }
  catch { throw identityEvidenceRejection('event', 'unsafe_event_text_preserved'); }
  const identity = identities.get(value.pid);
  if (!identity || !sameTuple(value, identity) || !Array.isArray(value.parentChain)
    || value.parentChain.length !== identity.parentChain.length
    || value.parentChain.some((parent, index) => !sameTuple(parent, identity.parentChain[index]))) {
    throw identityEvidenceRejection('event', 'event_identity_reference_mismatch');
  }
  const { commandLine, parentChain, command, ...projected } = value;
  const next = { atUtc: new Date(clock()).toISOString(), ...projected,
    commandLineSha256: createHash('sha256').update(commandLine).digest('hex').toUpperCase(),
    identityLedgerRef: { pid: identity.pid, created: identity.created },
    parentChainSource: 'accepted-identities-ledger', parentChainLedgerPid: identity.pid, parentChainLedgerCreated: identity.created };
  if (Buffer.byteLength(JSON.stringify([...events, next], null, 2)) > 192 * 1024) throw identityEvidenceRejection('event', 'event_budget_exceeded');
  events.push(next);
  return next;
}

/** Keep every accepted identity in a full ledger even if its secondary event sink fails. */
export function acceptOwnedIdentity(identity, identities, { ownerPid, validateText, validateLedger, recordEvent, eventName, clock = Date.now }) {
  try { validateOwnedIdentity(identity, ownerPid); }
  catch { throw identityEvidenceRejection('identity', 'incomplete_or_unsafe_identity_preserved'); }
  if (!(identities.size < 128 || identities.has(identity.pid))) throw identityEvidenceRejection('identity', 'identity_count_exceeded');
  const authorityLength = identity.pid === ownerPid ? 0 : identity.parentChain.findIndex((parent) => parent.pid === ownerPid) + 1;
  const authorityChain = identity.parentChain.slice(0, authorityLength);
  const previous = identities.get(identity.pid);
  if (previous) {
    const previousLength = previous.pid === ownerPid ? 0 : previous.parentChain.findIndex((parent) => parent.pid === ownerPid) + 1;
    const previousChain = previous.parentChain.slice(0, previousLength);
    if (!sameTuple(previous, identity) || previousChain.length !== authorityChain.length
      || previousChain.some((parent, index) => !sameTuple(parent, authorityChain[index]))) {
      throw identityEvidenceRejection('identity', 'recorded_identity_changed_replacement_preserved');
    }
  }
  try {
    const ownerIdentity = identities.get(ownerPid);
    if (ownerIdentity) validateOwnedIdentityAnchor(identity, ownerIdentity);
    if (identity.pid === ownerPid) {
      const expires = clock() + 1_000; let count = 0;
      for (const acceptedIdentity of identities.values()) {
        assert.ok(++count <= 128 && clock() < expires);
        validateOwnedIdentityAnchor(acceptedIdentity, identity);
      }
    }
  } catch { throw identityEvidenceRejection('identity', 'ownership_anchor_changed_preserved'); }
  try { validateText(JSON.stringify(identity)); }
  catch { throw identityEvidenceRejection('unsafeText', 'unsafe_identity_text_preserved'); }
  // External ancestor bodies are diagnostic only. Keep every complete authority tuple through the exact Node anchor.
  const externalAncestors = identity.parentChain.slice(authorityLength);
  const accepted = { pid: identity.pid, parentPid: identity.parentPid, created: identity.created, commandLine: identity.commandLine,
    parentChain: authorityChain.map(({ pid, parentPid, created, commandLine }) => ({ pid, parentPid, created, commandLine })),
    ownershipAnchorPid: ownerPid, externalAncestorsDiagnosticOnly: true,
    externalAncestorCount: externalAncestors.length, externalAncestorsOmitted: externalAncestors.length > 0,
    externalAncestorsSha256: createHash('sha256').update(JSON.stringify(externalAncestors)).digest('hex').toUpperCase(),
    recordedAtUtc: previous?.recordedAtUtc ?? new Date(clock()).toISOString() };
  try { validateLedger?.(accepted, identities); }
  catch { throw identityEvidenceRejection('ledger', 'ledger_budget_exceeded'); }
  // The ledger is the primary identity log; recordEvent is an independently fallible secondary sink.
  identities.set(accepted.pid, accepted);
  try { recordEvent({ event: eventName, ...accepted }); }
  catch (error) {
    if (rejectionReasons.get('event').has(error?.identityEvidenceFailure?.reason)
      && error.identityEvidenceFailure.stage === 'event') throw error;
    throw identityEvidenceRejection('event', 'secondary_event_failed');
  }
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
    } catch (error) {
      failures.push(ownedIdentityFailure(error, candidate?.pid));
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
