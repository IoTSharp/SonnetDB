// WB-41 is an explicit scenario of the existing actual Studio runner. The
// parent operates each OS picker with computer-use and acknowledges its phase;
// this entry does not replace the native host, bridge, DOM or lifecycle gates.
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const evidenceParent = path.join(repository, 'artifacts', 'wb41-validation-20261007');
const configured = process.env.SONNETDB_STUDIO_NATIVE_REAL_EVIDENCE_ROOT;
if (configured && (!path.isAbsolute(configured) || path.resolve(configured).toLowerCase() !== evidenceParent.toLowerCase())) {
  throw new Error('WB-41 SQL dialogs require their named WB-41 evidence parent.');
}
if (process.env.SONNETDB_STUDIO_NATIVE_REAL_SCENARIO && process.env.SONNETDB_STUDIO_NATIVE_REAL_SCENARIO !== 'sql-dialogs') {
  throw new Error('WB-41 SQL dialogs cannot override another native validation scenario.');
}
process.env.SONNETDB_STUDIO_NATIVE_REAL_EVIDENCE_ROOT = evidenceParent;
process.env.SONNETDB_STUDIO_NATIVE_REAL_SCENARIO = 'sql-dialogs';
await import('./run-studio-native-real.mjs');
