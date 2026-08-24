import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { downloadAndUnzipVSCode } from '@vscode/test-electron';

const extensionPath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const extensionTestsPath = path.join(extensionPath, 'out', 'test', 'host', 'index.js');
const code = await resolveCodeLaunch();
const scratchRoot = mkdtempSync(path.join(tmpdir(), 'sonnetdb-vscode-host-'));

try {
  const codeArguments = [
    `--user-data-dir=${path.join(scratchRoot, 'user-data')}`,
    `--extensions-dir=${path.join(scratchRoot, 'extensions')}`,
    `--extensionDevelopmentPath=${extensionPath}`,
    `--extensionTestsPath=${extensionTestsPath}`,
    '--disable-extensions',
    '--disable-gpu',
    '--disable-updates',
    '--disable-workspace-trust',
    '--no-cached-data',
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

async function resolveCodeLaunch() {
  const configured = process.env.VSCODE_EXECUTABLE_PATH?.trim();
  if (configured) {
    if (!existsSync(configured)) throw new Error(`VSCODE_EXECUTABLE_PATH does not exist: ${configured}`);
    return { command: configured, prefixArgs: [], environment: {} };
  }

  const version = process.env.VSCODE_TEST_VERSION?.trim() || '1.100.3';
  const command = await downloadAndUnzipVSCode({
    version,
    cachePath: path.join(tmpdir(), 'sonnetdb-vscode-test-cache'),
    timeout: 120_000,
  });
  return { command, prefixArgs: [], environment: {} };
}

function commandExists(command) {
  const probe = spawnSync(process.platform === 'win32' ? 'where.exe' : 'sh', process.platform === 'win32'
    ? [command]
    : ['-c', `command -v ${command}`], { stdio: 'ignore', windowsHide: true });
  return probe.status === 0;
}
