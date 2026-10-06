import { runRealWorkbench } from './run-workbench-real.mjs';

await runRealWorkbench({
  modelName: 'Object',
  environmentPrefix: 'SONNETDB_OBJECT_REAL',
  runPrefix: 'object-real',
  specFile: 'object-real-permission.spec.ts',
});
