$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required' }
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$remoteScript = @'
set -euo pipefail
test "$(id -u)" -eq 0 || exit 77
timeout 18s python3 - <<'PY'
import datetime,json,os,pathlib,subprocess,time
owner='sonnetdb-object-prefix-20261104-01a1a619'
root='/var/data/tolnsd-storage/validation/'+owner
started=time.monotonic()
r={'observedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'pid':os.getpid(),'parentPid':os.getppid(),'ownedDirectoryExists':pathlib.Path(root).exists(),'containers':[]}
for flavor in ['candidate','baseline']:
    if time.monotonic()-started>12:break
    name=owner+'-'+flavor
    p=subprocess.run(['docker','inspect',name],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,timeout=3)
    if p.returncode:continue
    c=json.loads(p.stdout)[0]
    assert c['Config'].get('Labels',{}).get('validation.owner')==owner
    assert all(os.path.commonpath([root,m['Source']])==root for m in c.get('Mounts',[]) if m.get('Type')=='bind')
    logs=subprocess.run(['docker','logs','--tail','10',c['Id']],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,timeout=3)
    item={'id':c['Id'],'flavor':flavor,'pid':c['State']['Pid'],'running':c['State']['Running'],'startedAt':c['State']['StartedAt'],'exitCode':c['State']['ExitCode'],'oomKilled':c['State']['OOMKilled'],'log':(logs.stdout+logs.stderr)[-12000:]}
    if c['State']['Running']:
        proc=pathlib.Path('/proc')/str(c['State']['Pid'])
        item['io']=proc.joinpath('io').read_text()[:2000]
        item['command']=proc.joinpath('cmdline').read_bytes().replace(b'\x00',b' ').decode()[:2000]
        item['memory']=[line for line in proc.joinpath('status').read_text().splitlines() if line.startswith(('VmRSS:','VmSize:','Threads:'))]
    r['containers'].append(item)
p=subprocess.run(['docker','inspect','tolnsd-sonnetdb-1'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,timeout=3)
assert p.returncode==0
c=json.loads(p.stdout)[0]
r['production']={'id':c['Id'],'image':c['Config']['Image'],'startedAt':c['State']['StartedAt'],'health':c['State'].get('Health',{}).get('Status'),'restarts':c['RestartCount'],'oomKilled':c['State']['OOMKilled']}
print(json.dumps(r,ensure_ascii=True))
PY
'@
$output = & 'C:\Users\mysti\.codex\skills\usm-gateshell-ssh\scripts\invoke-ssh-bash.ps1' -Destination 'mulei-direct-target' -Port 22022 -Script $remoteScript -Option @('BatchMode=yes','ConnectTimeout=8','ServerAliveInterval=5','ServerAliveCountMax=2') -SshExecutable 'C:\Windows\System32\OpenSSH\ssh.exe' | Out-String
$code = $LASTEXITCODE
$name = 'field-monitor-' + [DateTime]::UtcNow.ToString('HHmmssfff') + '.json'
[IO.File]::WriteAllText((Join-Path $PSScriptRoot $name), $output, [Text.UTF8Encoding]::new($false))
[Console]::Write($output)
exit $code
