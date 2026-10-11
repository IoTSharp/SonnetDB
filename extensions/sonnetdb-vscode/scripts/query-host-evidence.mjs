import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';

const rejectionReasons = new Map([
  ['identity', new Set(['incomplete_or_unsafe_identity_preserved', 'identity_count_exceeded',
    'recorded_identity_changed_replacement_preserved', 'ownership_anchor_changed_preserved'])],
  ['unsafeText', new Set(['unsafe_identity_text_preserved'])],
  ['ledger', new Set(['ledger_budget_exceeded'])],
  ['event', new Set(['secondary_event_failed', 'event_count_exceeded', 'event_budget_exceeded',
    'unsafe_event_text_preserved', 'event_identity_reference_mismatch'])],
]);

// Each validation check has a fixed field and completeness label. Candidate text is never part of this contract.
const identityChecks = new Map([
  ['owner_pid_invalid', ['ownerPid', 'invalid_pid']],
  ['identity_missing', ['identity', 'missing_identity']],
  ['identity_pid_invalid', ['pid', 'invalid_pid']],
  ['identity_parent_pid_invalid', ['parentPid', 'invalid_pid']],
  ['identity_created_missing', ['created', 'missing_creation']],
  ['identity_created_invalid', ['created', 'invalid_creation']],
  ['identity_command_missing', ['commandLine', 'missing_command']],
  ['identity_command_invalid', ['commandLine', 'invalid_command']],
  ['identity_command_empty', ['commandLine', 'missing_command']],
  ['identity_command_length_exceeded', ['commandLine', 'over_limit']],
  ['parent_chain_missing', ['parentChain', 'missing_chain']],
  ['parent_chain_length_exceeded', ['parentChain', 'over_limit']],
  ['parent_chain_empty', ['parentChain', 'missing_anchor']],
  ['parent_chain_deadline', ['parentChain', 'deadline']],
  ['parent_identity_missing', ['parentChain.identity', 'missing_identity']],
  ['parent_chain_discontinuous', ['parentChain.pid', 'discontinuous_chain']],
  ['parent_chain_cycle', ['parentChain.pid', 'cyclic_chain']],
  ['parent_unavailable_not_terminal', ['parentChain.unavailable', 'unavailable_parent']],
  ['parent_parent_pid_invalid', ['parentChain.parentPid', 'invalid_pid']],
  ['parent_created_missing', ['parentChain.created', 'missing_creation']],
  ['parent_created_invalid', ['parentChain.created', 'invalid_creation']],
  ['parent_command_missing', ['parentChain.commandLine', 'missing_command']],
  ['parent_command_invalid', ['parentChain.commandLine', 'invalid_command']],
  ['parent_command_empty', ['parentChain.commandLine', 'missing_command']],
  ['parent_command_length_exceeded', ['parentChain.commandLine', 'over_limit']],
  ['parent_created_after_identity', ['parentChain.created', 'invalid_chronology']],
  ['ownership_anchor_not_reached', ['ownershipAnchorPid', 'missing_anchor']],
  ['ownership_anchor_tuple_mismatch', ['ownershipAnchorIdentity', 'changed_anchor']],
  ['ownership_scan_count_exceeded', ['acceptedIdentities', 'over_limit']],
  ['ownership_scan_deadline', ['acceptedIdentities', 'deadline']],
  ['candidate_snapshot_count_exceeded', ['currentSnapshot', 'over_limit']],
  ['candidate_snapshot_deadline', ['currentSnapshot', 'deadline']],
  ['candidate_snapshot_duplicate', ['currentSnapshot.pid', 'duplicate_pid']],
  ['candidate_snapshot_missing', ['currentSnapshot.identity', 'missing_identity']],
  ['candidate_creation_changed', ['currentSnapshot.created', 'changed_creation']],
  ['candidate_parent_changed', ['currentSnapshot.parentPid', 'changed_parent']],
  ['candidate_command_changed', ['currentSnapshot.commandLine', 'changed_command']],
  ['candidate_parent_tuple_changed', ['parentChain.identity', 'changed_parent']],
  ['candidate_refresh_failed', ['currentSnapshot', 'refresh_failed']],
]);

function safePid(value, allowZero = false) {
  return Number.isSafeInteger(value) && value >= (allowZero ? 0 : 1) && value <= 0xffffffff ? value : null;
}

const candidateSnapshotSchema = 'sonnetdb.owned-candidate-snapshot.v1';
const candidateSnapshotSources = new Set(['initial', 'fresh']);
const candidateSnapshotSubjects = new Set(['candidate', 'parent', 'anchor']);
const candidateCommandStates = new Set(['missing', 'empty', 'present', 'invalid']);
const candidateTransitionSchema = 'sonnetdb.owned-candidate-transition.v1';
const candidateTupleRelations = new Set(['same', 'changed']);

function unknownCandidateSnapshot() {
  return { schema: candidateSnapshotSchema, source: null, subject: null, snapshotCount: null,
    candidateMatches: null, subjectMatches: null, candidateCommandState: null, subjectCommandState: null };
}

function safeCandidateSnapshot(value) {
  try {
    if (value?.schema !== candidateSnapshotSchema) return unknownCandidateSnapshot();
    const { source, subject, snapshotCount, candidateMatches, subjectMatches, candidateCommandState, subjectCommandState } = value;
    return { schema: candidateSnapshotSchema,
      source: candidateSnapshotSources.has(source) ? source : null,
      subject: candidateSnapshotSubjects.has(subject) ? subject : null,
      snapshotCount: Number.isSafeInteger(snapshotCount) && snapshotCount >= 0 && snapshotCount <= 4096 ? snapshotCount : null,
      candidateMatches: Number.isSafeInteger(candidateMatches) && candidateMatches >= 0 && candidateMatches <= 2 ? candidateMatches : null,
      subjectMatches: Number.isSafeInteger(subjectMatches) && subjectMatches >= 0 && subjectMatches <= 2 ? subjectMatches : null,
      candidateCommandState: candidateCommandStates.has(candidateCommandState) ? candidateCommandState : null,
      subjectCommandState: candidateCommandStates.has(subjectCommandState) ? subjectCommandState : null };
  } catch { return unknownCandidateSnapshot(); }
}

function unknownCandidateTransition() {
  return { schema: candidateTransitionSchema, availability: 'unknown', subject: null,
    initialSnapshotCount: null, freshSnapshotCount: null, initialSubjectMatches: null, freshSubjectMatches: null,
    initialSubjectCommandState: null, freshSubjectCommandState: null, tupleRelation: 'unknown' };
}

function safeCandidateTransition(value) {
  try {
    if (value?.schema !== candidateTransitionSchema) return unknownCandidateTransition();
    const { availability, subject, initialSnapshotCount, freshSnapshotCount, initialSubjectMatches, freshSubjectMatches,
      initialSubjectCommandState, freshSubjectCommandState, tupleRelation } = value;
    if (availability !== 'existing_fresh') return unknownCandidateTransition();
    const bounded = (count, maximum) => Number.isSafeInteger(count) && count >= 0 && count <= maximum ? count : null;
    const initialMatches = bounded(initialSubjectMatches, 2); const freshMatches = bounded(freshSubjectMatches, 2);
    const role = candidateSnapshotSubjects.has(subject) ? subject : null;
    return { schema: candidateTransitionSchema, availability: 'existing_fresh', subject: role,
      initialSnapshotCount: bounded(initialSnapshotCount, 4096), freshSnapshotCount: bounded(freshSnapshotCount, 4096),
      initialSubjectMatches: initialMatches, freshSubjectMatches: freshMatches,
      initialSubjectCommandState: initialMatches === 1 && candidateCommandStates.has(initialSubjectCommandState) ? initialSubjectCommandState : null,
      freshSubjectCommandState: freshMatches === 1 && candidateCommandStates.has(freshSubjectCommandState) ? freshSubjectCommandState : null,
      tupleRelation: role !== null && initialMatches === 1 && freshMatches === 1 && candidateTupleRelations.has(tupleRelation)
        ? tupleRelation : 'unknown' };
  } catch { return unknownCandidateTransition(); }
}

function safeIdentityDiagnostic(value) {
  try {
    const subreason = value?.subreason; const check = identityChecks.get(subreason);
    if (!check || value.failedField !== check[0] || value.completeness !== check[1]) return {};
    const structure = value.structure; const chainIndex = value.chainIndex; const parentChainCount = structure?.parentChainCount;
    return { subreason, failedField: check[0], completeness: check[1],
      chainIndex: Number.isSafeInteger(chainIndex) && chainIndex >= 0 && chainIndex < 12 ? chainIndex : null,
      structure: { candidatePid: safePid(structure?.candidatePid), candidateParentPid: safePid(structure?.candidateParentPid, true),
        ownerPid: safePid(structure?.ownerPid),
        parentChainCount: Number.isSafeInteger(parentChainCount) && parentChainCount >= 0 && parentChainCount <= 4096 ? parentChainCount : null,
        expectedParentPid: safePid(structure?.expectedParentPid, true), observedParentPid: safePid(structure?.observedParentPid) } };
  } catch { return {}; }
}

function rejectIdentityCheck(subreason, identity, ownerPid, chainIndex = null, expectedParentPid = null, observedParentPid = null) {
  const [failedField, completeness] = identityChecks.get(subreason);
  throw identityEvidenceRejection('identity', 'incomplete_or_unsafe_identity_preserved', {
    subreason, failedField, completeness, chainIndex,
    structure: { candidatePid: identity?.pid, candidateParentPid: identity?.parentPid, ownerPid,
      parentChainCount: Array.isArray(identity?.parentChain) ? identity.parentChain.length : null, expectedParentPid, observedParentPid },
  });
}

/** Create a rejection containing only fixed safe labels and bounded numeric structure. */
export function identityEvidenceRejection(stage, reason, diagnostic) {
  assert.ok(rejectionReasons.get(stage)?.has(reason));
  const error = new Error('Owned identity evidence rejected.');
  error.name = 'OwnedIdentityEvidenceError';
  error.identityEvidenceFailure = { stage, reason, ...(stage === 'identity' ? safeIdentityDiagnostic(diagnostic) : {}) };
  return error;
}

/** Project failures and optional snapshot observations without retaining raw identity or error text. */
export function ownedIdentityFailure(error, pid, candidateSnapshot, candidateTransition) {
  const fallback = { stage: 'identity', pid: safePid(pid), reason: 'incomplete_or_unsafe_identity_preserved' };
  let failure = fallback; let previousSnapshot; let previousTransition;
  try {
    const detail = error?.identityEvidenceFailure; const stage = detail?.stage; const reason = detail?.reason;
    if (rejectionReasons.get(stage)?.has(reason)) {
      failure = { stage, pid: safePid(pid), reason, ...(stage === 'identity' ? safeIdentityDiagnostic(detail) : {}) };
      try { previousSnapshot = detail.candidateSnapshot; }
      catch { previousSnapshot = null; }
      try { previousTransition = detail.candidateTransition; }
      catch { previousTransition = null; }
    }
  } catch { /* An observation cannot replace the original safe fallback. */ }
  const observation = candidateSnapshot === undefined ? previousSnapshot : candidateSnapshot;
  const transition = candidateTransition === undefined ? previousTransition : candidateTransition;
  if (observation !== undefined) failure = { ...failure, candidateSnapshot: safeCandidateSnapshot(observation) };
  return transition === undefined ? failure : { ...failure, candidateTransition: safeCandidateTransition(transition) };
}

function sameTuple(left, right) {
  return left?.pid === right?.pid && left?.parentPid === right?.parentPid
    && left?.created === right?.created && left?.commandLine === right?.commandLine;
}

/** Validate complete process identity before accepting it into the cleanup ledger. */
export function validateOwnedIdentity(identity, ownerPid, { clock = Date.now } = {}) {
  validateOwnedIdentityOwnTuple(identity, ownerPid);
  validateOwnedIdentityParentChain(identity, ownerPid, { clock });
}

function validateOwnedIdentityOwnTuple(identity, ownerPid) {
  const reject = (check, index, expected, observed) => rejectIdentityCheck(check, identity, ownerPid, index, expected, observed);
  if (!(Number.isSafeInteger(ownerPid) && ownerPid > 0)) reject('owner_pid_invalid');
  if (!identity) reject('identity_missing');
  if (!(Number.isSafeInteger(identity.pid) && identity.pid > 0)) reject('identity_pid_invalid');
  if (!(Number.isSafeInteger(identity.parentPid) && identity.parentPid >= 0)) reject('identity_parent_pid_invalid');
  if (identity.created === null || identity.created === undefined) reject('identity_created_missing');
  if (!(typeof identity.created === 'string' && Number.isFinite(Date.parse(identity.created)))) reject('identity_created_invalid');
  if (identity.commandLine === null || identity.commandLine === undefined) reject('identity_command_missing');
  if (typeof identity.commandLine !== 'string') reject('identity_command_invalid');
  if (identity.commandLine.length === 0) reject('identity_command_empty');
  if (identity.commandLine.length > 131072) reject('identity_command_length_exceeded');
}

function validateOwnedIdentityParentChain(identity, ownerPid, { clock = Date.now } = {}) {
  const reject = (check, index, expected, observed) => rejectIdentityCheck(check, identity, ownerPid, index, expected, observed);
  if (!Array.isArray(identity.parentChain)) reject('parent_chain_missing');
  if (identity.parentChain.length > 12) reject('parent_chain_length_exceeded');
  if (identity.pid !== ownerPid && identity.parentChain.length === 0) reject('parent_chain_empty');
  let expectedPid = identity.parentPid;
  let ownerSeen = identity.pid === ownerPid;
  const seen = new Set([identity.pid]);
  const expires = clock() + 1_000;
  for (let index = 0; index < identity.parentChain.length && index < 12; index += 1) {
    // Ancestors beyond the complete ownership anchor are diagnostic metadata, not cleanup authority.
    if (ownerSeen) break;
    if (!(clock() < expires)) reject('parent_chain_deadline', index, expectedPid);
    const parent = identity.parentChain[index];
    if (!parent) reject('parent_identity_missing', index, expectedPid);
    if (parent.pid !== expectedPid) reject('parent_chain_discontinuous', index, expectedPid, parent.pid);
    if (seen.has(parent.pid)) reject('parent_chain_cycle', index, expectedPid, parent.pid);
    seen.add(parent.pid);
    if (parent.unavailable) {
      if (index !== identity.parentChain.length - 1) reject('parent_unavailable_not_terminal', index, expectedPid, parent.pid);
      break;
    }
    if (!(Number.isSafeInteger(parent.parentPid) && parent.parentPid >= 0)) reject('parent_parent_pid_invalid', index, expectedPid, parent.pid);
    if (parent.created === null || parent.created === undefined) reject('parent_created_missing', index, expectedPid, parent.pid);
    if (!(typeof parent.created === 'string' && Number.isFinite(Date.parse(parent.created)))) reject('parent_created_invalid', index, expectedPid, parent.pid);
    if (parent.commandLine === null || parent.commandLine === undefined) reject('parent_command_missing', index, expectedPid, parent.pid);
    if (typeof parent.commandLine !== 'string') reject('parent_command_invalid', index, expectedPid, parent.pid);
    if (parent.commandLine.length === 0) reject('parent_command_empty', index, expectedPid, parent.pid);
    if (parent.commandLine.length > 131072) reject('parent_command_length_exceeded', index, expectedPid, parent.pid);
    if (!(Date.parse(parent.created) <= Date.parse(identity.created))) reject('parent_created_after_identity', index, expectedPid, parent.pid);
    if (parent.pid === ownerPid) ownerSeen = true;
    expectedPid = parent.parentPid;
  }
  if (!ownerSeen) reject('ownership_anchor_not_reached');
}

/** Bind the complete ownership anchor tuple to its authoritative ledger identity. */
export function validateOwnedIdentityAnchor(identity, ownerIdentity, options) {
  validateOwnedIdentity(identity, ownerIdentity?.pid, options);
  const index = identity.pid === ownerIdentity.pid ? null : identity.parentChain.findIndex((parent) => parent.pid === ownerIdentity.pid);
  if (!sameTuple(index === null ? identity : identity.parentChain[index], ownerIdentity)) {
    rejectIdentityCheck('ownership_anchor_tuple_mismatch', identity, ownerIdentity.pid, index, ownerIdentity.pid, ownerIdentity.pid);
  }
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
export function acceptOwnedIdentity(identity, identities, { ownerPid, validateText, validateLedger, recordEvent, eventName, clock = Date.now, diagnosticAncestors }) {
  try { validateOwnedIdentity(identity, ownerPid, { clock }); }
  catch (error) { throw identityEvidenceRejection('identity', 'incomplete_or_unsafe_identity_preserved', error?.identityEvidenceFailure); }
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
    if (ownerIdentity) validateOwnedIdentityAnchor(identity, ownerIdentity, { clock });
    if (identity.pid === ownerPid) {
      const expires = clock() + 1_000; let count = 0;
      for (const acceptedIdentity of identities.values()) {
        if (++count > 128) rejectIdentityCheck('ownership_scan_count_exceeded', acceptedIdentity, ownerPid);
        if (!(clock() < expires)) rejectIdentityCheck('ownership_scan_deadline', acceptedIdentity, ownerPid);
        validateOwnedIdentityAnchor(acceptedIdentity, identity, { clock });
      }
    }
  } catch (error) { throw identityEvidenceRejection('identity', 'ownership_anchor_changed_preserved', error?.identityEvidenceFailure); }
  try { validateText(JSON.stringify(identity)); }
  catch { throw identityEvidenceRejection('unsafeText', 'unsafe_identity_text_preserved'); }
  // External ancestor bodies are diagnostic only. Keep every complete authority tuple through the exact Node anchor.
  const externalAncestors = diagnosticAncestors ?? identity.parentChain.slice(authorityLength);
  try { validateText(JSON.stringify(externalAncestors)); }
  catch { throw identityEvidenceRejection('unsafeText', 'unsafe_identity_text_preserved'); }
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

function candidateText(value, validateText) {
  try { validateText(JSON.stringify(value)); }
  catch { throw identityEvidenceRejection('unsafeText', 'unsafe_identity_text_preserved'); }
}

function candidateLookup(snapshot, candidate, ownerPid, clock) {
  if (!Array.isArray(snapshot) || snapshot.length > 4096) rejectIdentityCheck('candidate_snapshot_count_exceeded', candidate, ownerPid);
  const lookup = new Map(); const duplicates = new Set(); const expires = clock() + 1_000;
  for (let index = 0; index < snapshot.length && index < 4096; index += 1) {
    if (!(clock() < expires)) rejectIdentityCheck('candidate_snapshot_deadline', candidate, ownerPid);
    const value = snapshot[index];
    if (lookup.has(value?.pid)) duplicates.add(value.pid);
    else lookup.set(value?.pid, value);
  }
  return { lookup, duplicates, snapshotCount: snapshot.length };
}

function commandPresence(value) {
  if (value === null || value === undefined) return 'missing';
  if (typeof value !== 'string') return 'invalid';
  return value === '' ? 'empty' : 'present';
}

function candidateFailureSubject(failure, ownerPid) {
  const check = failure.subreason; const expectedPid = failure.structure?.expectedParentPid;
  const parentCoordinate = Number.isSafeInteger(failure.chainIndex) && failure.chainIndex >= 0
    && failure.chainIndex < 12 && safePid(expectedPid) !== null;
  let subject = null; let subjectPid = null;
  if (check?.startsWith('identity_') || ['candidate_creation_changed', 'candidate_parent_changed', 'candidate_command_changed'].includes(check)) {
    subject = 'candidate'; subjectPid = failure.pid;
  } else if (check?.startsWith('parent_') || check === 'candidate_parent_tuple_changed') {
    if (parentCoordinate) { subject = expectedPid === ownerPid ? 'anchor' : 'parent'; subjectPid = expectedPid; }
  } else if (check === 'candidate_snapshot_missing' || check === 'candidate_snapshot_duplicate') {
    if (parentCoordinate) {
      subject = expectedPid === ownerPid ? 'anchor' : 'parent'; subjectPid = expectedPid;
    } else if (failure.pid !== null && expectedPid === failure.pid) {
      subject = 'candidate'; subjectPid = failure.pid;
    }
  } else if (check === 'ownership_anchor_not_reached' || check === 'ownership_anchor_tuple_mismatch') {
    subject = 'anchor'; subjectPid = ownerPid;
  }
  return { subject, subjectPid };
}

function candidateSnapshotFailure(error, pid, source, ownerPid, snapshotSource) {
  const failure = ownedIdentityFailure(error, pid);
  let observation = unknownCandidateSnapshot();
  try {
    const { subject, subjectPid } = candidateFailureSubject(failure, ownerPid);
    const matches = (targetPid) => !source || targetPid === null || targetPid === undefined ? null
      : source.duplicates.has(targetPid) ? 2 : source.lookup.has(targetPid) ? 1 : 0;
    const presence = (targetPid, matchCount) => matchCount === 1 ? commandPresence(source.lookup.get(targetPid).commandLine) : null;
    const candidateMatches = matches(failure.pid); const subjectMatches = matches(subjectPid);
    const candidateCommandState = presence(failure.pid, candidateMatches);
    const subjectCommandState = subjectPid === failure.pid ? candidateCommandState : presence(subjectPid, subjectMatches);
    observation = { schema: candidateSnapshotSchema, source: snapshotSource, subject,
      snapshotCount: source?.snapshotCount ?? null, candidateMatches, subjectMatches, candidateCommandState, subjectCommandState };
  } catch { /* Diagnostics remain unknown if observing a tuple fails; the rejection is unchanged. */ }
  return ownedIdentityFailure({ identityEvidenceFailure: failure }, failure.pid, observation);
}

function candidateTransitionTuple(value, subjectPid) {
  const { pid, parentPid, created, commandLine } = value;
  const commandState = commandPresence(commandLine);
  const comparable = safePid(pid) === subjectPid && safePid(parentPid, true) !== null
    && typeof created === 'string' && created.length <= 64 && Number.isFinite(Date.parse(created))
    && (commandState === 'missing' || typeof commandLine === 'string' && commandLine.length <= 8192);
  return { commandState, comparable, pid, parentPid, created, commandLine: commandState === 'missing' ? null : commandLine };
}

function observeCandidateTransition(source, fresh, subject, subjectPid) {
  try {
    const matches = (lookup) => lookup.duplicates.has(subjectPid) ? 2 : lookup.lookup.has(subjectPid) ? 1 : 0;
    const initialSubjectMatches = matches(source); const freshSubjectMatches = matches(fresh);
    const initial = initialSubjectMatches === 1 ? candidateTransitionTuple(source.lookup.get(subjectPid), subjectPid) : null;
    const current = freshSubjectMatches === 1 ? candidateTransitionTuple(fresh.lookup.get(subjectPid), subjectPid) : null;
    return { schema: candidateTransitionSchema, availability: 'existing_fresh', subject,
      initialSnapshotCount: source.snapshotCount, freshSnapshotCount: fresh.snapshotCount,
      initialSubjectMatches, freshSubjectMatches, initialSubjectCommandState: initial?.commandState ?? null,
      freshSubjectCommandState: current?.commandState ?? null,
      tupleRelation: initial?.comparable && current?.comparable ? sameTuple(initial, current) ? 'same' : 'changed' : 'unknown' };
  } catch { return unknownCandidateTransition(); }
}

function appendCandidateTransitions(failures, initialFailureCount, source, fresh, ownerPid, expires, clock) {
  if (!fresh) return;
  // Admission and all primary failures are complete. Observation shares the existing flush window and grants no authority.
  for (let index = 0; index < initialFailureCount && index < 128; index += 1) {
    try {
      if (!(clock() < expires)) break;
      const failure = failures[index];
      if (failure.candidateSnapshot?.source !== 'initial') continue;
      const { subject, subjectPid } = candidateFailureSubject(failure, ownerPid);
      if (!candidateSnapshotSubjects.has(subject) || safePid(subjectPid) === null) continue;
      const transition = observeCandidateTransition(source, fresh, subject, subjectPid);
      if (!(clock() < expires)) break;
      failures[index] = { ...failure, candidateTransition: safeCandidateTransition(transition) };
    } catch { break; /* A late observation cannot replace, remove or add any primary failure. */ }
  }
}

function candidateAt(source, pid, candidate, ownerPid, index = null) {
  if (source.duplicates.has(pid)) rejectIdentityCheck('candidate_snapshot_duplicate', candidate, ownerPid, index, pid, pid);
  const value = source.lookup.get(pid);
  if (!value) rejectIdentityCheck('candidate_snapshot_missing', candidate, ownerPid, index, pid);
  return value;
}

// Capture only current snapshot tuples; accepted ledger commands never fill an incomplete candidate.
function currentCandidateChain(candidate, source, ownerIdentity, { validateText, clock }) {
  candidateText(candidate, validateText);
  const current = candidateAt(source, candidate.pid, candidate, ownerIdentity.pid);
  candidateText(current, validateText);
  if (current.created !== candidate.created) rejectIdentityCheck('candidate_creation_changed', candidate, ownerIdentity.pid);
  if (current.parentPid !== candidate.parentPid) rejectIdentityCheck('candidate_parent_changed', candidate, ownerIdentity.pid);
  if (current.commandLine !== candidate.commandLine) rejectIdentityCheck('candidate_command_changed', candidate, ownerIdentity.pid);
  const chain = []; const seen = new Set([candidate.pid]); let nextPid = candidate.parentPid;
  let anchorIndex = candidate.pid === ownerIdentity.pid ? -1 : null;
  const expires = clock() + 1_000;
  // Discovery retains at most sixteen tuples. Guard current external text before omitting it too.
  for (let index = 0; index < 16 && nextPid > 0; index += 1) {
    if (!(clock() < expires)) rejectIdentityCheck('parent_chain_deadline', candidate, ownerIdentity.pid, index, nextPid);
    if (seen.has(nextPid)) {
      if (anchorIndex === null) rejectIdentityCheck('parent_chain_cycle', candidate, ownerIdentity.pid, index, nextPid, nextPid);
      break;
    }
    seen.add(nextPid);
    const parent = source.lookup.get(nextPid);
    if (source.duplicates.has(nextPid)) rejectIdentityCheck('candidate_snapshot_duplicate', candidate, ownerIdentity.pid, index, nextPid, nextPid);
    if (!parent) {
      if (anchorIndex === null) rejectIdentityCheck('candidate_snapshot_missing', candidate, ownerIdentity.pid, index, nextPid);
      break;
    }
    candidateText(parent, validateText);
    chain.push({ pid: parent.pid, parentPid: parent.parentPid, created: parent.created, commandLine: parent.commandLine });
    if (parent.pid === ownerIdentity.pid && anchorIndex === null) anchorIndex = index;
    if (anchorIndex === null && index === 12) {
      rejectIdentityCheck('parent_chain_length_exceeded', { ...candidate, parentChain: chain }, ownerIdentity.pid);
    }
    nextPid = parent.parentPid;
  }
  if (anchorIndex === null) rejectIdentityCheck('ownership_anchor_not_reached', candidate, ownerIdentity.pid);
  const authority = chain.slice(0, anchorIndex + 1);
  const normalized = { pid: candidate.pid, parentPid: candidate.parentPid, created: candidate.created,
    commandLine: candidate.commandLine, parentChain: authority };
  // This validates authority without inventing an own command for a pending recheck.
  validateOwnedIdentityParentChain(normalized, ownerIdentity.pid, { clock });
  const anchor = anchorIndex === -1 ? normalized : authority[anchorIndex];
  if (!sameTuple(anchor, ownerIdentity)) rejectIdentityCheck('ownership_anchor_tuple_mismatch', normalized, ownerIdentity.pid, anchorIndex);
  if (!Array.isArray(candidate.parentChain)) rejectIdentityCheck('parent_chain_missing', candidate, ownerIdentity.pid);
  for (let index = 0; index < authority.length && index < 12; index += 1) {
    if (!(clock() < expires)) rejectIdentityCheck('parent_chain_deadline', normalized, ownerIdentity.pid, index);
    if (!sameTuple(candidate.parentChain[index], authority[index])) {
      rejectIdentityCheck('candidate_parent_tuple_changed', normalized, ownerIdentity.pid, index, authority[index].pid, candidate.parentChain[index]?.pid);
    }
  }
  return { identity: normalized, diagnosticAncestors: chain.slice(authority.length), currentChain: chain };
}

function missingCandidateCommand(candidate) {
  return Number.isSafeInteger(candidate?.pid) && candidate.pid > 0
    && Number.isSafeInteger(candidate.parentPid) && candidate.parentPid >= 0
    && typeof candidate.created === 'string' && Number.isFinite(Date.parse(candidate.created))
    && (candidate.commandLine === null || candidate.commandLine === undefined || candidate.commandLine === '');
}

/** Admit new candidates from current authority, with at most one exact missing-command recheck per batch. */
export async function captureOwnedCandidateSnapshot(snapshot, identities, roots, {
  discover, ownerPid, validateText, validateLedger, recordEvent, refreshSnapshot, clock = Date.now, refreshMilliseconds = 6_000,
}) {
  assert.ok(identities.size <= 128 && roots.length <= 2 && Number.isSafeInteger(refreshMilliseconds)
    && refreshMilliseconds > 0 && refreshMilliseconds <= 6_000);
  const failures = []; const candidates = new Map(identities); let source;
  const discoveryExpires = clock() + 3_000;
  const discoveryClock = () => {
    const now = clock();
    if (!(now < discoveryExpires)) rejectIdentityCheck('candidate_snapshot_deadline', null, ownerPid);
    return now;
  };
  const ownerIdentity = identities.get(ownerPid);
  try {
    if (!ownerIdentity) rejectIdentityCheck('ownership_anchor_not_reached', null, ownerPid);
    source = candidateLookup(snapshot, null, ownerPid, discoveryClock);
  } catch (error) { return [candidateSnapshotFailure(error, null, source, ownerPid, 'initial')]; }
  try { discover(snapshot, candidates, roots, discoveryClock); }
  catch { failures.push({ stage: 'discovery', reason: 'partial_discovery_failed' }); }
  const prepared = []; const prepareExpires = clock() + 1_000; let count = 0;
  const prepareClock = () => {
    const now = clock();
    if (!(now < prepareExpires)) rejectIdentityCheck('candidate_snapshot_deadline', null, ownerPid);
    return now;
  };
  for (const candidate of candidates.values()) {
    if (++count > 128) {
      failures.push(ownedIdentityFailure(identityEvidenceRejection('identity', 'identity_count_exceeded'), candidate?.pid)); break;
    }
    if (!(clock() < prepareExpires)) {
      failures.push(ownedIdentityFailure(identityEvidenceRejection('identity', 'incomplete_or_unsafe_identity_preserved', {
        subreason: 'candidate_snapshot_deadline', failedField: 'currentSnapshot', completeness: 'deadline',
      }), candidate?.pid)); break;
    }
    if (identities.has(candidate?.pid)) continue;
    try {
      candidateText(candidate, validateText);
      const retry = missingCandidateCommand(candidate);
      if (!retry) validateOwnedIdentityOwnTuple(candidate, ownerPid);
      const value = currentCandidateChain(candidate, source, ownerIdentity, { validateText, clock: prepareClock });
      if (!retry) validateOwnedIdentity(value.identity, ownerPid, { clock: prepareClock });
      prepared.push({ candidate, value, retry });
    } catch (error) { failures.push(candidateSnapshotFailure(error, candidate?.pid, source, ownerPid, 'initial')); }
  }
  const initialFailureCount = failures.length;
  let fresh; let refreshError; let expires = prepareExpires;
  if (prepared.some((item) => item.retry)) {
    const refreshController = new AbortController(); let timer;
    const remainingFlushMilliseconds = Math.max(0, prepareExpires - clock());
    const refreshExpires = clock() + refreshMilliseconds;
    const refreshClock = () => {
      const now = clock();
      if (!(now < refreshExpires)) rejectIdentityCheck('candidate_snapshot_deadline', null, ownerPid);
      return now;
    };
    try {
      if (!(remainingFlushMilliseconds > 0)) rejectIdentityCheck('candidate_snapshot_deadline', null, ownerPid);
      if (typeof refreshSnapshot !== 'function') rejectIdentityCheck('candidate_refresh_failed', null, ownerPid);
      // The runner callback retains its six-second helper and snapshot count/deadline caps.
      const refreshed = await Promise.race([Promise.resolve().then(() => refreshSnapshot({ signal: refreshController.signal })),
        new Promise((_resolve, reject) => {
          timer = setTimeout(() => { refreshController.abort(); reject(identityEvidenceRejection('identity', 'incomplete_or_unsafe_identity_preserved', {
            subreason: 'candidate_refresh_failed', failedField: 'currentSnapshot', completeness: 'refresh_failed',
          })); }, refreshMilliseconds);
        })]);
      fresh = candidateLookup(refreshed, null, ownerPid, refreshClock);
    } catch (error) {
      const projected = ownedIdentityFailure(error, null);
      refreshError = projected.subreason ? identityEvidenceRejection(projected.stage, projected.reason, projected)
        : identityEvidenceRejection('identity', 'incomplete_or_unsafe_identity_preserved', {
          subreason: 'candidate_refresh_failed', failedField: 'currentSnapshot', completeness: 'refresh_failed',
        });
    } finally {
      clearTimeout(timer); refreshController.abort();
      expires = clock() + remainingFlushMilliseconds;
    }
  }
  // Discovery and waiting do not consume the one-second flush; preparation and admission share it.
  const flushClock = () => {
    const now = clock();
    if (!(now < expires)) rejectIdentityCheck('candidate_snapshot_deadline', null, ownerPid);
    return now;
  };
  for (let index = 0; index < prepared.length && index < 128; index += 1) {
    const item = prepared[index];
    if (!(clock() < expires)) {
      failures.push(ownedIdentityFailure(identityEvidenceRejection('identity', 'incomplete_or_unsafe_identity_preserved', {
        subreason: 'candidate_snapshot_deadline', failedField: 'currentSnapshot', completeness: 'deadline',
      }), item.candidate?.pid)); break;
    }
    try {
      let value = item.value;
      if (item.retry) {
        if (refreshError) throw refreshError;
        const current = candidateAt(fresh, item.candidate.pid, item.candidate, ownerPid);
        candidateText(current, validateText);
        if (current.created !== item.candidate.created) rejectIdentityCheck('candidate_creation_changed', item.candidate, ownerPid);
        if (current.parentPid !== item.candidate.parentPid) rejectIdentityCheck('candidate_parent_changed', item.candidate, ownerPid);
        // Keep the original candidate even when rediscovery would omit a vanished or reused PID.
        const freshCandidate = { ...current, parentChain: item.value.currentChain };
        value = currentCandidateChain(freshCandidate, fresh, ownerIdentity, { validateText, clock: flushClock });
        if (value.identity.parentChain.length !== item.value.identity.parentChain.length
          || value.identity.parentChain.some((parent, parentIndex) => !sameTuple(parent, item.value.identity.parentChain[parentIndex]))) {
          rejectIdentityCheck('candidate_parent_tuple_changed', item.candidate, ownerPid);
        }
      }
      acceptOwnedIdentity(value.identity, identities, { ownerPid, validateText, validateLedger,
        eventName: 'owned-descendant', clock: flushClock, diagnosticAncestors: value.diagnosticAncestors,
        recordEvent: item.retry ? (event) => recordEvent({ ...event, candidateCommandRechecked: true,
          initialCommandState: item.candidate.commandLine === '' ? 'empty' : 'missing' }) : recordEvent });
    } catch (error) { failures.push(candidateSnapshotFailure(error, item.candidate?.pid, item.retry ? fresh : source,
      ownerPid, item.retry ? 'fresh' : 'initial')); }
  }
  assert.ok(failures.length <= 128);
  appendCandidateTransitions(failures, initialFailureCount, source, fresh, ownerPid, expires, clock);
  return failures;
}

const stopDiagnosticSchema = 'sonnetdb.owned-stop-diagnostic.v1';
const stopMarker = 'SONNETDB_STOP_DIAGNOSTIC_V1 ';
const stopJsCheckpoints = new Set(['anchor_validation', 'helper_dispatch', 'helper_identity', 'helper_wait',
  'helper_terminal', 'helper_result', 'result_contract']);
const stopPsCheckpoints = new Set(['identity_lookup', 'identity_validation', 'anchor_validation',
  'parent_chain_validation', 'stop_before', 'stop_after', 'already_exited']);
const stopContextInvalidations = new WeakMap();

// Observation never invokes an accessor, even when the original authority path will do so later.
function stopData(value, key) {
  const descriptor = value && Object.getOwnPropertyDescriptor(value, key);
  return descriptor && Object.hasOwn(descriptor, 'value') ? descriptor.value : undefined;
}

function stopLedgerReference(value, identities) {
  const pid = stopData(value, 'pid'); const created = stopData(value, 'created');
  if (safePid(pid) === null || typeof created !== 'string' || created.length > 40
    || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3,7}(?:Z|\+00:00)$/u.test(created)) return null;
  const accepted = Map.prototype.get.call(identities, pid);
  return stopData(accepted, 'pid') === pid && stopData(accepted, 'created') === created ? { pid, created } : null;
}

function unknownStopDiagnostic(attempt = null) {
  return { schema: stopDiagnosticSchema, attempt, observation: 'unknown', candidate: null, helper: null,
    jsCheckpoint: 'unknown', psCheckpoint: 'unknown', helperExit: 'unknown', helperSignal: 'unknown', result: 'unknown' };
}

/** Invoke only a data-property diagnostic callback; its exception has no authority effect. */
export function observeOwnedStopDiagnostic(observation, method, ...args) {
  try {
    const run = stopData(observation, method);
    if (typeof run === 'function') return Reflect.apply(run, undefined, args);
  } catch { /* A diagnostic sink cannot replace an authority return or exception. */ }
  // Private invalidation does not read user properties or add an authority exception.
  const invalidate = stopContextInvalidations.get(observation);
  if (invalidate) invalidate();
}
const stopObserve = observeOwnedStopDiagnostic;

/** Keep at most the existing 384 stop attempts, with references exclusively to the accepted ledger. */
export function createOwnedStopDiagnostics(identities, { observe = () => {}, token = () => randomBytes(16).toString('hex') } = {}) {
  const records = []; let overflow = false;
  return {
    begin(identity) {
      if (records.length >= 384) { overflow = true; return null; }
      const attempt = records.length + 1; let candidate; let nonce; let invalid = false; let finished = false;
      let helper = null; let packet = null; let markers = 0; let bytes = 0; let lines = 0; let pending = '';
      const record = { schema: stopDiagnosticSchema, attempt, observation: 'complete', candidate: null, helper: null,
        jsCheckpoint: 'unknown', psCheckpoint: 'unknown', helperExit: 'not_observed', helperSignal: 'not_observed', result: 'not_observed' };
      records.push({ project: () => finished && !invalid ? record : unknownStopDiagnostic(attempt) });
      const notify = () => {
        if (invalid) Object.assign(record, unknownStopDiagnostic(attempt));
        try { observe({ ...record, candidate: record.candidate && { ...record.candidate }, helper: record.helper && { ...record.helper } }); }
        catch { invalid = true; Object.assign(record, unknownStopDiagnostic(attempt)); }
      };
      try {
        candidate = stopLedgerReference(identity, identities); nonce = token();
        if (!candidate || typeof nonce !== 'string' || !/^[a-f0-9]{32}$/u.test(nonce)) invalid = true;
        else record.candidate = candidate;
      } catch { invalid = true; }
      if (invalid) Object.assign(record, unknownStopDiagnostic(attempt));
      const readLine = (line) => {
        if (!line.startsWith(stopMarker)) return;
        if (++markers !== 1 || line.length > 1024) { invalid = true; return; }
        try {
          const value = JSON.parse(line.slice(stopMarker.length));
          const keys = Object.keys(value);
          if (keys.join(',') !== 'schema,attempt,token,candidatePid,candidateCreated,helperPid,helperCreated,checkpoint'
            || JSON.stringify(value) !== line.slice(stopMarker.length) || value.schema !== stopDiagnosticSchema
            || value.attempt !== attempt || value.token !== nonce || value.candidatePid !== candidate?.pid
            || value.candidateCreated !== candidate?.created || !stopPsCheckpoints.has(value.checkpoint)) { invalid = true; return; }
          packet = value;
        } catch { invalid = true; }
      };
      const observation = {
        input() { return invalid ? null : { attempt, token: nonce }; },
        checkpoint(value) {
          if (finished) return;
          if (stopJsCheckpoints.has(value)) record.jsCheckpoint = value; else invalid = true;
          notify();
        },
        bindHelper(value) {
          if (finished) return;
          try { helper = stopLedgerReference(value, identities); if (!helper) invalid = true; else record.helper = helper; }
          catch { invalid = true; }
          notify();
        },
        terminal(value) {
          if (finished) return;
          try {
            const code = stopData(value, 'exitCode'); const signal = stopData(value, 'signal');
            record.helperExit = Number.isSafeInteger(code) && code >= 0 && code <= 0xffffffff ? (code === 0 ? 'zero' : 'nonzero') : 'unknown';
            record.helperSignal = signal === null ? 'none' : typeof signal === 'string' && /^SIG[A-Z0-9]{1,12}$/u.test(signal) ? 'signalled' : 'unknown';
            if (record.helperExit === 'unknown' || record.helperSignal === 'unknown') invalid = true;
          } catch { invalid = true; }
          notify();
        },
        returned(value) {
          if (finished) return;
          try {
            const exited = stopData(value, 'exited'); const stopped = stopData(value, 'stopped');
            record.result = exited === true && stopped !== true ? 'already_exited'
              : stopped === true && exited !== true ? 'stopped' : 'unknown';
            if (record.result === 'unknown') invalid = true;
          } catch { invalid = true; }
          notify();
        },
        stderr(chunk) {
          if (finished || invalid) return;
          try {
            bytes += chunk.length;
            if (bytes > 4 * 1024 * 1024) { invalid = true; pending = ''; return; }
            const text = pending + chunk.toString('utf8'); let start = 0; const expires = Date.now() + 50;
            for (let index = 0; index < text.length && index < 4 * 1024 * 1024; index += 1) {
              if (Date.now() >= expires || lines > 4096) { invalid = true; break; }
              if (text[index] === '\n') { readLine(text.slice(start, index).replace(/\r$/u, '')); start = index + 1; lines += 1; }
              else if (index - start > 1024) { invalid = true; break; }
            }
            pending = invalid ? '' : text.slice(start);
          } catch { invalid = true; pending = ''; }
        },
        finish() {
          if (finished) return;
          finished = true;
          try {
            if (helper && (pending || markers !== 1 || !packet)) invalid = true;
            if (pending || markers !== 1 || !packet || !helper) record.psCheckpoint = 'unknown';
            else if (packet.helperPid !== helper.pid || packet.helperCreated !== helper.created) invalid = true;
            else record.psCheckpoint = packet.checkpoint;
          } catch { invalid = true; }
          if (invalid) Object.assign(record, unknownStopDiagnostic(attempt));
          notify();
          if (invalid) Object.assign(record, unknownStopDiagnostic(attempt));
          packet = null; pending = ''; nonce = null;
        },
      };
      stopContextInvalidations.set(observation, () => { invalid = true; Object.assign(record, unknownStopDiagnostic(attempt)); });
      return observation;
    },
    summary() {
      const attempts = []; const expires = Date.now() + 1000;
      for (let index = 0; index < records.length && index < 384; index += 1) {
        if (Date.now() >= expires) return { schema: 'sonnetdb.owned-stop-ledger.v1', observation: 'unknown', overflow, attempts: [] };
        const record = records[index].project();
        attempts.push({ ...record, candidate: record.candidate && { ...record.candidate }, helper: record.helper && { ...record.helper } });
      }
      return { schema: 'sonnetdb.owned-stop-ledger.v1', observation: overflow ? 'unknown' : 'complete', overflow,
        attempts };
    },
  };
}

/** Execute the same helper wait, terminal assertions and result parser used by the real runner. */
export async function readOwnedStopHelperResult({ waitTerminal, verifyTerminal, parseResult }, observation) {
  stopObserve(observation, 'checkpoint', 'helper_wait');
  const terminal = await waitTerminal();
  stopObserve(observation, 'checkpoint', 'helper_terminal'); stopObserve(observation, 'terminal', terminal);
  verifyTerminal(terminal);
  stopObserve(observation, 'checkpoint', 'helper_result');
  const result = parseResult(); stopObserve(observation, 'returned', result);
  return result;
}

/** Produce the existing stop checks with a fixed, observation-only checkpoint on a separate stream. */
export function ownedStopPowerShellScript() {
  return `$taskExpected = [Console]::In.ReadLine() | ConvertFrom-Json -DateKind String
$taskStopCheckpoint = 'identity_lookup'
try {
$taskCurrent = $taskHelperLookup[[int]$taskExpected.pid]
if ($null -eq $taskCurrent) { $taskStopCheckpoint = 'already_exited'; ConvertTo-Json -InputObject @{exited=$true} -Compress; exit 0 }
$taskStopCheckpoint = 'identity_validation'
if ($taskCurrent.CreationDate.ToUniversalTime().ToString('O') -ne $taskExpected.created -or
 $taskCurrent.CommandLine -cne $taskExpected.commandLine -or [int]$taskCurrent.ParentProcessId -ne [int]$taskExpected.parentPid) { throw 'Ownership changed; preserve process.' }
$taskStopCheckpoint = 'anchor_validation'
if ([int]$taskExpected.ownershipAnchorPid -ne [int]$taskSelf.ParentProcessId) { throw 'Ownership anchor changed; preserve process.' }
if ([int]$taskExpected.pid -eq [int]$taskExpected.ownershipAnchorPid) { throw 'Cannot stop ownership anchor.' }
$taskAnchorExpected = $taskExpected.ownershipAnchorIdentity
if ($null -eq $taskAnchorExpected -or [int]$taskAnchorExpected.pid -ne [int]$taskExpected.ownershipAnchorPid) { throw 'Ownership anchor ledger identity missing; preserve process.' }
$taskAnchorLive = $taskHelperLookup[[int]$taskAnchorExpected.pid]
if ($null -eq $taskAnchorLive -or $null -eq $taskAnchorLive.CreationDate -or [string]::IsNullOrEmpty($taskAnchorLive.CommandLine) -or
 $taskAnchorLive.CreationDate.ToUniversalTime().ToString('O') -ne $taskAnchorExpected.created -or $taskAnchorLive.CommandLine -cne $taskAnchorExpected.commandLine -or
 [int]$taskAnchorLive.ParentProcessId -ne [int]$taskAnchorExpected.parentPid) { throw 'Live ownership anchor changed or missing; preserve process.' }
$taskStopCheckpoint = 'parent_chain_validation'
$taskParentDeadline = [DateTime]::UtcNow.AddSeconds(2)
if (@($taskExpected.parentChain).Count -gt 12) { throw 'Stop parent-chain count exceeded.' }
$taskNextParentPid = [int]$taskExpected.parentPid
$taskAnchorSeen = $false
foreach ($taskParentIdentity in @($taskExpected.parentChain)) {
 if ([DateTime]::UtcNow -ge $taskParentDeadline) { throw 'Stop parent-chain wall clock exceeded.' }
 if ($taskParentIdentity.unavailable -or [int]$taskParentIdentity.pid -ne $taskNextParentPid) { throw 'Continuous ownership parent chain missing; preserve process.' }
 $taskLiveParent = $taskHelperLookup[[int]$taskParentIdentity.pid]
 if ($null -eq $taskLiveParent -or $null -eq $taskLiveParent.CreationDate -or [string]::IsNullOrEmpty($taskLiveParent.CommandLine)) { throw 'Live ownership parent missing; preserve process.' }
 if ($taskLiveParent.CreationDate.ToUniversalTime().ToString('O') -ne $taskParentIdentity.created -or $taskLiveParent.CommandLine -cne $taskParentIdentity.commandLine -or [int]$taskLiveParent.ParentProcessId -ne [int]$taskParentIdentity.parentPid) { throw 'Parent identity changed; preserve process.' }
 if ([int]$taskParentIdentity.pid -eq [int]$taskExpected.ownershipAnchorPid) {
  if ($taskParentIdentity.created -ne $taskAnchorExpected.created -or $taskParentIdentity.commandLine -cne $taskAnchorExpected.commandLine -or [int]$taskParentIdentity.parentPid -ne [int]$taskAnchorExpected.parentPid) { throw 'Parent anchor differs from ledger; preserve process.' }
  $taskAnchorSeen = $true; break
 }
 $taskNextParentPid = [int]$taskParentIdentity.parentPid
}
if (-not $taskAnchorSeen) { throw 'Live ownership anchor not reached; preserve process.' }
if ([int]$taskExpected.pid -eq $PID) { throw 'Cannot stop verifier.' }
$taskStopCheckpoint = 'stop_before'
Stop-Process -Id ([int]$taskExpected.pid) -Force -ErrorAction Stop
$taskStopCheckpoint = 'stop_after'
ConvertTo-Json -InputObject @{stopped=$true} -Compress
} finally {
 try {
  if ($null -ne $taskExpected.stopDiagnostic) {
   [Console]::Error.WriteLine('${stopMarker}' + (ConvertTo-Json -InputObject ([ordered]@{
    schema='${stopDiagnosticSchema}'; attempt=$taskExpected.stopDiagnostic.attempt; token=$taskExpected.stopDiagnostic.token;
    candidatePid=[int]$taskExpected.pid; candidateCreated=$taskExpected.created;
    helperPid=[int]$taskSelf.ProcessId; helperCreated=$taskSelf.CreationDate.ToUniversalTime().ToString('O'); checkpoint=$taskStopCheckpoint
   }) -Compress))
  }
 } catch { }
}`;
}

/** Preserve the stop authority path while making its helper boundary injectable for bounded diagnostics tests. */
export function createOwnedStopBoundary({ ownerPid, identities, helper, recordEvent,
  diagnostics, validateAnchor = validateOwnedIdentityAnchor }) {
  return async function stopVerified(identity) {
    let observation;
    try { const begin = stopData(diagnostics, 'begin'); if (typeof begin === 'function') observation = begin(identity); }
    catch { /* Observation is not stop authority. */ }
    try {
      stopObserve(observation, 'checkpoint', 'anchor_validation');
      assert.notEqual(identity.pid, ownerPid);
      const ownerIdentity = identities.get(ownerPid);
      assert.ok(ownerIdentity);
      validateAnchor(identity, ownerIdentity);
      const ownershipAnchorIdentity = { pid: ownerIdentity.pid, parentPid: ownerIdentity.parentPid,
        created: ownerIdentity.created, commandLine: ownerIdentity.commandLine };
      stopObserve(observation, 'checkpoint', 'helper_dispatch');
      const diagnosticInput = stopObserve(observation, 'input');
      const result = await helper(ownedStopPowerShellScript(), { ...identity, ownershipAnchorPid: ownerPid,
        ownershipAnchorIdentity, stopDiagnostic: diagnosticInput }, undefined, observation);
      stopObserve(observation, 'checkpoint', 'result_contract');
      assert.ok(result.exited || result.stopped);
      recordEvent({ event: 'owned-process-stop', ...identity, alreadyExited: Boolean(result.exited) });
    } finally { stopObserve(observation, 'finish'); }
  };
}

const cleanupChecks = new Map([
  ['cleanup-budget', 'initial'], ['round-snapshot', 'round'], ['round-audit', 'round'],
  ['round-live', 'round'], ['live-count', 'round'], ['stop-round-budget', 'round'],
  ['stop-verification', 'round'], ['round-delay', 'round'], ['final-snapshot', 'final'],
  ['final-audit', 'final'], ['final-live', 'final'], ['remaining-processes', 'final'],
  ['root-identities', 'final'], ['audit-failures', 'final'],
]);

function cleanupCheck(value) {
  if (value === null) return null;
  const subcheck = value?.subcheck; const stage = value?.stage;
  return cleanupChecks.get(subcheck) === stage ? { subcheck, stage } : { subcheck: 'unknown', stage: 'unknown' };
}

function cleanupCount(value, maximum) {
  return Number.isSafeInteger(value) && value >= 0 && value <= maximum ? value : null;
}

const cleanupFinalCheckStates = new Set(['passed', 'refused', 'not-reached', 'unknown']);

function unknownCleanupFinalChecks() {
  return { remainingProcesses: 'unknown', rootIdentities: 'unknown', auditFailures: 'unknown' };
}

function cleanupFinalCheckState(value, key) {
  try {
    const state = value?.[key];
    return cleanupFinalCheckStates.has(state) ? state : 'unknown';
  } catch { return 'unknown'; }
}

function cleanupFinalChecks(value) {
  let checks;
  try { checks = value?.finalChecks; }
  catch { return unknownCleanupFinalChecks(); }
  return { remainingProcesses: cleanupFinalCheckState(checks, 'remainingProcesses'),
    rootIdentities: cleanupFinalCheckState(checks, 'rootIdentities'),
    auditFailures: cleanupFinalCheckState(checks, 'auditFailures') };
}

function unknownCleanupDiagnostic() {
  return { schema: 'sonnetdb.owned-process-cleanup.v1', firstRecoverableFailure: null,
    terminalFailure: { subcheck: 'unknown', stage: 'unknown' }, observation: 'unknown',
    finalChecks: unknownCleanupFinalChecks(),
    structure: { roundsAttempted: null, roundSnapshotFailures: null, roundAuditFailures: null,
      stopAttempts: null, stopFailures: null, acceptedIdentityCount: null, finalSnapshotCount: null,
      finalLiveCount: null, remainingCount: null, rootCount: null, rootIdentityCount: null,
      storedAuditFailureCount: null, blockingAuditFailureCount: null, auditFailureOverflow: null } };
}

/** Project only fixed cleanup labels and bounded counts, reading each untrusted getter once. */
export function ownedProcessCleanupDiagnostic(value) {
  try {
    const first = value?.firstRecoverableFailure; const terminal = value?.terminalFailure;
    const observation = value?.observation; const structure = value?.structure;
    if (observation !== 'complete') return unknownCleanupDiagnostic();
    return { schema: 'sonnetdb.owned-process-cleanup.v1', firstRecoverableFailure: cleanupCheck(first),
      terminalFailure: cleanupCheck(terminal), observation: 'complete',
      finalChecks: cleanupFinalChecks(value),
      structure: { roundsAttempted: cleanupCount(structure?.roundsAttempted, 3),
        roundSnapshotFailures: cleanupCount(structure?.roundSnapshotFailures, 3),
        roundAuditFailures: cleanupCount(structure?.roundAuditFailures, 3),
        stopAttempts: cleanupCount(structure?.stopAttempts, 384), stopFailures: cleanupCount(structure?.stopFailures, 384),
        acceptedIdentityCount: cleanupCount(structure?.acceptedIdentityCount, 128),
        finalSnapshotCount: cleanupCount(structure?.finalSnapshotCount, 4096),
        finalLiveCount: cleanupCount(structure?.finalLiveCount, 128), remainingCount: cleanupCount(structure?.remainingCount, 128),
        rootCount: cleanupCount(structure?.rootCount, 2), rootIdentityCount: cleanupCount(structure?.rootIdentityCount, 2),
        storedAuditFailureCount: cleanupCount(structure?.storedAuditFailureCount, 128),
        blockingAuditFailureCount: cleanupCount(structure?.blockingAuditFailureCount, 128),
        auditFailureOverflow: cleanupCount(structure?.auditFailureOverflow, 1) } };
  } catch { return unknownCleanupDiagnostic(); }
}

/** Run the existing owned-process checks independently of diagnostic observation and later cleanup steps. */
export async function verifyOwnedProcessCleanup({ deadline, ownerPid, identities, helperStarts, snapshot, audit,
  safeLiveIdentities, stopVerified, noteAuditFailure, verifyRoots, verifyAuditFailures, observe, delay, clock = Date.now }) {
  let active = { subcheck: 'cleanup-budget', stage: 'initial' }; let firstRecoverableFailure = null;
  let terminalFailure = null; let proven = false;
  const finalChecks = { remainingProcesses: 'not-reached', rootIdentities: 'not-reached', auditFailures: 'not-reached' };
  const structure = { roundsAttempted: 0, roundSnapshotFailures: 0, roundAuditFailures: 0,
    stopAttempts: 0, stopFailures: 0, finalSnapshotCount: null, finalLiveCount: null, remainingCount: null };
  const check = (subcheck) => { active = { subcheck, stage: cleanupChecks.get(subcheck) }; };
  const recoverable = () => { firstRecoverableFailure ??= { ...active }; };
  const finalCheck = (subcheck, key, run) => {
    check(subcheck);
    try { run(); finalChecks[key] = 'passed'; }
    catch { finalChecks[key] = 'refused'; terminalFailure ??= { ...active }; }
  };
  try {
    const cleanupDeadline = Math.min(deadline - 35_000, clock() + 45_000);
    for (let round = 0; round < 3 && clock() < cleanupDeadline; round += 1) {
      structure.roundsAttempted += 1; let current;
      check('round-snapshot');
      try { current = await snapshot(true); }
      catch { structure.roundSnapshotFailures += 1; recoverable(); noteAuditFailure({ stage: 'cleanup-snapshot', reason: 'snapshot_failed' }); }
      if (current) {
        check('round-audit');
        try { await audit(current, true); }
        catch { structure.roundAuditFailures += 1; recoverable(); /* The audit callback retains its original failure accounting. */ }
      }
      check('round-live');
      const live = (current ? safeLiveIdentities(current) : [...identities.values()]).filter((item) => item.pid !== ownerPid
        && !helperStarts.some((helperRecord) => helperRecord.pid === item.pid && helperRecord.closed))
        .sort((left, right) => (right.parentChain?.length ?? 0) - (left.parentChain?.length ?? 0));
      if (live.length === 0) break;
      check('live-count'); assert.ok(live.length <= 128);
      for (let index = 0; index < live.length && index < 128; index += 1) {
        check('stop-round-budget'); assert.ok(clock() < cleanupDeadline);
        check('stop-verification'); structure.stopAttempts += 1;
        try { await stopVerified(live[index]); }
        catch {
          structure.stopFailures += 1; recoverable();
          noteAuditFailure({ stage: 'stop', pid: live[index].pid, reason: 'ownership_or_verifier_failed_process_preserved' });
        }
      }
      check('round-delay'); await delay(100);
    }
    check('final-snapshot'); const final = await snapshot(true);
    // Count observations do not participate in authority, stop decisions or existing success assertions.
    try { structure.finalSnapshotCount = final.length; } catch { structure.finalSnapshotCount = null; }
    check('final-audit');
    try { await audit(final, true); }
    catch { recoverable(); /* A failed audit must not hide independently observed terminal processes. */ }
    check('final-live'); const remaining = safeLiveIdentities(final).filter((item) => item.pid !== ownerPid);
    structure.finalLiveCount = remaining.length;
    const verifiedRemaining = remaining.filter((item) => !helperStarts.some((helper) => helper.pid === item.pid && helper.closed));
    structure.remainingCount = verifiedRemaining.length;
    // A refused terminal check must not hide the other mandatory checks or replace the first failure.
    finalCheck('remaining-processes', 'remainingProcesses', () => assert.deepEqual(verifiedRemaining, []));
    finalCheck('root-identities', 'rootIdentities', verifyRoots);
    finalCheck('audit-failures', 'auditFailures', verifyAuditFailures);
    proven = terminalFailure === null && finalChecks.remainingProcesses === 'passed'
      && finalChecks.rootIdentities === 'passed' && finalChecks.auditFailures === 'passed';
  } catch { terminalFailure ??= { ...active }; }
  let diagnostic;
  try {
    const observed = observe();
    diagnostic = ownedProcessCleanupDiagnostic({ firstRecoverableFailure, terminalFailure, finalChecks, observation: 'complete',
      structure: { ...structure, acceptedIdentityCount: observed.acceptedIdentityCount, rootCount: observed.rootCount,
        rootIdentityCount: observed.rootIdentityCount, storedAuditFailureCount: observed.storedAuditFailureCount,
        blockingAuditFailureCount: observed.blockingAuditFailureCount, auditFailureOverflow: observed.auditFailureOverflow } });
  } catch { diagnostic = unknownCleanupDiagnostic(); }
  return { proven, diagnostic };
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
