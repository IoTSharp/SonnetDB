// WB78: field categories are diagnostics only; the strict disk projector keeps
// its original authority. Production callers supply the already bounded JSON.
import { projectDatabaseSnapshot } from './studio-native-database-scenario.mjs';

const storedLibraryFields = ['profiles', 'activeProfileId', 'activeDatabase'];
const derivedLibraryFields = ['activeIdentity'];
const storedProfileFields = ['id', 'name', 'kind', 'baseUrl', 'defaultDatabase', 'tokenMode', 'createdAt', 'updatedAt'];
const derivedProfileFields = ['identity'];
const unknownFields = (state = 'unknown') => ({ state, storedFields: [], derivedFields: [], unknownFieldCount: null });

export function observeLibraryFieldCategories(value, { now = () => performance.now(), signal } = {}) {
  const result = { schema: 'sonnetdb.wb78.library-field-categories.v1', state: 'unknown',
    library: unknownFields(), profileCount: null, profile: unknownFields() };
  try {
    const started = now();
    let previous = started;
    let checks = 0;
    const guard = () => {
      const current = now();
      if (++checks > 80 || signal?.aborted || !Number.isFinite(started) || !Number.isFinite(current)
        || current < previous || current - started >= 100) throw new Error('Observation budget.');
      previous = current;
    };
    const plain = (input) => {
      guard();
      return input !== null && typeof input === 'object' && !Array.isArray(input)
        && [Object.prototype, null].includes(Object.getPrototypeOf(input));
    };
    const fields = (input, stored, derived) => {
      if (!plain(input)) return unknownFields('invalid-object');
      const keys = Reflect.ownKeys(input);
      guard();
      if (keys.length > 32) return unknownFields('field-cap');
      const observed = { state: 'observed', storedFields: [], derivedFields: [], unknownFieldCount: 0 };
      for (const key of keys) {
        guard();
        // Emit only literals from our allowlists, never the untrusted key.
        const storedIndex = stored.indexOf(key);
        const derivedIndex = derived.indexOf(key);
        if (storedIndex >= 0) observed.storedFields.push(stored[storedIndex]);
        else if (derivedIndex >= 0) observed.derivedFields.push(derived[derivedIndex]);
        else observed.unknownFieldCount += 1;
      }
      return observed;
    };
    guard();
    result.library = fields(value, storedLibraryFields, derivedLibraryFields);
    if (result.library.state !== 'observed') return result;
    const profiles = Object.getOwnPropertyDescriptor(value, 'profiles');
    guard();
    if (!profiles || !Object.hasOwn(profiles, 'value') || !Array.isArray(profiles.value)) {
      result.profile = unknownFields('invalid-profiles'); result.state = 'partial'; return result;
    }
    const count = Object.getOwnPropertyDescriptor(profiles.value, 'length')?.value;
    if (!Number.isSafeInteger(count) || count < 0 || count > 32) {
      result.profile = unknownFields('profile-cap'); result.state = 'partial'; return result;
    }
    result.profileCount = count;
    if (count !== 1) { result.profile = unknownFields('profile-count'); result.state = 'partial'; return result; }
    const profile = Object.getOwnPropertyDescriptor(profiles.value, '0');
    guard();
    if (!profile || !Object.hasOwn(profile, 'value')) {
      result.profile = unknownFields('invalid-profile'); result.state = 'partial'; return result;
    }
    result.profile = fields(profile.value, storedProfileFields, derivedProfileFields);
    guard();
    result.state = result.profile.state === 'observed' ? 'observed' : 'partial';
  } catch {
    // Retain any safely completed category; never persist an exception message.
    result.state = 'unknown';
  }
  return result;
}

export function projectObservedDatabaseDiskSnapshot(value, record, options) {
  try { record(observeLibraryFieldCategories(value, options)); } catch { /* Observation cannot replace validation. */ }
  return projectDatabaseSnapshot(value, { disk: true });
}
