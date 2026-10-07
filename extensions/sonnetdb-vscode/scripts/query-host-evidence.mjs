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

/** Project failures without retaining raw error messages or unsafe identity text. */
export function ownedIdentityFailure(error, pid) {
  const fallback = { stage: 'identity', pid: safePid(pid), reason: 'incomplete_or_unsafe_identity_preserved' };
  try {
    const detail = error?.identityEvidenceFailure; const stage = detail?.stage; const reason = detail?.reason;
    if (!rejectionReasons.get(stage)?.has(reason)) return fallback;
    return { stage, pid: safePid(pid), reason, ...(stage === 'identity' ? safeIdentityDiagnostic(detail) : {}) };
  } catch { return fallback; }
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
  return { lookup, duplicates };
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
  } catch (error) { return [ownedIdentityFailure(error, null)]; }
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
    } catch (error) { failures.push(ownedIdentityFailure(error, candidate?.pid)); }
  }
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
    } catch (error) { failures.push(ownedIdentityFailure(error, item.candidate?.pid)); }
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
