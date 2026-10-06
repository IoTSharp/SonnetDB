import { runRealWorkbench } from './run-workbench-real.mjs';

await runRealWorkbench({
  modelName: 'FullText',
  environmentPrefix: 'SONNETDB_FULLTEXT_REAL',
  runPrefix: 'fulltext-real',
  specFile: 'fulltext-real-permission.spec.ts',
});
