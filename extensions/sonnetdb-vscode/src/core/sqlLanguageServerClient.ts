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
  id: number;
  diagnostics: LanguageServerDiagnostic[];
  error?: string;
}

interface PendingRequest {
  resolve: (diagnostics: LanguageServerDiagnostic[]) => void;
  reject: (error: Error) => void;
  timeout: NodeJS.Timeout;
}

export class SqlLanguageServerClient {
  private process: ChildProcessWithoutNullStreams | undefined;
  private stdoutBuffer = '';
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
      process.stdin.write(`${JSON.stringify({ id, method: 'validate', text })}\n`, (error) => {
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
    process.stdout.setEncoding('utf8');
    process.stderr.setEncoding('utf8');
    process.stdout.on('data', (chunk: string) => this.consumeStdout(chunk));
    process.stderr.on('data', (chunk: string) => this.log(chunk.trimEnd()));
    process.once('error', (error) => this.handleExit(error));
    process.once('exit', (code, signal) => this.handleExit(
      new Error(`SonnetDB language server exited (code ${code ?? 'none'}, signal ${signal ?? 'none'}).`),
    ));
    return process;
  }

  private consumeStdout(chunk: string): void {
    this.stdoutBuffer += chunk;
    const lines = this.stdoutBuffer.split(/\r?\n/u);
    this.stdoutBuffer = lines.pop() ?? '';
    for (const line of lines) {
      if (!line.trim()) {
        continue;
      }
      try {
        this.handleResponse(JSON.parse(line) as unknown);
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
      pending.reject(new Error(value.error));
    } else {
      this.retryAfter = 0;
      pending.resolve(value.diagnostics);
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
    this.stdoutBuffer = '';
    this.rejectAll(error);
  }
}

function isLanguageServerResponse(value: unknown): value is LanguageServerResponse {
  if (!value || typeof value !== 'object') {
    return false;
  }
  const record = value as Record<string, unknown>;
  return Number.isInteger(record.id)
    && Array.isArray(record.diagnostics)
    && record.diagnostics.every(isDiagnostic)
    && (record.error === undefined || typeof record.error === 'string');
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
