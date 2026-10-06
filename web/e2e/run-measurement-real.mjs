import { runRealWorkbench } from './run-workbench-real.mjs';

await runRealWorkbench({
  modelName: 'Measurement',
  environmentPrefix: 'SONNETDB_MEASUREMENT_REAL',
  runPrefix: 'measurement-real',
  specFile: 'measurement-real-permission.spec.ts',
});
