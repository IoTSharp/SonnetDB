import { previewEnabled, previewSupported } from './policy';

/** Check the running native Server before mounting authentication or data views. */
export async function verifyPreviewIdentity(): Promise<void> {
  if (!previewEnabled) return;
  const version = import.meta.env.VITE_WORKBENCH_VERSION;
  const source = import.meta.env.VITE_WORKBENCH_SOURCE_COMMIT;
  if (!previewSupported || !version || !source) throw new Error('Preview 身份未绑定。');
  const response = await fetch('/workbench-preview/identity', {
    cache: 'no-store', credentials: 'omit', redirect: 'error', signal: AbortSignal.timeout(5000),
  });
  if (!response.ok || Number(response.headers.get('content-length') ?? 0) > 4096) throw new Error('Server 身份不可核验。');
  const reader = response.body?.getReader();
  if (!reader) throw new Error('Server 身份缺失。');
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    for (let count = 0; count < 128; count++) {
      const item = await reader.read();
      if (item.done) {
        const bytes = new Uint8Array(size);
        let offset = 0;
        for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
        const identity = JSON.parse(new TextDecoder().decode(bytes));
        const candidate = `candidate-${version.split('.').at(-1)!.padStart(2, '0')}`;
        if (identity.version !== version || identity.sourceCommit !== source || identity.candidate !== candidate
          || identity.profile !== 'preview-1' || identity.scopeRevision !== 'R1' || identity.rid !== 'win-x64') {
          throw new Error('Server/Web 身份不匹配。');
        }
        return;
      }
      size += item.value.length;
      if (size > 4096) throw new Error('Server 身份响应超限。');
      chunks.push(item.value);
    }
    throw new Error('Server 身份分块超限。');
  } finally { await reader.cancel(); reader.releaseLock(); }
}
