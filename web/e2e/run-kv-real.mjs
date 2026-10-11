import { runRealWorkbench } from './run-workbench-real.mjs';

await runRealWorkbench({
  modelName: 'KV',
  environmentPrefix: 'SONNETDB_KV_REAL',
  runPrefix: 'kv-real',
  specFile: 'kv-real-permission.spec.ts',
});
