import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import type { Plugin } from 'vite';

export function workbenchModuleInventory(root: string): Plugin {
  return {
    name: 'sonnetdb-module-inventory',
    apply: 'build',
    enforce: 'post',
    writeBundle(options, bundle) {
      const deadline = Date.now() + 30000;
      if (!options.dir) throw new Error('Bundle output directory is required.');
      const records: { chunk: string; module: string; renderedLength: number; sourceSha256: string | null }[] = [];
      const chunks: { path: string; size: number; sha256: string }[] = [];
      for (const file of Object.values(bundle)) {
        if (file.type !== 'chunk') continue;
        const bytes = fs.readFileSync(path.join(options.dir, file.fileName));
        chunks.push({ path: file.fileName, size: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') });
        for (const [id, module] of Object.entries(file.modules)) {
          if (Date.now() > deadline) throw new Error('Module inventory time exceeded.');
          if (module.renderedLength === 0) continue;
          const physical = id.replace(/^\0/, '').split('?')[0];
          const relative = path.relative(root, physical).replaceAll('\\', '/');
          const name = relative.startsWith('../') || path.isAbsolute(relative) ? '[virtual]/' + path.basename(physical) : relative;
          if (/^node_modules\/vdirs\//.test(name)) throw new Error('Unlicensed vdirs code entered the emitted bundle.');
          records.push({ chunk: file.fileName, module: name, renderedLength: module.renderedLength,
            sourceSha256: fs.existsSync(physical) && fs.statSync(physical).isFile() ? createHash('sha256').update(fs.readFileSync(physical)).digest('hex') : null });
          if (records.length > 10000) throw new Error('Module inventory count exceeded.');
        }
      }
      if (!records.some(record => record.module === 'src/utils/overlayDirectives.mjs')) throw new Error('Owned overlay directives are absent from emitted bundle.');
      records.sort((a, b) => a.chunk.localeCompare(b.chunk) || a.module.localeCompare(b.module));
      fs.writeFileSync(path.join(options.dir, 'workbench-module-inventory.json'), JSON.stringify({ schemaVersion: 1,
        source: 'rollup-rendered-modules', excludedInstalledPackages: ['vdirs'], replacement: 'src/utils/overlayDirectives.mjs', chunks, modules: records }, null, 2) + '\n', { flag: 'wx' });
    },
  };
}
