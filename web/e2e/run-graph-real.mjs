import { runRealWorkbench } from './run-workbench-real.mjs';

await runRealWorkbench({
  modelName: 'Graph',
  environmentPrefix: 'SONNETDB_GRAPH_REAL',
  runPrefix: 'graph-real',
  specFile: 'graph-real-permission.spec.ts',
});
