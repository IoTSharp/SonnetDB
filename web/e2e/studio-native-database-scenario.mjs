// WB-61: the database journey uses ordinary rendered controls and the existing
// native harness. These contracts are evidence checks, never app state writes.
const connectionPath = '/studio-bridge/connections';
const fail = (message) => { throw new Error(`Studio database recovery: ${message}`); };

// Failed seed diagnostics contain only fixed categories. Body bytes are held
// briefly in memory and are never returned, logged or included in exceptions.
export async function observeDatabaseSeedFailure(response, { signal, deadline = Infinity } = {}) {
  const maximumBytes = 8192;
  const maximumReads = 16;
  const expires = Math.min(Date.now() + 2000, deadline);
  const result = { schema: 'sonnetdb.wb62.seed-failure.v1', httpStatus: null, httpCategory: 'unknown',
    bodyState: 'unknown', observedBytes: 0, reads: 0, format: null, frameCount: null,
    frameTypes: [], codes: [], unknownCodePresent: false, rawBodyOrSqlOrHeadersPersisted: false };
  let reader;
  let timer;
  let stop;
  try {
    const status = response.status;
    if (Number.isInteger(status) && status >= 100 && status <= 599) {
      result.httpStatus = status;
      result.httpCategory = status >= 500 ? 'server-error' : status >= 400 ? 'client-error' : 'other';
    }
    if (signal?.aborted) { result.bodyState = 'cancelled'; return result; }
    if (Date.now() >= expires) { result.bodyState = 'timeout'; return result; }
    if (!response.body || typeof response.body.getReader !== 'function') { result.bodyState = 'unavailable'; return result; }
    reader = response.body.getReader();
    const stopped = new Promise((resolve) => {
      stop = () => resolve({ stopped: 'cancelled' });
      signal?.addEventListener('abort', stop, { once: true });
      timer = setTimeout(() => resolve({ stopped: 'timeout' }), Math.max(1, expires - Date.now()));
    });
    const chunks = [];
    for (let index = 0; index < maximumReads && Date.now() < expires; index += 1) {
      if (signal?.aborted) { result.bodyState = 'cancelled'; return result; }
      result.reads += 1;
      const part = await Promise.race([reader.read(), stopped]);
      if (part.stopped === 'cancelled' || part.stopped === 'timeout') { result.bodyState = part.stopped; return result; }
      if (part.done) {
        result.bodyState = 'complete';
        break;
      }
      if (!(part.value instanceof Uint8Array)) { result.bodyState = 'invalid-chunk'; return result; }
      result.observedBytes = Math.min(maximumBytes + 1, result.observedBytes + part.value.byteLength);
      if (result.observedBytes > maximumBytes) { result.bodyState = 'too-large'; return result; }
      chunks.push(Buffer.from(part.value));
    }
    if (result.bodyState !== 'complete') {
      result.bodyState = Date.now() >= expires ? 'timeout' : 'read-limit';
      return result;
    }
    const text = new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks, result.observedBytes));
    let frames;
    try { frames = [JSON.parse(text)]; result.format = 'json'; }
    catch {
      const lines = text.split(/\r?\n/u).filter((line) => line.trim().length !== 0);
      if (lines.length > 8 || !lines.length) { result.bodyState = 'invalid-json'; return result; }
      frames = lines.map((line) => JSON.parse(line)); result.format = 'ndjson';
    }
    const allowedCodes = new Set(['sql_parse_error', 'sql_execution_error', 'sql_cancelled', 'sql_timeout', 'sql_locking_read_unsupported']);
    result.frameCount = frames.length;
    for (const frame of frames) {
      if (Date.now() >= expires || signal?.aborted) {
        result.bodyState = signal?.aborted ? 'cancelled' : 'timeout';
        result.frameCount = null; result.frameTypes = []; result.codes = []; result.unknownCodePresent = false;
        return result;
      }
      result.frameTypes.push(Array.isArray(frame) ? 'array' : frame && ['meta', 'end', 'error'].includes(frame.type) ? frame.type : 'unknown');
      if (frame && typeof frame === 'object' && Object.hasOwn(frame, 'code')) {
        if (typeof frame.code === 'string' && allowedCodes.has(frame.code)) {
          if (!result.codes.includes(frame.code)) result.codes.push(frame.code);
        } else result.unknownCodePresent = true;
      }
    }
    return result;
  } catch {
    result.bodyState = signal?.aborted ? 'cancelled' : result.bodyState === 'complete' ? 'invalid-json' : 'read-error';
    result.format = null; result.frameCount = null; result.frameTypes = []; result.codes = []; result.unknownCodePresent = false;
    return result;
  } finally {
    clearTimeout(timer);
    if (stop) signal?.removeEventListener('abort', stop);
    // A refused/incomplete stream is cancelled without waiting beyond its own
    // read deadline; the original fetch still owns the request abort signal.
    try { if (reader) void Promise.resolve(reader.cancel()).catch(() => {}); } catch { /* Ancillary cleanup never replaces the HTTP failure. */ }
  }
}

export async function rejectDatabaseSeedHttpFailure(primary, response, options, record) {
  try { record(await observeDatabaseSeedFailure(response, options)); } catch { /* Observation cannot replace the primary failure. */ }
  throw primary;
}

const ownObservationValue = (object, key) => {
  try {
    const descriptor = object && typeof object === 'object' ? Object.getOwnPropertyDescriptor(object, key) : null;
    return descriptor && Object.hasOwn(descriptor, 'value') ? descriptor.value : undefined;
  } catch { return undefined; }
};
const safeObservationNumber = (value, maximum = Number.MAX_SAFE_INTEGER) =>
  Number.isSafeInteger(value) && value >= 0 && value <= maximum ? value : null;

export function beginDatabaseSelectionAttempt(result, target, barrier) {
  const attempt = { schema: 'sonnetdb.wb63.selection-attempt.v1', target: target === 'A' || target === 'B' ? target : 'unknown',
    phase: 'before-click', barrier: {
      afterSequence: safeObservationNumber(ownObservationValue(barrier, 'afterSequence')),
      afterRequestSequence: safeObservationNumber(ownObservationValue(barrier, 'afterRequestSequence')),
    }, ackPoll: { callbackCalls: 0, elapsedMs: null }, failure: null };
  // This assignment happens synchronously, before the original click promise
  // is created. The original barrier is still passed to every acceptance check.
  try { if (Array.isArray(result.selectionAttempts) && result.selectionAttempts.length < 2) result.selectionAttempts.push(attempt); } catch { /* Ancillary observation only. */ }
  return attempt;
}

export function projectDatabaseSelectionCandidates(source, barrier, database, { check = () => {}, deadline = Infinity } = {}) {
  const output = { state: 'unknown', candidateCount: null, candidates: [], rawTextOrSqlOrHeadersPersisted: false };
  const expires = Math.min(Date.now() + 100, deadline);
  try {
    check();
    if (Date.now() >= expires) { output.state = 'timeout'; return output; }
    if (!Array.isArray(source)) return output;
    const count = safeObservationNumber(ownObservationValue(source, 'length'));
    if (count === null) return output;
    if (count > 128) { output.state = 'count-limit'; return output; }
    output.candidateCount = count;
    const afterSequence = safeObservationNumber(ownObservationValue(barrier, 'afterSequence'));
    const afterRequestSequence = safeObservationNumber(ownObservationValue(barrier, 'afterRequestSequence'));
    for (let index = 0; index < count && index < 128; index += 1) {
      check();
      if (Date.now() >= expires) { output.state = 'timeout'; return output; }
      const item = ownObservationValue(source, String(index));
      const sequence = safeObservationNumber(ownObservationValue(item, 'sequence'));
      const requestSequence = safeObservationNumber(ownObservationValue(item, 'requestSequence'));
      const launch = safeObservationNumber(ownObservationValue(item, 'launch'));
      const method = ownObservationValue(item, 'method');
      const candidatePath = ownObservationValue(item, 'path');
      const httpStatus = safeObservationNumber(ownObservationValue(item, 'httpStatus'), 599);
      const body = ownObservationValue(item, 'body');
      const activeDatabase = ownObservationValue(body, 'activeDatabase');
      const predicates = {
        receiptAfterBarrier: sequence === null || afterSequence === null ? null : sequence > afterSequence,
        requestAfterBarrier: requestSequence === null || afterRequestSequence === null ? null : requestSequence > afterRequestSequence,
        firstLaunch: launch === null ? null : launch === 1,
        putMethod: typeof method === 'string' ? method === 'PUT' : null,
        connectionsPath: typeof candidatePath === 'string' ? candidatePath === connectionPath : null,
        status200: httpStatus === null || httpStatus < 100 ? null : httpStatus === 200,
        targetDatabase: typeof database === 'string' && typeof activeDatabase === 'string' ? activeDatabase === database : null,
      };
      const rejectedBy = Object.keys(predicates).filter((name) => predicates[name] === false);
      const unknownPredicates = Object.keys(predicates).filter((name) => predicates[name] === null);
      output.candidates.push({ ordinal: index, sequence, requestSequence, httpStatus: httpStatus !== null && httpStatus >= 100 ? httpStatus : null,
        predicates, rejectedBy, unknownPredicates,
        reason: unknownPredicates.length ? 'predicate-unknown' : rejectedBy.length ? 'predicate-mismatch' : 'find-predicates-match' });
    }
    check();
    if (Date.now() >= expires) { output.state = 'timeout'; return output; }
    output.state = 'complete'; return output;
  } catch {
    output.state = 'unknown'; output.candidates = []; return output;
  }
}

export function projectDatabaseSelectionDom(value) {
  const activeNodeCount = safeObservationNumber(ownObservationValue(value, 'activeNodeCount'), 16);
  const identityNodeCount = safeObservationNumber(ownObservationValue(value, 'identityNodeCount'), 16);
  const boolean = (name) => { const field = ownObservationValue(value, name); return typeof field === 'boolean' ? field : null; };
  const activeMatchesTarget = boolean('activeMatchesTarget');
  const identityMatchesTarget = boolean('identityMatchesTarget');
  const contractWarningPresent = boolean('contractWarningPresent');
  return { state: [activeNodeCount, identityNodeCount, activeMatchesTarget, identityMatchesTarget, contractWarningPresent].includes(null) ? 'unknown' : 'observed',
    activeNodeCount, identityNodeCount, activeMatchesTarget, identityMatchesTarget, contractWarningPresent };
}

export function projectDatabaseSelectionPrecondition(value) {
  const dom = projectDatabaseSelectionDom(value);
  const consistent = ownObservationValue(value, 'activeIdentityConsistent');
  dom.activeIdentityConsistent = typeof consistent === 'boolean' ? consistent : null;
  const result = { schema: 'sonnetdb.wb65.selection-precondition.v1', state: 'unknown', reason: 'invalid-observation',
    dom, readCalls: 0, rawTextOrSqlOrHeadersPersisted: false };
  if (dom.state !== 'observed' || dom.activeIdentityConsistent === null) return result;
  if (dom.activeNodeCount !== 1 || dom.identityNodeCount !== 1) { result.reason = 'node-count-not-one'; return result; }
  if (dom.contractWarningPresent) { result.reason = 'contract-warning'; return result; }
  if (!dom.activeIdentityConsistent) { result.reason = 'identity-mismatch'; return result; }
  if (dom.activeMatchesTarget !== dom.identityMatchesTarget) { result.reason = 'target-mismatch'; return result; }
  result.state = dom.activeMatchesTarget ? 'target-already-active' : 'change-required';
  result.reason = dom.activeMatchesTarget ? 'target-already-active' : 'different-active-database';
  return result;
}

export async function observeDatabaseSelectionPrecondition(readDom, { check = () => {}, signal, deadline = Infinity } = {}) {
  let result = projectDatabaseSelectionPrecondition(null);
  let timer;
  let stop;
  let readCalls = 0;
  const expires = Math.min(Date.now() + 2000, deadline);
  const unknown = (reason) => ({ ...projectDatabaseSelectionPrecondition(null), reason, readCalls });
  try {
    check();
    if (signal?.aborted) return unknown('cancelled');
    if (Date.now() >= expires) return unknown('timeout');
    const stopped = new Promise((resolve) => {
      stop = () => resolve({ stopped: 'cancelled' });
      signal?.addEventListener('abort', stop, { once: true });
      timer = setTimeout(() => resolve({ stopped: 'timeout' }), Math.max(1, expires - Date.now()));
    });
    readCalls = 1;
    // This bounds our wait, not the underlying page.evaluate operation. Its
    // eventual rejection stays handled; cancellation of that read is unproved.
    const reading = Promise.resolve().then(readDom).then((value) => ({ value }), () => ({ stopped: 'read-error' }));
    const observation = await Promise.race([reading, stopped]);
    check();
    if (signal?.aborted) return unknown('cancelled');
    if (Date.now() >= expires) return unknown('timeout');
    if (observation.stopped) return unknown(observation.stopped);
    result = projectDatabaseSelectionPrecondition(observation.value);
    result.readCalls = readCalls;
    check();
    if (Date.now() >= expires) return unknown('timeout');
    return result;
  } catch { return unknown('guard-refused'); }
  finally {
    clearTimeout(timer);
    if (stop) signal?.removeEventListener('abort', stop);
  }
}

export async function rejectDatabaseSelectionFailure(primary, attempt, options) {
  let timer;
  try {
    const candidates = ownObservationValue(options, 'candidates');
    const barrier = ownObservationValue(options, 'barrier');
    const database = ownObservationValue(options, 'database');
    const readDom = ownObservationValue(options, 'readDom');
    const suppliedCheck = ownObservationValue(options, 'check');
    const check = typeof suppliedCheck === 'function' ? suppliedCheck : () => {};
    const phase = ownObservationValue(attempt, 'phase');
    const failure = { phase: ['before-click', 'click', 'ack-poll', 'dom-poll', 'disk', 'contract', 'evidence', 'accepted'].includes(phase) ? phase : 'unknown',
      terminationReason: 'unknown', candidates: projectDatabaseSelectionCandidates(candidates, barrier, database, { check }),
      dom: projectDatabaseSelectionDom(null) };
    attempt.failure = failure; attempt.phase = 'failed';
    check();
    const expires = Date.now() + 2000;
    const expired = new Promise((resolve) => { timer = setTimeout(() => resolve(null), 2000); });
    const value = await Promise.race([readDom(), expired]);
    check();
    if (Date.now() < expires) {
      const projected = projectDatabaseSelectionDom(value);
      check();
      if (Date.now() < expires) failure.dom = projected;
    }
  } catch { /* Getter, timeout, cancellation and observer faults never replace the original error. */ }
  finally { clearTimeout(timer); }
  throw primary;
}

export function admitNativeStudioRoot(candidate, expected, commit) {
  const same = (left, right) => left && right && left.processId === right.processId && left.parentProcessId === right.parentProcessId
    && left.creationTimeUtc === right.creationTimeUtc && left.commandLine === right.commandLine && left.executablePath === right.executablePath;
  if (!candidate || candidate.processId !== expected.processId || candidate.parentProcessId !== expected.parentProcessId
    || typeof candidate.executablePath !== 'string' || candidate.executablePath.toLowerCase() !== expected.executablePath.toLowerCase()
    || typeof candidate.commandLine !== 'string' || !candidate.commandLine.includes(expected.dataRoot)
    || typeof candidate.creationTimeUtc !== 'string' || !Number.isFinite(Date.parse(candidate.creationTimeUtc))
    || !Array.isArray(candidate.parentChain) || candidate.parentChain.length > 12
    || !candidate.parentChain.some((parent) => same(parent, expected.runnerIdentity))) fail('native root candidate identity/parent chain was refused.');
  const identityKey = `${candidate.processId}:${candidate.creationTimeUtc}`;
  if (!Array.isArray(expected.ownedIdentityKeys) || expected.ownedIdentityKeys.length > expected.maximumOwned
    || expected.ownedIdentityKeys.length >= expected.maximumOwned && !expected.ownedIdentityKeys.includes(identityKey)) fail('native root candidate ownership cap was refused.');
  // The sole authority callback is reached only after every identity and
  // capacity check. Rejected candidates remain unavailable to cleanup.
  commit(candidate);
}

export function compactDatabaseProcessEvidence(compact, input) {
  if (!input || !Array.isArray(input.helpers) || input.helpers.length > 96 || !Array.isArray(input.events) || input.events.length > 24) fail('process evidence input count exceeded.');
  const segments = [];
  const expires = Date.now() + 1000;
  const segmentCount = Math.max(1, Math.ceil(input.helpers.length / 64));
  for (let index = 0; index < segmentCount && index < 2; index += 1) {
    if (Date.now() >= expires) fail('process evidence segmentation deadline exceeded.');
    const offset = index * 64;
    const helperSlice = input.helpers.slice(offset, offset + 64);
    const evidence = compact({ ...input, helpers: helperSlice, events: index === 0 ? input.events : [] });
    if (!Array.isArray(evidence?.helpers) || evidence.helpers.length !== helperSlice.length
      || !Array.isArray(evidence.events) || evidence.events.length !== (index === 0 ? input.events.length : 0)) fail('process compactor omitted helper/event evidence.');
    segments.push({ helperOffset: offset, helperCount: helperSlice.length, evidence });
  }
  if (segments.reduce((count, segment) => count + segment.evidence.helpers.length, 0) !== input.helpers.length) fail('process helper evidence count disagrees.');
  return { schema: 'sonnetdb.wb61.native-process-segments.v1', helperCount: input.helpers.length, eventCount: input.events.length,
    segments, complete: true, rawConsoleOrHeadersPersisted: false };
}

export function projectDatabaseSnapshot(value, { disk = false } = {}) {
  if (!value || typeof value !== 'object' || Array.isArray(value)
    || !Array.isArray(value.profiles) || value.profiles.length !== 1) fail('one isolated profile is required.');
  const allowed = new Set(['profiles', 'activeProfileId', 'activeDatabase', 'activeIdentity']);
  if (Object.keys(value).length > 8 || Object.keys(value).some((name) => !allowed.has(name))) fail('unexpected library fields are refused.');
  const profile = value.profiles[0];
  const profileAllowed = new Set(['id', 'name', 'kind', 'baseUrl', 'defaultDatabase', 'tokenMode', 'createdAt', 'updatedAt', 'identity']);
  if (!profile || typeof profile !== 'object' || Object.keys(profile).length > 10
    || Object.keys(profile).some((name) => !profileAllowed.has(name))) fail('unexpected profile fields are refused.');
  const identity = (input) => {
    if (!input || typeof input !== 'object' || Array.isArray(input)
      || Object.keys(input).length !== 4 || Object.keys(input).some((name) => !['host', 'profileId', 'baseUrl', 'database'].includes(name))) fail('complete public identity is required.');
    return { host: input.host, profileId: input.profileId, baseUrl: input.baseUrl, database: input.database };
  };
  const result = { activeProfileId: value.activeProfileId, activeDatabase: value.activeDatabase,
    profiles: [{ id: profile.id, kind: profile.kind, baseUrl: profile.baseUrl, defaultDatabase: profile.defaultDatabase, tokenMode: profile.tokenMode }] };
  if (!disk) {
    result.activeIdentity = identity(value.activeIdentity);
    result.profiles[0].identity = identity(profile.identity);
  } else {
    const hasActiveIdentity = Object.hasOwn(value, 'activeIdentity');
    const hasProfileIdentity = Object.hasOwn(profile, 'identity');
    if (hasActiveIdentity !== hasProfileIdentity) fail('paired disk identity fields are required.');
    if (hasActiveIdentity) {
      // SaveAsync persists these derived properties. Validate their exact
      // relationship before discarding them; disk data cannot certify a bridge ack.
      const activeIdentity = identity(value.activeIdentity);
      const profileIdentity = identity(profile.identity);
      const matches = (input, profileId, database) => input.host === 'studio-desktop'
        && typeof input.profileId === 'string' && input.profileId.length > 0 && input.profileId === profileId
        && typeof input.baseUrl === 'string' && input.baseUrl.length > 0 && input.baseUrl === profile.baseUrl
        && typeof input.database === 'string' && input.database.length > 0 && input.database === database;
      if (value.activeProfileId !== profile.id
        || !matches(activeIdentity, value.activeProfileId, value.activeDatabase)
        || !matches(profileIdentity, profile.id, profile.defaultDatabase)) fail('derived disk identity disagrees.');
    }
  }
  return result;
}

export function assertDatabaseSnapshot(snapshot, database, origin, { disk = false } = {}) {
  if (typeof database !== 'string' || !database || database === '__control_plane__') fail('ordinary database name required.');
  const profile = snapshot?.profiles?.[0];
  if (snapshot?.profiles?.length !== 1 || snapshot.activeProfileId !== 'managed-local' || snapshot.activeDatabase !== database
    || profile.id !== 'managed-local' || profile.kind !== 'managed-local' || profile.baseUrl !== origin
    || profile.defaultDatabase !== database || profile.tokenMode !== 'current-session') fail('database/profile/default spelling disagrees.');
  if (!disk) {
    for (const identity of [snapshot.activeIdentity, profile.identity]) {
      if (identity?.host !== 'studio-desktop' || identity.profileId !== 'managed-local'
        || identity.baseUrl !== origin || identity.database !== database) fail('native identity disagrees.');
    }
  }
  return true;
}

export function assertDatabaseAcknowledgement(ack, { afterSequence, afterRequestSequence, launch, method, database, origin }) {
  if (!Number.isSafeInteger(afterSequence) || afterSequence < 0 || !Number.isSafeInteger(afterRequestSequence) || afterRequestSequence < 0
    || !ack || !Number.isSafeInteger(ack.sequence) || ack.sequence <= afterSequence
    || !Number.isSafeInteger(ack.requestSequence) || ack.requestSequence <= afterRequestSequence || ack.launch !== launch
    || ack.method !== method || ack.path !== connectionPath || ack.httpStatus !== 200) fail('fresh native acknowledgement is missing.');
  assertDatabaseSnapshot(ack.body, database, origin);
  return true;
}

export function assertDatabaseClose(value) {
  if (value?.method !== 'CloseMainWindow' || value.accepted !== true || value.exitCode !== 0 || value.exitSignal !== null
    || value.studioIdentityExited !== true || value.serverIdentityExited !== true || value.allOwnedIdentitiesExited !== true
    || value.allFourPortsReleased !== true || value.fallbackUsed !== false) fail('normal desktop exit and strict absence are required.');
  return true;
}

export function readDatabaseSqlResult(text, expected) {
  if (typeof text !== 'string' || Buffer.byteLength(text) > 65_536) fail('SQL response exceeds known fixture boundary.');
  const lines = text.split(/\r?\n/u).filter(Boolean);
  if (lines.length !== 3) fail('one complete SQL result is required.');
  const [meta, row, end] = lines.map((line) => JSON.parse(line));
  const elapsed = end.elapsedMilliseconds ?? end.elapsedMs;
  if (meta.type !== 'meta' || JSON.stringify(meta.columns) !== JSON.stringify(['Marker'])
    || !Array.isArray(row) || row.length !== 1 || row[0] !== expected || end.type !== 'end' || end.rowCount !== 1
    || !Number.isSafeInteger(end.recordsAffected) || end.recordsAffected < -1
    || typeof elapsed !== 'number' || !Number.isFinite(elapsed) || elapsed < 0
    || (end.truncated !== undefined && end.truncated !== false)) fail('real database sentinel or terminal result disagrees.');
  return { columns: ['Marker'], rows: [[expected]], rowCount: 1, complete: true };
}

export function createDatabaseRecoveryContract({ databaseA, databaseB, origin }) {
  if (!databaseA || !databaseB || databaseA === databaseB || databaseA.toLowerCase() === databaseA || databaseB.toLowerCase() === databaseB) fail('two distinct mixed-case databases required.');
  let phase = 0;
  let previousSequence = 0;
  let firstIdentityKey;
  const requirePhase = (expected) => { if (phase !== expected) fail('journey order was not proved.'); };
  return {
    launched(identityKey) {
      requirePhase(0);
      if (typeof identityKey !== 'string' || !identityKey) fail('first native launch identity required.');
      firstIdentityKey = identityKey; phase = 1;
    },
    selected(database, ack, dom, disk, barrier) {
      requirePhase(database === databaseA ? 1 : database === databaseB ? 2 : -1);
      const { afterSequence, afterRequestSequence } = barrier ?? {};
      if (!Number.isSafeInteger(afterSequence) || afterSequence < previousSequence) fail('selection must follow the previous acknowledgement.');
      assertDatabaseAcknowledgement(ack, { afterSequence, afterRequestSequence, launch: 1, method: 'PUT', database, origin });
      if (dom?.activeDatabase !== database || dom.hostIdentity !== `studio-desktop · managed-local · ${origin} · ${database}` || dom.contractWarning !== false) fail('rendered database/identity disagrees.');
      assertDatabaseSnapshot(disk, database, origin, { disk: true });
      previousSequence = ack.sequence; phase += 1;
    },
    firstClosed(close) { requirePhase(3); assertDatabaseClose(close); phase = 4; },
    relaunched(identityKey) {
      requirePhase(4);
      if (typeof identityKey !== 'string' || !identityKey || identityKey === firstIdentityKey) fail('second native identity required; reload is insufficient.');
      phase = 5;
    },
    restored(ack, dom, disk, barrier) {
      requirePhase(5);
      assertDatabaseAcknowledgement(ack, { ...barrier, launch: 2, method: 'GET', database: databaseB, origin });
      if (dom?.activeDatabase !== databaseB || dom.hostIdentity !== `studio-desktop · managed-local · ${origin} · ${databaseB}` || dom.contractWarning !== false) fail('second native DOM did not restore B.');
      assertDatabaseSnapshot(disk, databaseB, origin, { disk: true }); phase = 6;
    },
    queried(query) {
      requirePhase(6);
      if (query?.database !== databaseB || query.requestPath !== `/v1/db/${encodeURIComponent(databaseB)}/sql`
        || query.method !== 'POST' || query.httpStatus !== 200 || query.requestSql !== 'SELECT "Marker" FROM "WB61Probe"'
        || query.actual?.rows?.[0]?.[0] !== query.sentinelB || query.sentinelA === query.sentinelB
        || query.rendered !== query.sentinelB || query.actual?.complete !== true) fail('B request/response/rendered sentinel was not proved.');
      phase = 7;
    },
    secondClosed(close) { requirePhase(7); assertDatabaseClose(close); phase = 8; },
    get passed() { return phase === 8; },
  };
}

export async function runDatabaseRecoveryScenario(harness) {
  const { origin, api, auth, poll, check, evidence, bridgeEvidence, readLibrary, getPage, launchIdentityKey,
    closeDesktop, relaunchDesktop, setStage, observationBarrier } = harness;
  const suffix = harness.runId.slice(-8);
  const databaseA = `WB61_Alpha_${suffix}`;
  const databaseB = `WB61_Bravo_${suffix}`;
  const sentinelA = 'WB61_A';
  const sentinelB = 'WB61_B';
  const querySql = 'SELECT "Marker" FROM "WB61Probe"';
  const result = { schema: 'sonnetdb.wb61.database-recovery.v1', databaseA, databaseB, sentinelA, sentinelB,
    seedFailures: [], selectionAttempts: [], selections: [], firstClose: null, secondLaunch: null, restored: null, query: null, secondClose: null, passed: false,
    boundary: 'Real API seed and auth preparation only; ordinary DOM database selection, native PUT/GET, exact owned disk semantics, actual desktop restart and read-only B query. Login UI, dialogs, installation, backup recovery and full three-host acceptance remain separate.' };
  // Only the native runner explicitly opts into this ordinary preparation.
  // Legacy callers retain the same-value refusal and the original result shape.
  if (harness.prepareFirstSelection === true) result.firstSelectionPreparation = {
    schema: 'sonnetdb.wb68.first-selection-preparation.v1', state: 'not-reached', initialPrecondition: null, attempt: null,
  };
  harness.setResult(result);
  const contract = createDatabaseRecoveryContract({ databaseA, databaseB, origin });
  contract.launched(launchIdentityKey());
  const executeSeed = async (operation, apiPath, sql) => {
    check();
    const raw = await api('POST', apiPath, { sql }, auth, true, { operation,
      record: (observation) => {
        if (!['create-database', 'create-table', 'insert-marker'].includes(operation) || result.seedFailures.length >= 1) return;
        result.seedFailures.push({ operation, observation });
      } });
    if (typeof raw !== 'string' || Buffer.byteLength(raw) > 65_536) fail('seed result size/type failed.');
    const lines = raw.split(/\r?\n/u).filter(Boolean);
    if (lines.length > 8 || !lines.length) fail('seed result count failed.');
    const values = lines.map((line) => JSON.parse(line));
    const terminal = values.at(-1);
    const elapsed = terminal?.elapsedMilliseconds ?? terminal?.elapsedMs;
    if (values.some((value) => value?.type === 'error' || value?.error || value?.code)
      || terminal?.type !== 'end' || !Number.isSafeInteger(terminal.rowCount) || terminal.rowCount !== 0
      || !Number.isSafeInteger(terminal.recordsAffected) || terminal.recordsAffected < -1
      || typeof elapsed !== 'number' || !Number.isFinite(elapsed) || elapsed < 0
      || values.filter((value) => value?.type === 'end').length !== 1
      || values.some((value) => Array.isArray(value) || value?.type !== 'meta' && value?.type !== 'end')) fail('real seed SQL did not complete.');
  };
  setStage('real isolated mixed-case database seed');
  for (const [database, sentinel] of [[databaseA, sentinelA], [databaseB, sentinelB]]) {
    await executeSeed('create-database', '/v1/sql', `CREATE DATABASE "${database}"`);
    await executeSeed('create-table', `/v1/db/${encodeURIComponent(database)}/sql`, 'CREATE TABLE "WB61Probe" ("Marker" STRING, PRIMARY KEY ("Marker"))');
    await executeSeed('insert-marker', `/v1/db/${encodeURIComponent(database)}/sql`, `INSERT INTO "WB61Probe" ("Marker") VALUES ('${sentinel}')`);
    readDatabaseSqlResult(await api('POST', `/v1/db/${encodeURIComponent(database)}/sql`, { sql: querySql }, auth, true), sentinel);
  }
  const boundedRead = async (operation) => {
    check();
    let timer;
    try {
      const value = await Promise.race([operation(), new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error('Known database DOM/response read exceeded 2 seconds.')), 2000);
      })]);
      check(); return value;
    } finally { clearTimeout(timer); }
  };
  const dom = async () => {
    check();
    return boundedRead(() => getPage().evaluate(() => {
      const nodes = [...document.querySelectorAll('.schema-database-node .schema-item--database.is-active')];
      if (nodes.length !== 1) return { activeDatabase: null, hostIdentity: null, contractWarning: true };
      return { activeDatabase: nodes[0].querySelector('strong')?.textContent ?? null,
        hostIdentity: document.querySelector('[data-testid="studio-host-identity"]')?.textContent?.slice(0, 1024) ?? null,
        contractWarning: Boolean(document.querySelector('[data-testid="studio-managed-contract-warning"]')) };
    }));
  };
  const prepareExplorer = async ({ refresh = false } = {}) => {
    const page = getPage();
    if (await page.getByTitle('展开资源浏览器', { exact: true }).count() === 1) await page.getByTitle('展开资源浏览器', { exact: true }).click({ timeout: 5000 });
    if (refresh) await page.getByTitle('刷新资源', { exact: true }).click({ timeout: 5000 });
    const group = page.locator('.schema-group--databases');
    if (await group.locator('.schema-group__items').count() === 0) await group.locator('.schema-group__head').click({ timeout: 5000 });
    await poll('Both real seeded databases in ordinary Explorer', async () => {
      const names = await page.locator('.schema-database-node .schema-item--database strong').allTextContents();
      return names.length <= 16 && names.includes(databaseA) && names.includes(databaseB);
    }, { attempts: 30, timeoutMs: 20_000, intervalMs: 500 });
  };
  const selectionPrecondition = (page, database) => observeDatabaseSelectionPrecondition(() => page.evaluate(({ database, identityPrefix }) => {
    const nodes = document.querySelectorAll('.schema-database-node .schema-item--database.is-active');
    const identities = document.querySelectorAll('[data-testid="studio-host-identity"]');
    const activeText = nodes.length === 1 ? nodes[0].querySelector('strong')?.textContent : null;
    const identityText = identities.length === 1 ? identities[0].textContent : null;
    const activeKnown = typeof activeText === 'string' && activeText.length > 0 && activeText.length <= 1024;
    const identityKnown = typeof identityText === 'string' && identityText.length <= 1024;
    // A case variant alone cannot prove a different database.
    const activeCaseMismatch = activeKnown && activeText !== database && activeText.toLowerCase() === database.toLowerCase();
    return { activeNodeCount: nodes.length <= 16 ? nodes.length : null, identityNodeCount: identities.length <= 16 ? identities.length : null,
      activeMatchesTarget: activeKnown && !activeCaseMismatch ? activeText === database : null,
      identityMatchesTarget: identityKnown ? identityText === `${identityPrefix}${database}` : null,
      activeIdentityConsistent: activeKnown && identityKnown ? identityText === `${identityPrefix}${activeText}` : null,
      contractWarningPresent: Boolean(document.querySelector('[data-testid="studio-managed-contract-warning"]')) };
  }, { database, identityPrefix: `studio-desktop · managed-local · ${origin} · ` }), { check });
  await prepareExplorer({ refresh: true });
  for (const database of [databaseA, databaseB]) {
    setStage(`normal DOM select ${database}`);
    const page = getPage();
    const button = page.locator('.schema-database-node .schema-item--database').filter({ has: page.locator('strong', { hasText: new RegExp(`^${database}$`, 'u') }) });
    if (await button.count() !== 1) fail('ordinary exact database button is not unique.');
    let precondition = await selectionPrecondition(page, database);
    if (database === databaseA && harness.prepareFirstSelection === true) {
      const preparation = result.firstSelectionPreparation;
      preparation.initialPrecondition = precondition;
      preparation.state = precondition.state === 'change-required' ? 'not-needed' : 'refused';
      if (precondition.state === 'target-already-active') {
        // This one preparatory B click is separate from contract.selected and
        // both journey arrays. It consumes the existing runner budgets.
        setStage('ordinary B preparation before first A selection');
        preparation.state = 'preparing';
        const prepared = { schema: 'sonnetdb.wb68.preparation-attempt.v1', target: 'B', phase: 'button', barrier: null,
          precondition: null, ackPoll: { callbackCalls: 0, elapsedMs: null }, failure: null, selection: null };
        preparation.attempt = prepared;
        let preparationBarrier;
        let preparationPollStartedAt = null;
        try {
          check();
          const preparationButton = page.locator('.schema-database-node .schema-item--database').filter({ has: page.locator('strong', { hasText: new RegExp(`^${databaseB}$`, 'u') }) });
          if (await preparationButton.count() !== 1) fail('ordinary exact preparation database button is not unique.');
          prepared.phase = 'before-click-precondition';
          prepared.precondition = await selectionPrecondition(page, databaseB);
          preparationBarrier = observationBarrier();
          prepared.barrier = { afterSequence: safeObservationNumber(ownObservationValue(preparationBarrier, 'afterSequence')),
            afterRequestSequence: safeObservationNumber(ownObservationValue(preparationBarrier, 'afterRequestSequence')) };
          check();
          if (prepared.precondition.state !== 'change-required') fail(`ordinary preparation precondition refused (${prepared.precondition.state}/${prepared.precondition.reason}).`);
          prepared.phase = 'click';
          await preparationButton.locator('strong').click({ timeout: 5000 });
          prepared.phase = 'ack-poll';
          preparationPollStartedAt = Date.now();
          const ack = await poll('Fresh ordinary preparation native PUT', async () => {
            prepared.ackPoll.callbackCalls += 1;
            return bridgeEvidence.find((item) => item.sequence > preparationBarrier.afterSequence
              && item.requestSequence > preparationBarrier.afterRequestSequence
              && item.launch === 1 && item.method === 'PUT' && item.path === connectionPath && item.httpStatus === 200 && item.body.activeDatabase === databaseB);
          }, { attempts: 30, timeoutMs: 20_000, intervalMs: 250 });
          prepared.ackPoll.elapsedMs = safeObservationNumber(Date.now() - preparationPollStartedAt, 900_000);
          prepared.phase = 'dom-poll';
          const rendered = await poll('Confirmed ordinary preparation database DOM', async () => { const value = await dom(); return value.activeDatabase === databaseB && value.hostIdentity === `studio-desktop · managed-local · ${origin} · ${databaseB}` ? value : false; },
            { attempts: 30, timeoutMs: 20_000, intervalMs: 250 });
          prepared.phase = 'disk';
          const disk = await readLibrary();
          prepared.phase = 'contract';
          assertDatabaseAcknowledgement(ack, { ...preparationBarrier, launch: 1, method: 'PUT', database: databaseB, origin });
          if (rendered?.activeDatabase !== databaseB || rendered.hostIdentity !== `studio-desktop · managed-local · ${origin} · ${databaseB}` || rendered.contractWarning !== false) fail('preparation rendered database/identity disagrees.');
          assertDatabaseSnapshot(disk.snapshot, databaseB, origin, { disk: true });
          prepared.selection = { database: databaseB, barrier: preparationBarrier, acknowledgement: ack, dom: rendered, disk };
          prepared.phase = 'accepted'; preparation.state = 'prepared';
        } catch (primary) {
          preparation.state = 'refused';
          if (prepared.phase === 'button' || prepared.phase === 'before-click-precondition') {
            prepared.failure = { phase: prepared.phase, terminationReason: 'precondition-refused', dom: prepared.precondition?.dom ?? projectDatabaseSelectionDom(null) };
            prepared.phase = 'failed'; throw primary;
          }
          if (prepared.phase === 'ack-poll' && preparationPollStartedAt !== null) prepared.ackPoll.elapsedMs = safeObservationNumber(Date.now() - preparationPollStartedAt, 900_000);
          await rejectDatabaseSelectionFailure(primary, prepared, { candidates: bridgeEvidence, barrier: preparationBarrier, database: databaseB, check,
            readDom: () => boundedRead(() => getPage().evaluate(({ database, expectedIdentity }) => {
              const nodes = document.querySelectorAll('.schema-database-node .schema-item--database.is-active');
              const identities = document.querySelectorAll('[data-testid="studio-host-identity"]');
              const activeText = nodes.length === 1 ? nodes[0].querySelector('strong')?.textContent : null;
              const identityText = identities.length === 1 ? identities[0].textContent : null;
              return { activeNodeCount: nodes.length <= 16 ? nodes.length : null, identityNodeCount: identities.length <= 16 ? identities.length : null,
                activeMatchesTarget: typeof activeText === 'string' ? activeText === database : null,
                identityMatchesTarget: typeof identityText === 'string' ? identityText === expectedIdentity : null,
                contractWarningPresent: Boolean(document.querySelector('[data-testid="studio-managed-contract-warning"]')) };
            }, { database: databaseB, expectedIdentity: `studio-desktop · managed-local · ${origin} · ${databaseB}` })) });
        }
        // A real B acknowledgement is preparation only. A must pass a new DOM
        // read and new genuine barriers before the original journey can begin.
        precondition = await selectionPrecondition(page, databaseA);
        setStage(`normal DOM select ${database}`);
      }
    }
    // The ordinary DOM read completes before this synchronous barrier capture.
    // It does not prove which earlier UI action originated an observed request.
    const barrier = observationBarrier();
    const attempt = beginDatabaseSelectionAttempt(result, database === databaseA ? 'A' : 'B', barrier);
    attempt.phase = 'before-click-precondition';
    attempt.precondition = precondition;
    try {
      check();
      if (precondition.state !== 'change-required') fail(`ordinary selection precondition refused (${precondition.state}/${precondition.reason}).`);
    } catch (primary) {
      attempt.failure = { phase: 'before-click-precondition', terminationReason: 'precondition-refused', dom: precondition.dom };
      attempt.phase = 'failed';
      throw primary;
    }
    let ackPollStartedAt = null;
    try {
      attempt.phase = 'click';
      await button.locator('strong').click({ timeout: 5000 });
      attempt.phase = 'ack-poll';
      ackPollStartedAt = Date.now();
      const ack = await poll('Fresh ordinary selection native PUT', async () => {
        attempt.ackPoll.callbackCalls += 1;
        return bridgeEvidence.find((item) => item.sequence > barrier.afterSequence
          && item.requestSequence > barrier.afterRequestSequence
          && item.launch === 1 && item.method === 'PUT' && item.path === connectionPath && item.httpStatus === 200 && item.body.activeDatabase === database);
      },
      { attempts: 30, timeoutMs: 20_000, intervalMs: 250 });
      attempt.ackPoll.elapsedMs = safeObservationNumber(Date.now() - ackPollStartedAt, 900_000);
      attempt.phase = 'dom-poll';
      const rendered = await poll('Confirmed ordinary database DOM', async () => { const value = await dom(); return value.activeDatabase === database && value.hostIdentity === `studio-desktop · managed-local · ${origin} · ${database}` ? value : false; },
        { attempts: 30, timeoutMs: 20_000, intervalMs: 250 });
      attempt.phase = 'disk';
      const disk = await readLibrary();
      attempt.phase = 'contract';
      contract.selected(database, ack, rendered, disk.snapshot, barrier);
      const selection = { database, barrier, acknowledgement: ack, dom: rendered, disk };
      result.selections.push(selection); attempt.phase = 'evidence'; await evidence(`database-selection-${result.selections.length}.json`, selection);
      attempt.phase = 'accepted';
    } catch (primary) {
      if (attempt.phase === 'ack-poll' && ackPollStartedAt !== null) attempt.ackPoll.elapsedMs = safeObservationNumber(Date.now() - ackPollStartedAt, 900_000);
      await rejectDatabaseSelectionFailure(primary, attempt, { candidates: bridgeEvidence, barrier, database, check,
        readDom: () => boundedRead(() => getPage().evaluate(({ database, expectedIdentity }) => {
          const nodes = document.querySelectorAll('.schema-database-node .schema-item--database.is-active');
          const identities = document.querySelectorAll('[data-testid="studio-host-identity"]');
          const activeText = nodes.length === 1 ? nodes[0].querySelector('strong')?.textContent : null;
          const identityText = identities.length === 1 ? identities[0].textContent : null;
          return { activeNodeCount: nodes.length <= 16 ? nodes.length : null, identityNodeCount: identities.length <= 16 ? identities.length : null,
            activeMatchesTarget: typeof activeText === 'string' ? activeText === database : null,
            identityMatchesTarget: typeof identityText === 'string' ? identityText === expectedIdentity : null,
            contractWarningPresent: Boolean(document.querySelector('[data-testid="studio-managed-contract-warning"]')) };
        }, { database, expectedIdentity: `studio-desktop · managed-local · ${origin} · ${database}` })) });
    }
  }
  setStage('first normal desktop exit with data/profile/library retained');
  result.firstClose = await closeDesktop('first'); contract.firstClosed(result.firstClose);
  await evidence('database-first-close.json', result.firstClose);
  setStage('second actual desktop launch with same owned directories');
  result.secondLaunch = await relaunchDesktop(); contract.relaunched(launchIdentityKey());
  await prepareExplorer();
  const restoredAck = await poll('Second native bootstrap GET restores B', async () => bridgeEvidence.find((item) => item.sequence > result.secondLaunch.barrier.afterSequence
    && item.requestSequence > result.secondLaunch.barrier.afterRequestSequence
    && item.launch === 2 && item.method === 'GET' && item.path === connectionPath && item.httpStatus === 200 && item.body.activeDatabase === databaseB),
  { attempts: 30, timeoutMs: 20_000, intervalMs: 250 });
  const restoredDom = await poll('Second native ordinary DOM restores B', async () => { const value = await dom(); return value.activeDatabase === databaseB && value.hostIdentity === `studio-desktop · managed-local · ${origin} · ${databaseB}` ? value : false; },
    { attempts: 30, timeoutMs: 20_000, intervalMs: 250 });
  const restoredDisk = await readLibrary();
  contract.restored(restoredAck, restoredDom, restoredDisk.snapshot, result.secondLaunch.barrier);
  result.restored = { acknowledgement: restoredAck, dom: restoredDom, disk: restoredDisk };
  await evidence('database-restored.json', result.restored);
  setStage('normal SQL Run from restored real B');
  const page = getPage();
  const editor = page.locator('.sql-editor .cm-content');
  await editor.click({ timeout: 5000 }); await page.keyboard.press('Control+a'); await page.keyboard.insertText(querySql);
  const responsePromise = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return url.origin === origin && url.pathname === `/v1/db/${encodeURIComponent(databaseB)}/sql` && response.request().method() === 'POST';
  }, { timeout: 20_000 });
  // Attach a rejection handler immediately so a failed click cannot leak an
  // unhandled waiter; the waiter itself has a fixed 20-second bound.
  responsePromise.catch(() => {});
  await page.getByRole('button', { name: 'Run', exact: true }).click({ timeout: 5000 });
  const response = await responsePromise;
  const requestBody = response.request().postDataJSON();
  const actual = readDatabaseSqlResult(await boundedRead(() => response.text()), sentinelB);
  await page.getByTestId('sql-result-region').getByRole('button', { name: '表格', exact: true }).click({ timeout: 5000 });
  const rendered = await poll('Ordinary SQL result table shows B sentinel', async () => {
    const region = page.getByTestId('sql-result-region');
    const cells = await region.locator('td').allTextContents();
    return cells.length <= 16 && cells.filter((cell) => cell === sentinelB).length === 1 && !cells.includes(sentinelA) ? sentinelB : false;
  }, { attempts: 30, timeoutMs: 20_000, intervalMs: 250 });
  result.query = { database: databaseB, requestPath: new URL(response.url()).pathname, method: response.request().method(),
    requestSql: requestBody?.sql === querySql ? querySql : '[unexpected SQL omitted]', httpStatus: response.status(), actual, rendered, sentinelA, sentinelB };
  contract.queried(result.query); await evidence('database-query.json', result.query);
  setStage('second normal desktop exit and strict owned absence');
  result.secondClose = await closeDesktop('second'); contract.secondClosed(result.secondClose);
  await evidence('database-second-close.json', result.secondClose);
  result.passed = contract.passed;
  return result;
}
