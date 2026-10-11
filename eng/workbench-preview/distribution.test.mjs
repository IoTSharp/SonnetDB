import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { digest } from './package.mjs';
import { identity, names, checksums, prepare, verifyDirectory, validateManifest, validateNotes, validateBuildRun, validateP07 } from './distribution.mjs';
import { authorize, publish } from './release.mjs';
import { target, response, expectedMissing } from './github-api.mjs';

const environment = { name:'workbench-preview-p08', protection_rules:[{type:'required_reviewers',prevent_self_review:true,reviewers:[{type:'User',reviewer:{login:'human'}}]}] };
function fixture() {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(),'sonnetdb-p06-contract-'));
  const version = '4.5.0-preview.1.10', sourceCommit = 'a'.repeat(40), sourceTree = 'b'.repeat(40), n = names(version);
  const seal = {schemaVersion:1,kind:'immutable-preview-distribution-addendum',repository:'IoTSharp/SonnetDB',candidate:'candidate-10',version,sourceCommit,sourceTree,
    tag:'workbench-preview-1.10',profile:'preview-1',scopeRevision:'R1',rid:'win-x64',archiveFiles:1,build:{runId:'123',attempt:1,artifactId:'456',artifactDigest:`sha256:${'c'.repeat(64)}`},attachments:[]};
  const zip = Buffer.from('LOCAL_SYNTHETIC_ZIP; actual archive tested separately');
  const file = (p,b) => ({path:p,size:b.length,sha256:digest(b)});
  const z = file(n.zip,zip);
  const manifest = {schemaVersion:1,kind:'workbench-preview-artifact-manifest',identity:{...seal},source:{commit:sourceCommit,tree:sourceTree},
    components:{server:{productVersion:`${version}+${sourceCommit}`},web:{releaseVersion:version,profile:'preview-1'}},build:{nativeAot:'PUBLISHED_WIN_X64_NO_CLR_DIRECTORY',aotWarningCount:0,ci:{repository:seal.repository,runId:'123',attempt:'1',workflowSha:sourceCommit}},
    files:[file('SonnetDB.exe',zip)],zip:z};
  const notes = Buffer.from(`<!-- preview-identity: ${JSON.stringify({candidate:seal.candidate,version,sourceCommit,profile:'preview-1',scopeRevision:'R1',rid:'win-x64'})} -->\nNOT_READY P08 unknown 回退 反馈\n`);
  const m = Buffer.from(JSON.stringify(manifest));
  seal.attachments=[z,file(n.manifest,m),file(n.notes,notes)];
  const sums = Buffer.from(checksums(seal.attachments,n)); seal.attachments.push(file(n.sums,sums));
  const oldSums = Buffer.from(`${z.sha256}  ${n.zip}\n${digest(m)}  ${n.manifest}\n`);
  seal.p04Assets=[z,file(n.manifest,m),file(n.sums,oldSums)];
  const incoming=path.join(temp,'incoming'),delivery=path.join(temp,'delivery'),notesPath=path.join(temp,n.notes);
  fs.mkdirSync(incoming); fs.writeFileSync(path.join(incoming,n.zip),zip);fs.writeFileSync(path.join(incoming,n.manifest),m);fs.writeFileSync(path.join(incoming,n.sums),oldSums);fs.writeFileSync(notesPath,notes);
  const close=()=>{assert.ok(path.resolve(temp).startsWith(path.resolve(os.tmpdir())+path.sep+'sonnetdb-p06-contract-'));fs.rmSync(temp,{recursive:true});};
  return {temp,seal,n,zip,manifest,notes,incoming,delivery,notesPath,close};
}
function use(fn){const f=fixture();try{return fn(f);}finally{f.close();}}
async function asyncUse(fn){const f=fixture();try{return await fn(f);}finally{f.close();}}
function context(f) {const sealBytes=Buffer.from(JSON.stringify(f.seal));return {seal:f.seal,sealBytes,delivery:f.delivery,environment,context:{event:'workflow_dispatch',operation:'publish',repository:f.seal.repository,authorization:`P08:${f.seal.tag}:${digest(sealBytes)}`},record:{mutationStarted:false}};}
function remote(f,opts={}) {
  const calls=[], ctx=context(f), assets=[]; let release=null, tag=null;
  if(opts.existing) {tag={object:{type:'commit',sha:opts.wrongTag?'d'.repeat(40):f.seal.sourceCommit}};release={id:1,html_url:'fixture',draft:false,prerelease:true,tag_name:f.seal.tag,name:`SonnetDB Workbench Preview 1 — ${f.seal.candidate}`,body:`${f.notes}\n<!-- distribution-sha256: ${digest(ctx.sealBytes)} -->\n`};for(const [i,a]of f.seal.attachments.entries())assets.push({id:i+1,name:a.path,size:a.size,state:'uploaded'});if(opts.extra)assets.push({id:99,name:'old.vsix'});if(opts.missing)assets.pop();}
  const api=async(method,payload,body,accept)=>{calls.push({method,payload,body});if(opts.fail===payload&&method!=='GET')throw new Error('UNKNOWN_FIXTURE');
    if(payload.startsWith('git/ref/tags/'))return tag;
    if(payload==='git/refs'){tag={object:{type:'commit',sha:body.sha}};return tag;}
    if(payload.startsWith('releases/tags/'))return release;
    if(payload==='releases'&&method==='POST'){release={...body,id:1,html_url:'fixture'};return release;}
    if(payload.startsWith('upload/')){const name=decodeURIComponent(payload.split('name=')[1]);const a={id:assets.length+1,name,size:body.length,state:'uploaded'};assets.push(a);return a;}
    if(payload==='releases/1/assets?per_page=100')return assets;
    if(accept==='application/octet-stream'){const a=assets.find(a=>String(a.id)===payload.split('/').at(-1));return opts.corrupt?Buffer.from('BAD'):fs.readFileSync(path.join(f.delivery,a.name));}
    if(payload==='releases/1'&&method==='PATCH'){release={...release,...body};return release;}
    if(payload==='releases/1')return release;
    if(payload==='releases/latest')return {id:99};
    throw new Error(`Unexpected fixture API ${method} ${payload}`);
  }; return {api,calls,ctx};
}
test('LOCAL_SYNTHETIC_CONTRACT exact four attachments; original bytes and sum unchanged',()=>use(f=>{
  const before=fs.readFileSync(path.join(f.incoming,f.n.sums));prepare(f.incoming,f.delivery,f.notesPath,f.seal);verifyDirectory(f.delivery,f.seal);
  assert.equal(fs.readFileSync(path.join(f.delivery,f.n.zip)).equals(f.zip),true);assert.equal(fs.readFileSync(path.join(f.incoming,f.n.sums)).equals(before),true);
  assert.equal(fs.readFileSync(path.join(f.delivery,f.n.sums),'utf8').split('\n').filter(Boolean).length,3);
}));
for(const extra of ['SonnetDB.Studio.zip','old.vsix','unexpected.txt'])test(`reject extra attachment ${extra}`,()=>use(f=>{prepare(f.incoming,f.delivery,f.notesPath,f.seal);fs.writeFileSync(path.join(f.delivery,extra),'x');assert.throws(()=>verifyDirectory(f.delivery,f.seal));}));
for(const type of ['manifest','zip','notes','sums'])test(`reject missing ${type}`,()=>use(f=>{prepare(f.incoming,f.delivery,f.notesPath,f.seal);fs.unlinkSync(path.join(f.delivery,f.n[type]));assert.throws(()=>verifyDirectory(f.delivery,f.seal));}));
for(const field of ['version','sourceCommit','sourceTree','profile','scopeRevision','rid','candidate'])test(`reject mismatched manifest ${field}`,()=>use(f=>{const m=structuredClone(f.manifest);m.identity[field]='wrong';assert.throws(()=>validateManifest(m,f.seal));}));
test('reject wrong notes version',()=>use(f=>assert.throws(()=>validateNotes(Buffer.from(f.notes.toString().replaceAll(f.seal.version,'4.5.0-preview.1.9')),f.seal))));
test('reject corrupted ZIP and original checksum drift',()=>use(f=>{fs.appendFileSync(path.join(f.incoming,f.n.zip),'wrong');assert.throws(()=>prepare(f.incoming,f.delivery,f.notesPath,f.seal));assert.equal(fs.existsSync(f.delivery),false);}));
test('reject output reuse',()=>use(f=>{prepare(f.incoming,f.delivery,f.notesPath,f.seal);assert.throws(()=>prepare(f.incoming,f.delivery,f.notesPath,f.seal));}));
test('reject checksum self entry and case duplicate list',()=>use(f=>{f.seal.attachments.push({...f.seal.attachments[0],path:f.n.zip.toUpperCase()});assert.throws(()=>identity(f.seal));}));
test('latest build attempt, event, source, artifact digest and expiry are required',()=>use(f=>{
  const run={id:123,repository:{full_name:f.seal.repository},head_sha:f.seal.sourceCommit,path:'.github/workflows/workbench-preview-build.yml',run_attempt:1,event:'workflow_dispatch',status:'completed',conclusion:'success',run_started_at:'2026-10-11T01:00:00Z'};
  const artifacts={total_count:1,artifacts:[{id:456,name:`workbench-preview-${f.seal.version}`,expired:false,size_in_bytes:1,created_at:'2026-10-11T01:01:00Z',digest:f.seal.build.artifactDigest}]};
  validateBuildRun(run,artifacts,f.seal);for(const [k,v]of [['run_attempt',2],['event','push'],['head_sha','c'.repeat(40)],['status','queued'],['conclusion','failure']])assert.throws(()=>validateBuildRun({...run,[k]:v},artifacts,f.seal));
  for(const [k,v]of [['digest','bad'],['expired',true],['created_at','2026-10-10T00:00:00Z']])assert.throws(()=>validateBuildRun(run,{...artifacts,artifacts:[{...artifacts.artifacts[0],[k]:v}]},f.seal));
}));
test('P07 requires full original policy and eight raw checks; scheduled observation does not block',()=>use(f=>{
  const b=Buffer.from(JSON.stringify(f.seal));const readiness={status:'READY',source:'github',repository:f.seal.repository,commitSha:f.seal.sourceCommit,version:f.seal.version,workflows:Array.from({length:12},(_,i)=>({ready:true,runId:i+1})),observations:[{status:'NOT_READY'}]};
  const p={schemaVersion:1,milestone:'M47-P07',status:'READY',source:'github-and-installed-package',repository:f.seal.repository,candidate:f.seal.candidate,version:f.seal.version,sourceCommit:f.seal.sourceCommit,distributionSha256:digest(b),checks:[]};
  for(const id of ['C01','C02','C03','C04','C05','D01','P05-installed-R1','licenses']){const bytes=Buffer.from(JSON.stringify({status:'PASS',candidate:f.seal.candidate,sourceCommit:f.seal.sourceCommit,version:f.seal.version,check:id}));const file=`${id}.json`;fs.writeFileSync(path.join(f.temp,file),bytes);p.checks.push({id,status:'PASS',file,sha256:digest(bytes)});}
  validateP07(p,readiness,f.seal,b,f.temp);assert.throws(()=>validateP07({...p,status:'UNKNOWN'},readiness,f.seal,b,f.temp));assert.throws(()=>validateP07(p,{...readiness,status:'NOT_READY'},f.seal,b,f.temp));assert.throws(()=>validateP07({...p,checks:p.checks.slice(1)},readiness,f.seal,b,f.temp));assert.throws(()=>validateP07({...p,distributionSha256:'0'.repeat(64)},readiness,f.seal,b,f.temp));
}));
test('P08 denies default prepare, absent approval, unprotected environment and self approval',()=>use(f=>{
  const c=context(f);authorize(c.context,f.seal,c.sealBytes,environment);
  for(const override of [{operation:'prepare'},{event:'push'},{authorization:''}])assert.throws(()=>authorize({...c.context,...override},f.seal,c.sealBytes,environment));
  assert.throws(()=>authorize(c.context,f.seal,c.sealBytes,{...environment,protection_rules:[]}));assert.throws(()=>authorize(c.context,f.seal,c.sealBytes,{...environment,protection_rules:[{...environment.protection_rules[0],prevent_self_review:false}]}));
}));
test('fixture publisher makes one immutable tag, draft, four explicit uploads, byte readback, prerelease/latest=false',async()=>asyncUse(async f=>{
  prepare(f.incoming,f.delivery,f.notesPath,f.seal);const r=remote(f);await publish(r.api,r.ctx);assert.equal(r.ctx.record.status,'PUBLISHED_API_READBACK');
  assert.equal(r.calls.filter(c=>c.payload==='git/refs').length,1);const create=r.calls.find(c=>c.payload==='releases');assert.equal(create.body.draft,true);assert.equal(create.body.prerelease,true);assert.equal(create.body.make_latest,'false');assert.equal(r.calls.filter(c=>c.payload.startsWith('upload/')).length,4);assert.equal(r.calls.filter(c=>c.method==='PATCH').length,1);
}));
test('existing exact release only reconciles; zero writes',async()=>asyncUse(async f=>{prepare(f.incoming,f.delivery,f.notesPath,f.seal);const r=remote(f,{existing:true});await publish(r.api,r.ctx);assert.equal(r.ctx.record.status,'ALREADY_PRESENT_VERIFIED');assert.ok(r.calls.every(c=>c.method==='GET'));}));
for(const options of [{wrongTag:true},{extra:true},{missing:true},{corrupt:true}])test(`existing remote mismatch refuses without writes ${JSON.stringify(options)}`,async()=>asyncUse(async f=>{prepare(f.incoming,f.delivery,f.notesPath,f.seal);const r=remote(f,{existing:true,...options});await assert.rejects(publish(r.api,r.ctx));assert.ok(r.calls.every(c=>c.method==='GET'));}));
for(const phase of ['git/refs','releases','upload/1?name=sonnetdb-workbench-web-4.5.0-preview.1.10-win-x64.zip','upload/1?name=sonnetdb-workbench-web-4.5.0-preview.1.10-notes.md'])test(`unknown ${phase} is never retried`,async()=>asyncUse(async f=>{prepare(f.incoming,f.delivery,f.notesPath,f.seal);const r=remote(f,{fail:phase});await assert.rejects(publish(r.api,r.ctx));assert.equal(r.calls.filter(c=>c.payload===phase&&c.method!=='GET').length,1);assert.equal(r.ctx.record.mutationStarted,true);assert.equal(r.calls.filter(c=>c.method==='PATCH').length,0);}));
test('gh response parsing preserves binary and fails closed on non-success or truncation',()=>{
  assert.deepEqual(response(Buffer.from('HTTP/2.0 200 OK\r\nContent-Type: application/json\r\n\r\n{"id":1}')),{id:1});
  const b=Buffer.from([0,13,10,255]);assert.equal(response(Buffer.concat([Buffer.from('HTTP/2.0 200 OK\n\n'),b]),true).equals(b),true);
  assert.throws(()=>response(Buffer.from('HTTP/2.0 302 Found\r\n\r\n{}')));assert.throws(()=>response(Buffer.from('HTTP/2.0 200 OK')));
});
test('only authoritative GET 404 for original tag/release is absence; mutations and 5xx are not retried',()=>{
  const missing=Buffer.from('HTTP/2.0 404 Not Found\r\n\r\n{}');assert.equal(expectedMissing('GET','releases/tags/workbench-preview-1.10',missing),true);assert.equal(expectedMissing('POST','releases/tags/workbench-preview-1.10',missing),false);assert.equal(expectedMissing('GET','actions/runs/123',missing),false);assert.equal(expectedMissing('GET','releases/tags/workbench-preview-1.10',Buffer.from('HTTP/2.0 500 Error')),false);
});
test('upload URL contains exact release ID and encoded explicit filename',()=>{
  assert.equal(target('IoTSharp/SonnetDB','upload/123?name=notes.md'),'https://uploads.github.com/repos/IoTSharp/SonnetDB/releases/123/assets?name=notes.md');assert.throws(()=>target('other/repo','git/refs'));assert.throws(()=>target('IoTSharp/SonnetDB','upload/123?name=a&name=b'));
});
