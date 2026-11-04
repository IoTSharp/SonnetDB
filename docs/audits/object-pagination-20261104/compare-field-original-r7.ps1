$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required' }
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$bundle = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'probe.zip')))
$remoteScript = @'
set -euo pipefail
test "$(id -u)" -eq 0 || exit 77
timeout --kill-after=15s 600s python3 - <<'PY'
import contextlib,datetime,hashlib,io,json,os,pathlib,shutil,stat,subprocess,time,zipfile,base64,signal
owner='sonnetdb-object-prefix-20261104-01a1a619'
root=pathlib.Path('/var/data/tolnsd-storage/validation')/owner
source=pathlib.Path('/var/data/tolnsd-storage/sonnetdb/TOLNSD/kv/keyspaces/__object_storage')
started=time.monotonic()
r={'owner':owner,'observedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'pid':os.getpid(),'parentPid':os.getppid(),'sourceReadOnly':True,'sourceDirectory':str(source),'commands':[],'runs':{},'cleanup':{}}
root.parent.mkdir(exist_ok=True)
root.mkdir(exist_ok=False)
(root/'owner.txt').write_text(owner)
names=[]
def cancelled(signum,frame):
    raise TimeoutError('cancelled signal '+str(signum))
for sig in [signal.SIGTERM,signal.SIGINT,signal.SIGHUP]:signal.signal(sig,cancelled)
def run(argv,timeout=10,cleanup=False):
    if not cleanup and time.monotonic()-started>570:raise TimeoutError('whole task deadline')
    p=subprocess.Popen(argv,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    r['commands'].append({'pid':p.pid,'parentPid':os.getpid(),'startedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'argv':argv})
    try:out,err=p.communicate(timeout=timeout)
    except BaseException:
        if p.poll() is None:p.kill()
        out,err=p.communicate(timeout=5)
        r['failedCommandOutput']={'pid':p.pid,'stdout':out[-16000:],'stderr':err[-8000:]}
        raise
    return p.returncode,out,err
def remove_container(name):
    code,out,err=run(['docker','inspect',name],5,True)
    if code:return
    c=json.loads(out)[0]
    assert c['Config'].get('Labels',{}).get('validation.owner')==owner
    assert all(os.path.commonpath([str(root),m['Source']])==str(root) for m in c.get('Mounts',[]) if m.get('Type')=='bind')
    r['cleanup'][name]={'id':c['Id'],'exitCode':c['State']['ExitCode'],'oomKilled':c['State']['OOMKilled']}
    code,out,err=run(['docker','rm','--force',c['Id']],10,True)
    assert code==0,err
    r['cleanup'][name]['removed']=True
def copy_bounded(f,output,length,digest):
    remaining=length
    for block in range((length+1048575)//1048576):
        if time.monotonic()-started>100:raise TimeoutError('capture deadline')
        data=f.read(min(remaining,1048576))
        if not data:raise ValueError('source truncated while capturing')
        output.write(data);digest.update(data);remaining-=len(data)
    assert remaining==0
    return remaining
try:
    tiny=io.BytesIO();digest=hashlib.sha256()
    assert copy_bounded(io.BytesIO(b'x'),tiny,1,digest)==0 and tiny.getvalue()==b'x'
    (root/'cow-source').write_bytes(b'old')
    code,out,err=run(['cp','--reflink=always','--',str(root/'cow-source'),str(root/'cow-copy')],5)
    assert code==0,err
    assert (root/'cow-source').stat().st_ino!=(root/'cow-copy').stat().st_ino
    (root/'cow-copy').write_bytes(b'new')
    assert (root/'cow-source').read_bytes()==b'old'
    r['reflinkMutationIsolation']='PASS'
    code,out,err=run(['docker','inspect','tolnsd-sonnetdb-1'])
    assert code==0
    prod=json.loads(out)[0]
    image=prod['Image'];r['production']={'id':prod['Id'],'image':prod['Config']['Image'],'imageId':image,'restarts':prod['RestartCount'],'startedAt':prod['State']['StartedAt']}
    paths=[]
    for i,p in enumerate(source.iterdir()):
        if i>=20:raise ValueError('metadata root item budget')
        if p.name=='keyspace.lock':continue
        if p.is_dir():
            if p.name not in ['wal','segments','snapshots']:raise ValueError('unexpected metadata directory')
            for j,q in enumerate(p.iterdir()):
                if j>=32:raise ValueError('metadata child item budget')
                if not q.is_file() or q.is_symlink():raise ValueError('non-file metadata child')
                paths.append(q)
        elif p.is_file() and not p.is_symlink():paths.append(p)
        else:raise ValueError('unexpected metadata object')
    assert len(paths)<=64
    total=sum(p.stat().st_size for p in paths)
    assert total<=32*1024**3,'metadata logical byte budget'
    assert shutil.disk_usage(root).free>8*1024**3,'disk reserve'
    r['capture']={'fileCount':len(paths),'bytes':total,'method':'verified XFS reflink; no byte-copy fallback','fullFileSha256':'NOT_RUN for files above 64 MiB','files':[],'activeWalMayEndInPartialRecord':True}
    with contextlib.ExitStack() as stack:
        handles=[(p,stack.enter_context(p.open('rb')),p.stat()) for p in paths]
        for p,f,before in handles:
            relative=p.relative_to(source)
            dest=root/'seed-db'/'kv'/'keyspaces'/'__object_storage'/relative
            dest.parent.mkdir(parents=True,exist_ok=True)
            code,out,err=run(['cp','--reflink=always','--',str(p),str(dest)],10)
            assert code==0,err
            with dest.open('r+b') as output:output.truncate(before.st_size)
            after=p.stat()
            assert before.st_ino==after.st_ino and dest.stat().st_ino!=before.st_ino
            if p.name!='active.SDBKVWAL':assert before.st_size==after.st_size
            else:assert after.st_size>=before.st_size
            digest=hashlib.sha256(dest.read_bytes()).hexdigest() if before.st_size<=64*1024**2 else None
            r['capture']['files'].append({'path':str(relative),'bytes':before.st_size,'sha256':digest,'sourceInode':before.st_ino,'seedInode':dest.stat().st_ino})
        # Reject a checkpoint switching files during the capture.
        observed=set(str(p.relative_to(source)) for p in paths)
        current=set()
        for i,p in enumerate(source.iterdir()):
            if i>=20:raise ValueError('metadata recheck root budget')
            if p.name=='keyspace.lock':continue
            if p.is_dir():
                for j,q in enumerate(p.iterdir()):
                    if j>=32:raise ValueError('metadata recheck child budget')
                    current.add(str(q.relative_to(source)))
            else:current.add(str(p.relative_to(source)))
        assert observed==current,'checkpoint changed source file set'
    bundle=base64.b64decode('TASK_BUNDLE_BASE64')
    with zipfile.ZipFile(io.BytesIO(bundle)) as z:
        assert len(z.infolist())<=16
        for item in z.infolist():
            assert '/' not in item.filename and '\\' not in item.filename and item.file_size<32*1024**2
        z.extractall(root/'candidate')
    r['baselineComparison']='DEFERRED: previous R1 cold-open reached 240s; original production listing FAIL_TIMEOUT remains archived'
    for flavor in ['candidate']:
        db=root/(flavor+'-db')
        code,out,err=run(['cp','-a','--reflink=always','--',str(root/'seed-db'),str(db)],15)
        assert code==0,err
        (db/'object-prefix-probe-owned.txt').write_text(owner)
        name=owner+'-'+flavor;names.append(name)
        argv=['docker','run','--name',name,'--label','validation.owner='+owner,'--network','none','--read-only','--tmpfs','/tmp:rw,nosuid,size=128m','--cpus','4','--memory','2g','--pids-limit','128','--cap-drop','ALL','--env','DOTNET_PROCESSOR_COUNT=4','--env','DOTNET_GCHeapCount=4','--mount','type=bind,src='+str(root/flavor)+',dst=/probe,readonly','--mount','type=bind,src='+str(db)+',dst=/validation/'+flavor+'-db','--entrypoint','dotnet',image,'/probe/object-prefix-probe.dll','/validation/'+flavor+'-db','vehicle-recognition','flows/2026/11/04/']
        try:
            code,out,err=run(argv,360)
        except subprocess.TimeoutExpired:
            code=124
            _,out,err=run(['docker','logs','--tail','30',name],5)
        lines=[line for line in out.splitlines() if line.startswith('{')]
        r['runs'][flavor]={'exitCode':code,'result':json.loads(lines[-1]) if lines else None,'stderr':err[-8000:],'stdoutTail':out[-8000:] if not lines else None}
        remove_container(name)
    b=r['runs']['candidate']['result']
    r['sameCapturedSequence']='NOT_RUN_BASELINE_COLD_BOUND'
    r['status']='PASS' if b and b['Status']=='PASS' and r['runs']['candidate']['exitCode']==0 else 'FAIL'
except Exception as e:
    r['status']='FAIL';r['error']=type(e).__name__+': '+str(e)
finally:
    for name in names[:2]:
        try:remove_container(name)
        except Exception as e:r['cleanup'][name]={'error':type(e).__name__}
    try:
        assert root.resolve().parent==pathlib.Path('/var/data/tolnsd-storage/validation')
        assert (root/'owner.txt').read_text()==owner
        shutil.rmtree(root);r['cleanup']['ownedDirectoryRemoved']=not root.exists()
    except Exception as e:r['cleanup']['directoryError']=type(e).__name__
    r['elapsedSeconds']=time.monotonic()-started
    print(json.dumps(r,ensure_ascii=True))
PY
'@
$remoteScript = $remoteScript.Replace('TASK_BUNDLE_BASE64', $bundle)
$asciiGuard = [regex]::new('[^\x00-\x7F]', [Text.RegularExpressions.RegexOptions]::None, [TimeSpan]::FromSeconds(2))
if ($asciiGuard.IsMatch($remoteScript)) { throw 'Remote stdin must be ASCII; encode content as Base64 before sending.' }
$output = & 'C:\Users\mysti\.codex\skills\usm-gateshell-ssh\scripts\invoke-ssh-bash.ps1' -Destination 'mulei-direct-target' -Port 22022 -Script $remoteScript -Option @('BatchMode=yes','ConnectTimeout=8','ServerAliveInterval=10','ServerAliveCountMax=3') -SshExecutable 'C:\Windows\System32\OpenSSH\ssh.exe' | Out-String
$code = $LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'field-comparison-r7.json'), $output, [Text.UTF8Encoding]::new($false))
[Console]::Write($output)
exit $code
