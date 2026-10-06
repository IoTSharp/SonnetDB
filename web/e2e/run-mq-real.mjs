import { runRealWorkbench } from './run-workbench-real.mjs';

await runRealWorkbench({
  modelName: 'MQ',
  environmentPrefix: 'SONNETDB_MQ_REAL',
  runPrefix: 'mq-real',
  specFile: 'mq-real-permission.spec.ts',
});
