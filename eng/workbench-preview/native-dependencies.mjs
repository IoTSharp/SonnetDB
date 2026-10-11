import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { digest, readJson, requireThat, validateNative, writeJson } from './package.mjs';

export const runtimeNames = ['msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll'];
const systemNames = new Set(('advapi32 bcrypt bcryptprimitives cabinet cfgmgr32 combase crypt32 cryptbase d3d11 d3d12 d3dcompiler_47 dbghelp dnsapi dxgi fontsub gdi32 imm32 iphlpapi kernel32 kernelbase mswsock ncrypt netapi32 normaliz ntdll ole32 oleaut32 powrprof psapi rpcrt4 secur32 setupapi shell32 shlwapi ucrtbase user32 userenv version wevtapi winhttp winmm wintrust ws2_32 wtsapi32').split(' ').map(name => `${name}.dll`));
export const isSystemImport = name => systemNames.has(name.toLowerCase()) || /^(?:api|ext)-ms-win-[a-z0-9-]+\.dll$/iu.test(name);

// Read both PE import tables directly; never resolve against the build host's PATH/System32.
export function readImports(bytes) {
  validateNative(bytes);
  const pe = bytes.readUInt32LE(0x3c), optional = pe + 24;
  const sections = bytes.readUInt16LE(pe + 6), sectionStart = optional + bytes.readUInt16LE(pe + 20);
  requireThat(sections > 0 && sections <= 96 && sectionStart + sections * 40 <= bytes.length, 'Invalid PE sections');
  const rvaOffset = rva => {
    for (let index = 0; index < sections; index++) {
      const start = sectionStart + index * 40;
      const address = bytes.readUInt32LE(start + 12), rawSize = bytes.readUInt32LE(start + 16), raw = bytes.readUInt32LE(start + 20);
      if (rva >= address && rva - address < rawSize) {
        const offset = raw + rva - address;
        requireThat(offset < bytes.length, 'Import RVA outside file'); return offset;
      }
    }
    throw new Error('Import RVA outside mapped sections');
  };
  const imports = [];
  for (const [directory, stride, nameField, kind] of [[1, 20, 12, 'ordinary'], [13, 32, 4, 'delay']]) {
    const entry = optional + 112 + directory * 8;
    requireThat(entry + 8 <= sectionStart, 'Missing import directory');
    const rva = bytes.readUInt32LE(entry), size = bytes.readUInt32LE(entry + 4);
    if (rva === 0) { requireThat(size === 0, 'Inconsistent empty import table'); continue; }
    requireThat(size >= stride && size <= 256 * stride, 'Import descriptor budget');
    const start = rvaOffset(rva); requireThat(start + size <= bytes.length, 'Truncated import table');
    let terminated = false;
    for (let index = 0; index < 256 && (index + 1) * stride <= size; index++) {
      const offset = start + index * stride;
      if (bytes.subarray(offset, offset + stride).every(value => value === 0)) { terminated = true; break; }
      if (kind === 'delay') requireThat(bytes.readUInt32LE(offset) === 1, 'Unsupported delay-import VA attributes');
      const nameOffset = rvaOffset(bytes.readUInt32LE(offset + nameField));
      const end = bytes.indexOf(0, nameOffset);
      requireThat(end > nameOffset && end - nameOffset <= 260, 'Invalid import name');
      const name = bytes.toString('ascii', nameOffset, end).toLowerCase();
      requireThat(/^[a-z0-9_.-]+\.dll$/u.test(name), 'Unsafe native import name');
      imports.push({ name, kind });
    }
    requireThat(terminated, 'Unterminated import table');
  }
  return imports;
}
export function validateClosure(binaries) {
  requireThat(binaries.length > 0 && binaries.length <= 32, 'Native file budget');
  const names = new Set(binaries.map(file => file.path.toLowerCase()));
  requireThat(names.size === binaries.length, 'Native name collision');
  return binaries.map(file => ({ ...file, imports: file.imports.map(item => {
    const resolution = names.has(item.name.toLowerCase()) ? 'APP_LOCAL' : isSystemImport(item.name) ? 'WINDOWS_11_SYSTEM_CONTRACT' : null;
    requireThat(resolution, `Unresolved native import: ${file.path} -> ${item.name}`);
    return { ...item, resolution };
  }) }));
}
function inspect(root, name) {
  const bytes = fs.readFileSync(path.join(root, name));
  requireThat(bytes.length <= 256 * 1024 * 1024, 'Native byte budget');
  return { path: name, size: bytes.length, sha256: digest(bytes), imports: readImports(bytes) };
}
function collect(publish, redist, evidence, registration) {
  const deadline = Date.now() + 60_000;
  const names = fs.readdirSync(publish).filter(name => /\.(?:dll|exe)$/iu.test(name));
  requireThat(names.length <= 28, 'Published native file budget');
  const binaries = names.map(name => inspect(publish, name)), added = [];
  for (let index = 0; index < binaries.length && index < 32; index++) {
    requireThat(Date.now() < deadline && !fs.existsSync(path.join(evidence, 'cancel')), 'Native collection deadline/cancel');
    for (const item of binaries[index].imports) {
      if (binaries.some(file => file.path.toLowerCase() === item.name) || isSystemImport(item.name)) continue;
      requireThat(runtimeNames.includes(item.name), `Unlisted external runtime: ${item.name}`);
      const source = path.join(redist, item.name), target = path.join(publish, item.name);
      requireThat(fs.existsSync(source) && !fs.existsSync(target), 'Registered runtime unavailable or ambiguous');
      const file = inspect(redist, item.name);
      fs.copyFileSync(source, target, fs.constants.COPYFILE_EXCL);
      binaries.push(file); added.push({ ...file, source });
    }
  }
  const closure = validateClosure(binaries);
  writeJson(path.join(evidence, 'native-dependencies.json'), { schemaVersion: 1, status: 'PASS', ordinaryAndDelayImports: true,
    operatingSystemPrerequisite: 'Windows 11 x64 with its built-in UCRT and system/API-set libraries',
    dynamicLoadCoverage: 'Explicit runtime loads outside PE imports are not inferred; P05 runtime journey remains pending',
    registration: readJson(registration), redistributed: added, binaries: closure });
}
function verify(root, evidence) {
  const report = readJson(evidence), actual = fs.readdirSync(root).filter(name => /\.(?:dll|exe)$/iu.test(name));
  const binaries = validateClosure(actual.map(name => inspect(root, name)));
  requireThat(binaries.length === report.binaries.length, 'Native inventory count mismatch');
  for (const file of binaries) requireThat(JSON.stringify(file) === JSON.stringify(report.binaries.find(item => item.path === file.path)), 'Native import/hash mismatch');
  process.stdout.write(`Verified ${binaries.length} native binaries and ordinary/delay import closure\n`);
}
if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const [command, ...args] = process.argv.slice(2);
  if (command === 'collect') collect(...args);
  else if (command === 'verify') verify(...args);
  else throw new Error('Unknown dependency command');
}
