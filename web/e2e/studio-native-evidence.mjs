// Pure evidence helpers: no process launches, filesystem discovery or native
// host emulation. The runner supplies its owned writer and absolute deadline.
export const NativeEvidenceMaxBytes = 524_288;
const failureCodes = new Set(['deadline', 'credential-budget', 'oversize', 'credential', 'serialization', 'identity', 'process-count',
  'identity-conflict', 'identity-table-count', 'parent-count', 'parent-conflict', 'event-identity-count', 'terminal-contract', 'writer-timeout']);

export class NativeEvidenceError extends Error {
  constructor(code) {
    super(`Native evidence ${code}.`);
    this.code = code;
  }
}

export function encodeNativeEvidence(value, secrets = [], { now = Date.now, deadline = now() + 1000 } = {}) {
  if (now() >= deadline) throw new NativeEvidenceError('deadline');
  const credentials = [...secrets];
  if (credentials.length > 16 || credentials.some((item) => typeof item !== 'string' || !item)) throw new NativeEvidenceError('credential-budget');
  let json;
  try { json = JSON.stringify(value, null, 2); } catch { throw new NativeEvidenceError('serialization'); }
  if (typeof json !== 'string') throw new NativeEvidenceError('serialization');
  const text = `${json}\n`;
  if (Buffer.byteLength(text) > NativeEvidenceMaxBytes) throw new NativeEvidenceError('oversize');
  for (let index = 0; index < credentials.length && index < 16; index += 1) {
    if (now() >= deadline) throw new NativeEvidenceError('deadline');
    if (text.includes(credentials[index])) throw new NativeEvidenceError('credential');
  }
  // The native bootstrap is an in-memory secret even if it has not been added
  // to the auth credential set. Raw headers/credential DTOs are never evidence.
  if (/\b[0-9a-f]{48}\b/iu.test(text) || /"(?:token|tokenId|password|bearerToken|authorization|X-SonnetDB-Studio-Bridge-Token)"\s*:/iu.test(text)) {
    throw new NativeEvidenceError('credential');
  }
  if (now() >= deadline) throw new NativeEvidenceError('deadline');
  return text;
}

export function nativeIdentityKey(identity) {
  if (!identity || !Number.isInteger(identity.processId) || identity.processId <= 0 || typeof identity.creationTimeUtc !== 'string'
    || !Number.isFinite(Date.parse(identity.creationTimeUtc))) throw new NativeEvidenceError('identity');
  return `${identity.processId}:${identity.creationTimeUtc}`;
}

// Statistics are optional observations from one helper envelope. Reading fixed
// own data properties never invokes an accessor or turns a rejected action
// into authority; absent or invalid values remain explicitly unknown.
export function projectNativeHelperStatistics(envelope) {
  const number = (field, count = false) => {
    try {
      if (!envelope || typeof envelope !== 'object') return null;
      const descriptor = Object.getOwnPropertyDescriptor(envelope, field);
      if (!descriptor || !Object.hasOwn(descriptor, 'value')) return null;
      const value = descriptor.value;
      if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) return null;
      return count && (!Number.isSafeInteger(value) || value > 160) ? null : value;
    } catch { return null; }
  };
  return { cimQueries: number('cimQueries', true), cachedPids: number('cachedPids', true), elapsedSeconds: number('elapsedSeconds') };
}

// A fixed, optional snapshot observation cannot grant process authority. Own
// data descriptors avoid invoking getters; hostile/missing data stays unknown.
export function projectNativeCimObservation(envelope, { action = 'snapshot', now = () => performance.now(), deadline } = {}) {
  if (action !== 'snapshot') return null;
  try {
    const tick = () => {
      const value = now();
      if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) throw new Error('Unknown observation clock.');
      return value;
    };
    let previousTick = tick();
    if (deadline !== undefined && (typeof deadline !== 'number' || !Number.isFinite(deadline))) return null;
    const expires = Math.min(deadline ?? Infinity, previousTick + 100);
    const check = () => {
      const current = tick();
      if (current < previousTick || current >= expires) throw new Error('Observation projection deadline.');
      previousTick = current;
    };
    const own = (object, field) => {
      check();
      if (!object || typeof object !== 'object') throw new Error('Unknown observation.');
      const descriptor = Object.getOwnPropertyDescriptor(object, field);
      if (!descriptor || !Object.hasOwn(descriptor, 'value')) throw new Error('Unknown observation.');
      return descriptor.value;
    };
    const value = own(envelope, 'cimObservation');
    if (own(value, 'schemaVersion') !== 1) return null;
    const state = own(value, 'state');
    if (!['complete', 'incomplete', 'unknown'].includes(state)) return null;
    const source = own(value, 'operations');
    if (!Array.isArray(source)) return null;
    const length = own(source, 'length');
    if (!Number.isSafeInteger(length) || length < 0 || length > 160) return null;
    const operations = [];
    let previousStarted = 0;
    for (let index = 0; index < length && index < 160; index += 1) {
      check();
      const item = own(source, String(index));
      const ordinal = own(item, 'ordinal');
      const phase = own(item, 'phase');
      const startedMilliseconds = own(item, 'startedMilliseconds');
      const elapsedMilliseconds = own(item, 'elapsedMilliseconds');
      const outcome = own(item, 'outcome');
      const resultCount = own(item, 'resultCount');
      const time = (number) => number === null || typeof number === 'number' && Number.isFinite(number) && number >= 0;
      if (ordinal !== index + 1 || !['self-handshake', 'parent-chain', 'seed-lookup', 'child-enumeration'].includes(phase)
        || !time(startedMilliseconds) || !time(elapsedMilliseconds) || !['returned', 'threw', 'unknown'].includes(outcome)
        || resultCount !== null && (!Number.isSafeInteger(resultCount) || resultCount < 0 || resultCount > (phase === 'child-enumeration' ? 64 : 1))
        || outcome !== 'returned' && resultCount !== null || startedMilliseconds === null && elapsedMilliseconds !== null
        || state === 'complete' && (startedMilliseconds === null || elapsedMilliseconds === null || outcome === 'unknown'
          || outcome === 'returned' && resultCount === null)) return null;
      if (startedMilliseconds !== null && startedMilliseconds < previousStarted) return null;
      previousStarted = startedMilliseconds ?? previousStarted;
      operations.push({ ordinal, phase, startedMilliseconds, elapsedMilliseconds, outcome, resultCount });
    }
    check();
    return { schemaVersion: 1, state, operations };
  } catch { return null; }
}

export function compactNativeProcessEvidence({ runnerIdentity, studioIdentity, events, helpers, streamCounts }, { now = Date.now, deadline = now() + 1000 } = {}) {
  if (!Array.isArray(events) || events.length > 24 || !Array.isArray(helpers) || helpers.length > 64) throw new NativeEvidenceError('process-count');
  const table = new Map();
  const check = () => { if (now() >= deadline) throw new NativeEvidenceError('deadline'); };
  const registerCore = (identity) => {
    check();
    const identityKey = nativeIdentityKey(identity);
    const core = { identityKey, processId: identity.processId, parentProcessId: identity.parentProcessId,
      creationTimeUtc: identity.creationTimeUtc, commandLine: identity.commandLine, executablePath: identity.executablePath };
    if (!Number.isInteger(core.parentProcessId) || core.parentProcessId < 0 || typeof core.commandLine !== 'string'
      || typeof core.executablePath !== 'string' || !core.commandLine.trim() || !core.executablePath.trim()) throw new NativeEvidenceError('identity');
    const previous = table.get(identityKey);
    if (previous && (previous.parentProcessId !== core.parentProcessId || previous.commandLine !== core.commandLine || previous.executablePath !== core.executablePath)) {
      throw new NativeEvidenceError('identity-conflict');
    }
    if (!previous) {
      if (table.size >= 256) throw new NativeEvidenceError('identity-table-count');
      table.set(identityKey, { ...core, parentChainKeys: [] });
    }
    return identityKey;
  };
  const reference = (identity) => {
    if (!identity) return null;
    const identityKey = registerCore(identity);
    const chain = identity.parentChain ?? [];
    if (!Array.isArray(chain) || chain.length > 12) throw new NativeEvidenceError('parent-count');
    const chainKeys = [];
    for (let index = 0; index < chain.length && index < 12; index += 1) { check(); chainKeys.push(registerCore(chain[index])); }
    const entry = table.get(identityKey);
    const shared = Math.min(entry.parentChainKeys.length, chainKeys.length);
    for (let index = 0; index < shared && index < 12; index += 1) {
      check();
      if (entry.parentChainKeys[index] !== chainKeys[index]) throw new NativeEvidenceError('parent-conflict');
    }
    if (chainKeys.length >= entry.parentChainKeys.length) {
      entry.parentChainKeys = chainKeys;
      entry.parentChainBoundary = identity.parentChainBoundary ?? null;
    }
    return identityKey;
  };
  const runnerIdentityKey = reference(runnerIdentity);
  const studioIdentityKey = reference(studioIdentity);
  const compactEvents = [];
  for (let index = 0; index < events.length && index < 24; index += 1) {
    check();
    const event = events[index];
    const identities = event.identities ?? [];
    if (!Array.isArray(identities) || identities.length > 64) throw new NativeEvidenceError('event-identity-count');
    const keys = [];
    for (let item = 0; item < identities.length && item < 64; item += 1) { check(); keys.push(reference(identities[item])); }
    compactEvents.push({ event: event.event, atUtc: event.atUtc, processId: event.processId, code: event.code, signal: event.signal, identityKeys: keys });
  }
  const compactHelpers = [];
  for (let index = 0; index < helpers.length && index < 64; index += 1) {
    check();
    const item = helpers[index];
    compactHelpers.push({ processId: item.processId, parentProcessId: item.parentProcessId, startedAtUtc: item.startedAtUtc,
      command: item.command, action: item.action, identityKey: reference(item.identity), exitCode: item.exitCode,
      exitedAtUtc: item.exitedAtUtc, stderrBytes: item.stderrBytes, timedOut: item.timedOut === true,
      ...projectNativeHelperStatistics(item), cimObservation: projectNativeCimObservation(item, { action: item.action }) });
  }
  check();
  return { schemaVersion: 2, identityKeyFormat: 'processId:creationTimeUtc', runnerIdentityKey, studioIdentityKey,
    identities: [...table.values()], events: compactEvents, helpers: compactHelpers, streamCounts, rawConsoleOrHeadersPersisted: false };
}

// Every terminal file has an independent attempt. An oversize/credential/detail
// failure cannot skip cleanup/result, and any required evidence failure makes
// the overall result fail even when the native lifecycle itself succeeded.
export async function persistNativeTerminalEvidence({ write, normalExit, cleanup, result, details = [], secrets = [],
  deadline, now = Date.now, perWriteTimeoutMs = 2000 }) {
  if (typeof write !== 'function' || !Number.isFinite(deadline) || !Array.isArray(details) || details.length > 8
    || !Number.isInteger(perWriteTimeoutMs) || perWriteTimeoutMs < 1 || perWriteTimeoutMs > 2000) throw new NativeEvidenceError('terminal-contract');
  const outcomes = [];
  const beforeResultDeadline = deadline - 3000; // 1s encoding + 2s result write.
  const attempt = async (name, produce, role, attemptDeadline = beforeResultDeadline) => {
    const outcome = { name, role, persisted: false };
    outcomes.push(outcome);
    const controller = new AbortController();
    let timer;
    try {
      if (!/^[a-z0-9.-]+\.json$/u.test(name) || now() >= attemptDeadline) throw new NativeEvidenceError('deadline');
      const value = typeof produce === 'function' ? produce() : produce;
      encodeNativeEvidence(value, secrets, { now, deadline: Math.min(now() + 1000, attemptDeadline) });
      const waitMs = Math.min(perWriteTimeoutMs, attemptDeadline - now());
      if (waitMs <= 0) throw new NativeEvidenceError('deadline');
      await Promise.race([
        Promise.resolve().then(() => write(name, value, { signal: controller.signal })),
        new Promise((_, reject) => { timer = setTimeout(() => { controller.abort(); reject(new NativeEvidenceError('writer-timeout')); }, waitMs); }),
      ]);
      if (now() >= attemptDeadline) throw new NativeEvidenceError('deadline');
      outcome.persisted = true;
    } catch (error) {
      // Never reflect a writer's raw error text: it can contain a rejected DTO.
      outcome.failure = error instanceof NativeEvidenceError && failureCodes.has(error.code) ? error.code : 'writer-failed';
    } finally { clearTimeout(timer); }
  };
  await attempt('normal-exit.json', normalExit, 'essential');
  await attempt('cleanup.json', cleanup, 'essential');
  for (let index = 0; index < details.length && index < 8; index += 1) {
    // Expired attempts record deadline failure without invoking the writer; the
    // remaining essential result attempt is still independently recorded.
    await attempt(details[index].name, details[index].value, 'detail');
  }
  const evidenceFailed = outcomes.some((item) => !item.persisted);
  const lifecycleProven = normalExit.normalExit === true && normalExit.method === 'CloseMainWindow' && normalExit.accepted === true
    && normalExit.studioExitCode === 0 && normalExit.studioExitSignal === null && normalExit.studioIdentityExited === true
    && normalExit.oldServerIdentityExited === true && normalExit.newServerIdentityExited === true && normalExit.allFourPortsReleased === true;
  const passed = result.passed === true && (result.fatal === null || result.fatal === undefined) && lifecycleProven
    && cleanup.cleanupProven === true && cleanup.allFourPortsReleased === true && Array.isArray(cleanup.errors) && cleanup.errors.length === 0
    && Array.isArray(cleanup.fallbackActions) && cleanup.fallbackActions.length === 0
    && Array.isArray(cleanup.helperReclaims) && cleanup.helperReclaims.length === 0 && !evidenceFailed;
  const finalResult = { ...result, passed, fatal: result.fatal ?? (evidenceFailed ? { stage: 'evidence', message: 'A required independent evidence write failed.' } : passed ? null : { stage: 'acceptance', message: 'Native lifecycle acceptance was not proved.' }),
    evidenceWrites: outcomes.map((item) => ({ ...item })), requiredResultWriter: true };
  await attempt('result.json', finalResult, 'essential', deadline);
  const resultPersisted = outcomes.at(-1).persisted;
  return { passed: passed && resultPersisted, result: resultPersisted ? finalResult : { ...finalResult, passed: false,
    fatal: finalResult.fatal ?? { stage: 'evidence', message: 'The independent result writer failed.' } }, outcomes };
}
