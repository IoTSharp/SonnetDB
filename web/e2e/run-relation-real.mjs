import { runRealWorkbench } from './run-workbench-real.mjs';

await runRealWorkbench({
  modelName: 'Relation',
  environmentPrefix: 'SONNETDB_RELATION_REAL',
  runPrefix: 'relation-real',
  specFile: 'relational-real-permission.spec.ts',
});
