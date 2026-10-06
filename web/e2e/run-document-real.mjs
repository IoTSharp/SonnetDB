import { runRealWorkbench } from './run-workbench-real.mjs';

await runRealWorkbench({
  modelName: 'Document',
  environmentPrefix: 'SONNETDB_DOCUMENT_REAL',
  runPrefix: 'document-real',
  specFile: 'document-real-permission.spec.ts',
});
