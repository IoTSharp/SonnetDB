import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { digest, readJson, requireThat } from './package.mjs';
import { identity, names, verifyDirectory } from './distribution.mjs';

// Public verification receives only anonymous responses. It cannot authorize or mutate a release.
export function publicMetadata(seal, sealBytes, notes, release, assets, tag, latest, page) {
  identity(seal);
  requireThat(Number.isSafeInteger(release?.id) && release.id > 0 && release.draft === false && release.prerelease === true,
    'Public prerelease required');
  requireThat(release.tag_name === seal.tag && release.name === `SonnetDB Workbench Preview 1 — ${seal.candidate}`
    && release.body === `${notes}\n<!-- distribution-sha256: ${digest(sealBytes)} -->\n`, 'Public identity/notes mismatch');
  requireThat(Number.isFinite(Date.parse(release.published_at)), 'Public publication date missing');
  const releaseUrl = `https://github.com/${seal.repository}/releases/tag/${seal.tag}`;
  requireThat(release.html_url === releaseUrl, 'Public page URL mismatch');
  requireThat(tag?.ref === `refs/tags/${seal.tag}` && tag.object?.type === 'commit' && tag.object.sha === seal.sourceCommit,
    'Public immutable tag source mismatch');
  // This channel deliberately creates a lightweight tag. Annotated/changed tags require review.
  requireThat(latest === null || (Number.isSafeInteger(latest.id) && latest.id > 0 && latest.id !== release.id),
    'Preview unexpectedly became latest or latest response is invalid');
  requireThat(Array.isArray(assets) && assets.length === 4 && Array.isArray(release.assets) && release.assets.length === 4,
    'Public attachment set must contain exactly four files');
  const seen = new Set();
  for (const asset of assets) {
    const expected = seal.attachments.find(f => f.path === asset.name);
    requireThat(expected && !seen.has(asset.name) && Number.isSafeInteger(asset.id) && asset.id > 0
      && asset.state === 'uploaded' && asset.size === expected.size, 'Public attachment identity/size/state mismatch');
    const embedded = release.assets.filter(a => a.id === asset.id && a.name === asset.name);
    requireThat(embedded.length === 1, 'Public release/asset list disagree');
    seen.add(asset.name);
    requireThat(asset.browser_download_url === `https://github.com/${seal.repository}/releases/download/${seal.tag}/${expected.path}`,
      'Unexpected public download URL');
    if (asset.digest !== undefined && asset.digest !== null) requireThat(asset.digest === `sha256:${expected.sha256}`, 'Public asset digest mismatch');
  }
  requireThat(typeof page === 'string' && page.length < 4 * 1024 * 1024 && /<title>Release\s/iu.test(page)
    && page.includes(seal.repository) && page.includes(seal.tag) && page.includes(seal.candidate), 'Anonymous release page unavailable/mismatched');
  return { releaseId: release.id, url: releaseUrl, publishedAt: release.published_at,
    attachments: assets.map(a => ({ id: a.id, path: a.name, url: a.browser_download_url })) };
}

export function verifyPublicDownload(seal, sealBytes, directory, delivery) {
  const read = name => readJson(path.join(directory, name));
  const notes = fs.readFileSync(path.join(delivery, names(seal.version).notes), 'utf8');
  const result = publicMetadata(seal, sealBytes, notes, read('release.json'), read('assets.json'), read('tag.json'), read('latest.json'),
    fs.readFileSync(path.join(directory, 'public-page.html'), 'utf8'));
  const manifest = verifyDirectory(delivery, seal);
  return { ...result, status: 'PUBLIC_DOWNLOAD_BYTES_VERIFIED', source: 'anonymous-public-http',
    candidate: seal.candidate, version: seal.version, sourceCommit: seal.sourceCommit, distributionSha256: digest(sealBytes),
    archiveFiles: manifest.files.length, attachments: seal.attachments,
    firstRun: 'NOT_RUN', boundary: 'Anonymous public page and four downloaded files verified; archive and actual first run are separate checks.' };
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  if (process.argv[2] === 'check-seal') {
    const seal = readJson(process.argv[3]); identity(seal);
    const n = names(seal.version), limits = { [n.zip]: 256 * 1024 * 1024, [n.manifest]: 4 * 1024 * 1024, [n.notes]: 1024 * 1024, [n.sums]: 4096 };
    requireThat(seal.attachments.every(f => f.size > 0 && f.size <= limits[f.path]), 'Public asset byte budget');
    console.log(`PASS immutable public download identity: ${seal.candidate}`);
    process.exit(0);
  }
  const [sealFile, directory, delivery, resultFile] = process.argv.slice(2);
  const bytes = fs.readFileSync(sealFile);
  const result = verifyPublicDownload(JSON.parse(bytes), bytes, directory, delivery);
  fs.writeFileSync(resultFile, `${JSON.stringify(result, null, 2)}\n`, { flag: 'wx' });
  console.log(JSON.stringify({ status: result.status, releaseId: result.releaseId, candidate: result.candidate, firstRun: result.firstRun }));
}
