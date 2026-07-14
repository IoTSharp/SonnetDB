import { existsSync, mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const extensionPath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const extensionTestsPath = path.join(extensionPath, 'out', 'test', 'host', 'index.js');
const code = resolveCodeLaunch();
const scratchRoot = mkdtempSync(path.join(tmpdir(), 'sonnetdb-vscode-host-'));

try {
  const codeArguments = [
    `--user-data-dir=${path.join(scratchRoot, 'user-data')}`,
    `--extensions-dir=${path.join(scratchRoot, 'extensions')}`,
    `--extensionDevelopmentPath=${extensionPath}`,
    `--extensionTestsPath=${extensionTestsPath}`,
    '--disable-extensions',
    '--disable-gpu',
    '--disable-workspace-trust',
    '--skip-release-notes',
    '--skip-welcome',
    '--new-window',
    '--wait',
    extensionPath,
  ];
  const launch = process.platform === 'linux' && commandExists('xvfb-run')
    ? { command: 'xvfb-run', args: ['-a', code.command, ...code.prefixArgs, '--no-sandbox', ...codeArguments] }
    : { command: code.command, args: [...code.prefixArgs, ...codeArguments] };
  const result = spawnSync(launch.command, launch.args, {
    cwd: extensionPath,
    env: { ...process.env, ...code.environment, ELECTRON_ENABLE_LOGGING: '1' },
    stdio: 'inherit',
    timeout: 120_000,
    windowsHide: true,
  });
  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    throw new Error(`VS Code Extension Host smoke failed with exit code ${result.status ?? 'unknown'}.`);
  }
} finally {
  rmSync(scratchRoot, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
}

function resolveCodeLaunch() {
  const configured = process.env.VSCODE_EXECUTABLE_PATH?.trim();
  if (configured) {
    if (!existsSync(configured)) throw new Error(`VSCODE_EXECUTABLE_PATH does not exist: ${configured}`);
    return { command: configured, prefixArgs: [], environment: {} };
  }

  if (process.platform === 'win32') {
    const installRoot = path.join(process.env.LOCALAPPDATA ?? '', 'Programs', 'Microsoft VS Code');
    const installed = path.join(installRoot, 'Code.exe');
    if (existsSync(installed)) {
      const cli = readdirSync(installRoot, { withFileTypes: true })
        .filter((entry) => entry.isDirectory())
        .map((entry) => path.join(installRoot, entry.name, 'resources', 'app', 'out', 'cli.js'))
        .find(existsSync);
      if (cli) {
        return {
          command: installed,
          prefixArgs: [cli],
          environment: { ELECTRON_RUN_AS_NODE: '1', VSCODE_DEV: '' },
        };
      }
    }
  }
  if (commandExists('code')) return { command: 'code', prefixArgs: [], environment: {} };
  throw new Error('VS Code executable was not found. Set VSCODE_EXECUTABLE_PATH to run the Extension Host smoke.');
}

function commandExists(command) {
  const probe = spawnSync(process.platform === 'win32' ? 'where.exe' : 'sh', process.platform === 'win32'
    ? [command]
    : ['-c', `command -v ${command}`], { stdio: 'ignore', windowsHide: true });
  return probe.status === 0;
}
