import fs from 'node:fs';
import path from 'node:path';
import { digest, requireThat } from './package.mjs';
import { identity, verifyDirectory } from './distribution.mjs';

export function authorize(context, seal, sealBytes, environment) {
  requireThat(context.event === 'workflow_dispatch' && context.operation === 'publish' && context.repository === seal.repository, 'Manual P08 dispatch required');
  requireThat(context.authorization === `P08:${seal.tag}:${digest(sealBytes)}`, 'Explicit P08 authorization missing/mismatched');
  requireThat(environment.name === 'workbench-preview-p08', 'Wrong P08 environment');
  const reviewers = environment.protection_rules?.filter(r => r.type === 'required_reviewers');
  requireThat(reviewers?.length === 1 && reviewers[0].prevent_self_review === true && reviewers[0].reviewers?.length > 0, 'P08 requires configured reviewers and prevents self approval');
}
export async function reconcile(api, release, seal, sealBytes) {
  const assets = await api('GET', `releases/${release.id}/assets?per_page=100`);
  requireThat(Array.isArray(assets) && assets.length === 4, 'Existing release incomplete/extra assets; read-only reconciliation required');
  const seen = new Set();
  for (const asset of assets) {
    const expected = seal.attachments.find(f => f.path === asset.name);
    requireThat(expected && !seen.has(asset.name) && asset.state === 'uploaded' && asset.size === expected.size, 'Remote asset name/size/state mismatch');
    seen.add(asset.name);
    const bytes = await api('GET', `releases/assets/${asset.id}`, undefined, 'application/octet-stream');
    requireThat(Buffer.isBuffer(bytes) && digest(bytes) === expected.sha256, 'Remote asset download hash mismatch');
  }
  requireThat(release.tag_name === seal.tag && release.prerelease === true && release.name === `SonnetDB Workbench Preview 1 — ${seal.candidate}`, 'Remote release identity mismatch');
  const marker = `<!-- distribution-sha256: ${digest(sealBytes)} -->`;
  requireThat(release.body?.includes(marker), 'Remote release notes/distribution mismatch');
  return assets;
}
export async function publish(api, { seal, sealBytes, delivery, context, environment, record }) {
  identity(seal); authorize(context, seal, sealBytes, environment);
  verifyDirectory(delivery, seal);
  const tag = await api('GET', `git/ref/tags/${seal.tag}`);
  const existing = await api('GET', `releases/tags/${seal.tag}`);
  if (tag) {
    let target = tag.object;
    if (target.type === 'tag') target = (await api('GET', `git/tags/${target.sha}`)).object;
    requireThat(target.type === 'commit' && target.sha === seal.sourceCommit, 'Tag source mismatch; immutable tag cannot move');
  }
  if (tag || existing) {
    requireThat(tag && existing, 'Partial existing remote state; reconcile without mutation');
    await reconcile(api, existing, seal, sealBytes);
    const notes = fs.readFileSync(path.join(delivery, `sonnetdb-workbench-web-${seal.version}-notes.md`), 'utf8');
    requireThat(existing.body === `${notes}\n<!-- distribution-sha256: ${digest(sealBytes)} -->\n`, 'Existing release body mismatch');
    requireThat(existing.draft === false, 'Existing draft retained; requires separate reconciliation and authorization');
    const latest = await api('GET', 'releases/latest');
    requireThat(latest?.id !== existing.id, 'Existing Preview unexpectedly became latest');
    record.status = 'ALREADY_PRESENT_VERIFIED'; record.releaseId = existing.id; record.url = existing.html_url;
    return;
  }
  // Each mutation is attempted once. The caller persists UNKNOWN on any error.
  record.phase = 'create-tag'; record.mutationStarted = true;
  const createdTag = await api('POST', 'git/refs', { ref: `refs/tags/${seal.tag}`, sha: seal.sourceCommit });
  requireThat(createdTag.object.type === 'commit' && createdTag.object.sha === seal.sourceCommit, 'Created tag mismatch');
  record.phase = 'create-draft';
  const notes = fs.readFileSync(path.join(delivery, `sonnetdb-workbench-web-${seal.version}-notes.md`), 'utf8');
  const release = await api('POST', 'releases', { tag_name: seal.tag, target_commitish: seal.sourceCommit,
    name: `SonnetDB Workbench Preview 1 — ${seal.candidate}`, body: `${notes}\n<!-- distribution-sha256: ${digest(sealBytes)} -->\n`,
    draft: true, prerelease: true, make_latest: 'false' });
  requireThat(Number.isSafeInteger(release.id) && release.id > 0, 'Release ID missing');
  record.releaseId = release.id; record.url = release.html_url; record.uploads = [];
  for (const f of seal.attachments) {
    record.phase = `upload:${f.path}`;
    const upload = await api('POST', `upload/${release.id}?name=${encodeURIComponent(f.path)}`, fs.readFileSync(path.join(delivery, f.path)), 'application/octet-stream');
    requireThat(upload.name === f.path && upload.state === 'uploaded' && upload.size === f.size, 'Upload response mismatch');
    record.uploads.push({ id: upload.id, name: upload.name });
  }
  record.phase = 'reconcile-draft';
  const draft = await api('GET', `releases/${release.id}`);
  await reconcile(api, draft, seal, sealBytes);
  requireThat(draft.draft === true && draft.body === `${notes}\n<!-- distribution-sha256: ${digest(sealBytes)} -->\n`, 'Draft changed');
  record.phase = 'publish-draft';
  await api('PATCH', `releases/${release.id}`, { draft: false, prerelease: true, make_latest: 'false' });
  record.phase = 'reconcile-public';
  const published = await api('GET', `releases/${release.id}`);
  await reconcile(api, published, seal, sealBytes);
  requireThat(published.draft === false && published.body === draft.body, 'Public release mismatch');
  const finalTag = await api('GET', `git/ref/tags/${seal.tag}`);
  requireThat(finalTag?.object.type === 'commit' && finalTag.object.sha === seal.sourceCommit, 'Published tag changed');
  const latest = await api('GET', 'releases/latest');
  requireThat(latest?.id !== release.id, 'Preview unexpectedly became latest');
  record.status = 'PUBLISHED_API_READBACK';
  record.boundary = 'Authenticated API byte readback only; P08 must independently verify anonymous public download and first run.';
}
