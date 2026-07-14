import { ChildProcessWithoutNullStreams, spawn } from 'node:child_process';

export interface LanguageServerLaunch {
  command: string;
  args: string[];
}

export interface LanguageServerDiagnostic {
  severity: 'error' | 'warning' | 'information' | 'hint';
  message: string;
  offset: number;
  length: number;
}

interface LanguageServerResponse {
  jsonrpc: '2.0';
  id: number;
  result?: {
    diagnostics: LanguageServerDiagnostic[];
  };
  error?: {
    code: number;
    message: string;
  };
}

interface PendingRequest {
  resolve: (diagnostics: LanguageServerDiagnostic[]) => void;
  reject: (error: Error) => void;
  timeout: NodeJS.Timeout;
}

export class SqlLanguageServerClient {
  private process: ChildProcessWithoutNullStreams | undefined;
  private stdoutBuffer = Buffer.alloc(0);
  private nextRequestId = 1;
  private readonly pending = new Map<number, PendingRequest>();
  private disposed = false;
  private retryAfter = 0;

  public constructor(
    private readonly launch: LanguageServerLaunch,
    private readonly log: (message: string) => void = () => undefined,
  ) {}

  public validate(text: string, timeoutMilliseconds = 2_000): Promise<LanguageServerDiagnostic[]> {
    if (this.disposed) {
      return Promise.reject(new Error('SonnetDB language server client is disposed.'));
    }

    let process: ChildProcessWithoutNullStreams;
    try {
      process = this.ensureStarted();
    } catch (error) {
      return Promise.reject(error instanceof Error ? error : new Error(String(error)));
    }
    const id = this.nextRequestId++;
    return new Promise<LanguageServerDiagnostic[]>((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`SonnetDB language server timed out after ${timeoutMilliseconds} ms.`));
      }, timeoutMilliseconds);
      this.pending.set(id, { resolve, reject, timeout });
      const payload = Buffer.from(JSON.stringify({
        jsonrpc: '2.0',
        id,
        method: 'sonnetdb/validate',
        params: { text },
      }), 'utf8');
      const header = Buffer.from(`Content-Length: ${payload.byteLength}\r\n\r\n`, 'ascii');
      process.stdin.write(Buffer.concat([header, payload]), (error) => {
        if (error) {
          this.rejectRequest(id, error);
        }
      });
    });
  }

  public dispose(): void {
    this.disposed = true;
    this.rejectAll(new Error('SonnetDB language server stopped.'));
    this.process?.kill();
    this.process = undefined;
  }

  private ensureStarted(): ChildProcessWithoutNullStreams {
    if (this.process) {
      return this.process;
    }
    if (Date.now() < this.retryAfter) {
      throw new Error('SonnetDB language server is temporarily unavailable.');
    }

    this.log(`Starting: ${this.launch.command} ${this.launch.args.join(' ')}`);
    const process = spawn(this.launch.command, this.launch.args, {
      stdio: 'pipe',
      windowsHide: true,
    });
    this.process = process;
    process.stderr.setEncoding('utf8');
    process.stdout.on('data', (chunk: Buffer) => this.consumeStdout(chunk));
    process.stderr.on('data', (chunk: string) => this.log(chunk.trimEnd()));
    process.once('error', (error) => this.handleExit(error));
    process.once('exit', (code, signal) => this.handleExit(
      new Error(`SonnetDB language server exited (code ${code ?? 'none'}, signal ${signal ?? 'none'}).`),
    ));
    return process;
  }

  private consumeStdout(chunk: Buffer): void {
    this.stdoutBuffer = Buffer.concat([this.stdoutBuffer, chunk]);
    const delimiter = Buffer.from('\r\n\r\n', 'ascii');
    while (true) {
      const headerEnd = this.stdoutBuffer.indexOf(delimiter);
      if (headerEnd < 0) {
        return;
      }
      const header = this.stdoutBuffer.subarray(0, headerEnd).toString('ascii');
      const match = header.match(/(?:^|\r\n)Content-Length:\s*(\d+)\s*(?:\r\n|$)/iu);
      if (!match) {
        this.log('Invalid language server response: missing Content-Length header');
        this.stdoutBuffer = this.stdoutBuffer.subarray(headerEnd + delimiter.length);
        continue;
      }
      const contentLength = Number.parseInt(match[1], 10);
      const payloadStart = headerEnd + delimiter.length;
      const payloadEnd = payloadStart + contentLength;
      if (this.stdoutBuffer.length < payloadEnd) {
        return;
      }
      const payload = this.stdoutBuffer.subarray(payloadStart, payloadEnd).toString('utf8');
      this.stdoutBuffer = this.stdoutBuffer.subarray(payloadEnd);
      try {
        this.handleResponse(JSON.parse(payload) as unknown);
      } catch (error) {
        this.log(`Invalid language server response: ${errorMessage(error)}`);
      }
    }
  }

  private handleResponse(value: unknown): void {
    if (!isLanguageServerResponse(value)) {
      throw new Error('response does not match the SonnetDB language server protocol');
    }
    const pending = this.pending.get(value.id);
    if (!pending) {
      return;
    }
    clearTimeout(pending.timeout);
    this.pending.delete(value.id);
    if (value.error) {
      pending.reject(new Error(value.error.message));
    } else {
      this.retryAfter = 0;
      pending.resolve(value.result?.diagnostics ?? []);
    }
  }

  private rejectRequest(id: number, error: Error): void {
    const pending = this.pending.get(id);
    if (!pending) {
      return;
    }
    clearTimeout(pending.timeout);
    this.pending.delete(id);
    pending.reject(error);
  }

  private rejectAll(error: Error): void {
    for (const [id] of this.pending) {
      this.rejectRequest(id, error);
    }
  }

  private handleExit(error: Error): void {
    if (this.process) {
      this.log(error.message);
    }
    this.process = undefined;
    this.retryAfter = Date.now() + 30_000;
    this.stdoutBuffer = Buffer.alloc(0);
    this.rejectAll(error);
  }
}

function isLanguageServerResponse(value: unknown): value is LanguageServerResponse {
  if (!value || typeof value !== 'object') {
    return false;
  }
  const record = value as Record<string, unknown>;
  if (record.jsonrpc !== '2.0' || !Number.isInteger(record.id)) {
    return false;
  }
  if (record.error !== undefined) {
    return isLanguageServerError(record.error);
  }
  if (!record.result || typeof record.result !== 'object') {
    return false;
  }
  const result = record.result as Record<string, unknown>;
  return Array.isArray(result.diagnostics) && result.diagnostics.every(isDiagnostic);
}

function isLanguageServerError(value: unknown): boolean {
  if (!value || typeof value !== 'object') {
    return false;
  }
  const record = value as Record<string, unknown>;
  return Number.isInteger(record.code) && typeof record.message === 'string';
}

function isDiagnostic(value: unknown): value is LanguageServerDiagnostic {
  if (!value || typeof value !== 'object') {
    return false;
  }
  const record = value as Record<string, unknown>;
  return typeof record.severity === 'string'
    && typeof record.message === 'string'
    && Number.isInteger(record.offset)
    && Number.isInteger(record.length);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
