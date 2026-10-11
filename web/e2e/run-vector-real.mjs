import { runRealWorkbench } from './run-workbench-real.mjs';

await runRealWorkbench({
  modelName: 'Vector',
  environmentPrefix: 'SONNETDB_VECTOR_REAL',
  runPrefix: 'vector-real',
  specFile: 'vector-real-permission.spec.ts',
});
