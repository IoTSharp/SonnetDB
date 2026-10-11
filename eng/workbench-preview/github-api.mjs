import { requireThat } from './package.mjs';

export function binaryBodyHeaders(body) {
  requireThat(Buffer.isBuffer(body) && body.length > 0, 'Nonempty binary upload required');
  return ['Content-Type: application/octet-stream', `Content-Length: ${body.length}`];
}

export function target(repository, endpoint) {
  requireThat(repository === 'IoTSharp/SonnetDB' && typeof endpoint === 'string' && !/[\r\n]/u.test(endpoint), 'Unsafe GitHub target');
  if (endpoint.startsWith('upload/')) {
    const match = endpoint.match(/^upload\/([1-9][0-9]*)\?name=([^&]+)$/u);
    requireThat(match, 'Invalid upload endpoint');
    return `https://uploads.github.com/repos/${repository}/releases/${match[1]}/assets?name=${match[2]}`;
  }
  return `repos/${repository}/${endpoint}`;
}
export function response(raw, binary = false) {
  // gh --include writes the final response headers. Reject ambiguous/truncated output.
  const cr = raw.indexOf('\r\n\r\n'), lf = raw.indexOf('\n\n');
  const end = cr >= 0 ? cr + 4 : lf >= 0 ? lf + 2 : -1;
  requireThat(end >= 0 && /^HTTP\/\S+ 2\d\d/u.test(raw.toString('utf8',0,100)), 'Unexpected API response');
  const b = raw.subarray(end);
  return binary ? b : JSON.parse(b.toString('utf8'));
}
export function expectedMissing(method, endpoint, stdout) {
  const status = stdout?.toString('utf8',0,150).match(/^HTTP\/\S+ (\d{3})/u)?.[1];
  return method === 'GET' && status === '404' && /^(?:git\/ref\/tags\/|releases\/tags\/|releases\/latest$)/u.test(endpoint);
}
