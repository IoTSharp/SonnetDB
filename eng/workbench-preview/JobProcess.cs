using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// Task-local containment. Only a suspended root is assigned. Descendants inherit
// kernel membership; no PID ancestry search and no taskkill/Stop-Process exist.
/// <summary>在不可脱离的 Windows Job 中启动挂起根进程并记录成员身份。</summary>
public sealed class JobProcess : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct SA { public int length; public IntPtr descriptor; public int inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct SI {
        public int cb; public string reserved, desktop, title;
        public int x,y,cx,cy,xc,yc,fill,flags; public short show,cbReserved;
        public IntPtr reserved2,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)] struct SIX { public SI si; public IntPtr attributes; }
    [StructLayout(LayoutKind.Sequential)] struct PI { public IntPtr process,thread; public uint pid,tid; }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit {
        public long processTime,jobTime; public uint flags; public UIntPtr min,max;
        public uint active; public UIntPtr affinity; public uint priority,scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong a,b,c,d,e,f; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit {
        public BasicLimit basic; public IoCounters io; public UIntPtr processMemory,jobMemory,peakProcess,peakJob;
    }
    [StructLayout(LayoutKind.Sequential)] struct PBI { public IntPtr reserved1,peb,reserved2,reserved3,unique,parent; }
    [StructLayout(LayoutKind.Sequential)] struct UnicodeString { public ushort length,max; public IntPtr buffer; }
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateJobObjectW(IntPtr a, IntPtr name);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int cls,ref ExtendedLimit data,int length);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job,int cls,IntPtr data,int length,out int returned);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool member);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateJobObject(IntPtr job,uint code);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetProcessTimes(IntPtr process,out long created,out long exited,out long kernel,out long user);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetConsoleCtrlHandler(IntPtr handler,bool add);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GenerateConsoleCtrlEvent(uint type,uint group);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint GetConsoleProcessList([Out] uint[] processes,uint count);
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateFileW(string path,uint access,uint share,ref SA attributes,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcessW(string app,StringBuilder command,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string cwd,ref SIX startup,out PI info);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process,int cls,IntPtr data,int size,out int returned);
    static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    /// <summary>保留句柄对应的进程身份，禁止以历史父 PID 推导终止权限。</summary>
    public sealed class Identity {
        /// <summary>进程及其直接父进程的标识。</summary>
        public uint pid, parentPid;
        /// <summary>创建时间和完整命令行。</summary>
        public string createdUtc, commandLine;
        /// <summary>是否已核实为当前内核 Job 成员。</summary>
        public bool kernelJobMember;
        /// <summary>用于防止 PID 重用的保留句柄。</summary>
        public IntPtr handle;
    }
    IntPtr job,root,thread;
    readonly bool privateConsole;
    readonly Dictionary<uint,Identity> identities = new();
    readonly Dictionary<uint,string> deferred = new();
    /// <summary>进程退出期间暂不可读取身份的观察次数。</summary>
    public int DeferredObservationCount { get; private set; }
    /// <summary>身份不可读的保留诊断，不提供终止权限。</summary>
    public string[] DeferredObservations {
        get { var items=new List<string>(); foreach(var item in deferred) items.Add(item.Key+": "+item.Value); return items.ToArray(); }
    }
    /// <summary>根进程标识。</summary>
    public uint RootPid { get; private set; }
    /// <summary>根进程句柄是否已发出退出信号。</summary>
    public bool RootExited => root!=IntPtr.Zero && WaitForSingleObject(root,0)==0;
    /// <summary>根进程退出码。</summary>
    public uint RootExitCode { get { Check(GetExitCodeProcess(root,out uint code)); return code; } }
    /// <summary>按 Windows 参数规则引用单个命令参数。</summary>
    public static string Quote(string value) {
        var result=new StringBuilder("\""); int slashes=0;
        foreach(char c in value) {
            if(c=='\\') { slashes++; continue; }
            if(c=='\"') result.Append('\\',slashes*2+1).Append(c);
            else result.Append('\\',slashes).Append(c);
            slashes=0;
        }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    /// <summary>创建挂起根，先加入当前 Job，调用 Start 后才允许执行。</summary>
    public JobProcess(string exe,string[] args,string cwd,string stdout,string stderr,bool privateConsole=false) {
        this.privateConsole=privateConsole;
        IntPtr attributes=IntPtr.Zero,handleArray=IntPtr.Zero;
        var files=new List<IntPtr>(); bool initialized=false,assigned=false;
        try {
            job=CreateJobObjectW(IntPtr.Zero,IntPtr.Zero); Check(job!=IntPtr.Zero);
            var limits=new ExtendedLimit(); limits.basic.flags=0x2000; // KILL_ON_JOB_CLOSE; no breakaway.
            Check(SetInformationJobObject(job,9,ref limits,Marshal.SizeOf<ExtendedLimit>()));
            var sa=new SA { length=Marshal.SizeOf<SA>(),inherit=1 };
            foreach(string path in new[]{stdout,stderr,"NUL"}) {
                var h=CreateFileW(path,path=="NUL"?0x80000000u:0x40000000u,3,ref sa,path=="NUL"?3u:1u,0,IntPtr.Zero);
                Check(h!=new IntPtr(-1)); files.Add(h);
            }
            IntPtr size=IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);
            attributes=Marshal.AllocHGlobal(size);
            Check(InitializeProcThreadAttributeList(attributes,1,0,ref size)); initialized=true;
            handleArray=Marshal.AllocHGlobal(IntPtr.Size*3);
            for(int i=0;i<3;i++) Marshal.WriteIntPtr(handleArray,i*IntPtr.Size,files[i]);
            Check(UpdateProcThreadAttribute(attributes,0,new IntPtr(0x20002),handleArray,new IntPtr(IntPtr.Size*3),IntPtr.Zero,IntPtr.Zero));
            var startup=new SIX { si=new SI { cb=Marshal.SizeOf<SIX>(),flags=privateConsole?0x101:0x100,show=0,input=files[2],output=files[0],error=files[1] }, attributes=attributes };
            var command=new StringBuilder(Quote(exe)); foreach(string arg in args) command.Append(' ').Append(Quote(arg));
            // Root cannot execute or spawn before assignment succeeds.
            // DETACHED_PROCESS avoids an unnecessary console host and its
            // mutable startup command. stdout/stderr use explicit file handles.
            // pwsh can exit without evaluating -File when detached from the
            // console subsystem. CREATE_NO_WINDOW retains console semantics
            // without displaying a window; containment remains identical.
            uint flags=privateConsole ? 0x00080014u : exe.EndsWith("pwsh.exe",StringComparison.OrdinalIgnoreCase) ? 0x08080004u : 0x0008000Cu;
            Check(CreateProcessW(exe,command,IntPtr.Zero,IntPtr.Zero,true,flags,IntPtr.Zero,cwd,ref startup,out PI info));
            root=info.process; thread=info.thread; RootPid=info.pid;
            Check(AssignProcessToJobObject(job,root)); assigned=true;
            Refresh();
            if(!identities.ContainsKey(RootPid)) throw new InvalidOperationException("Suspended root identity missing.");
        } catch {
            if(root!=IntPtr.Zero && !assigned) TerminateProcess(root,125); // retained handle to our never-started root only.
            Dispose(); throw;
        } finally {
            if(initialized) DeleteProcThreadAttributeList(attributes);
            if(attributes!=IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if(handleArray!=IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            foreach(var file in files) CloseHandle(file);
        }
    }
    /// <summary>只向本 Job 创建的独占隐藏控制台发送 Ctrl+C，拒绝附加共享控制台的调用方。</summary>
    public void InterruptRoot() {
        if(!privateConsole || RootExited) throw new InvalidOperationException("No live private console root.");
        Check(IsProcessInJob(root,job,out bool member));
        if(!member) throw new InvalidOperationException("Root no longer belongs to this Job.");
        // PowerShell may allocate a console even with CREATE_NO_WINDOW. Only
        // detach when it contains this task controller alone; shared consoles
        // and oversized/unknown membership fail closed.
        var consoleMembers=new uint[256];
        uint count=GetConsoleProcessList(consoleMembers,256);
        if(count>0) {
            if(count!=1 || consoleMembers[0]!=GetCurrentProcessId()) throw new InvalidOperationException("Controller console is shared.");
            Check(FreeConsole());
        }
        Check(AttachConsole(RootPid));
        try { Check(SetConsoleCtrlHandler(IntPtr.Zero,true)); Check(GenerateConsoleCtrlEvent(0,0)); }
        finally { Check(FreeConsole()); }
    }
    /// <summary>恢复已经入 Job 的根线程。</summary>
    public void Start() { Check(ResumeThread(thread)!=uint.MaxValue); CloseHandle(thread); thread=IntPtr.Zero; }
    /// <summary>读取最多256个当前内核成员。</summary>
    public uint[] Members() {
        const int capacity=256; int bytes=8+capacity*IntPtr.Size;
        var data=Marshal.AllocHGlobal(bytes);
        try {
            Check(QueryInformationJobObject(job,3,data,bytes,out _));
            int count=Marshal.ReadInt32(data,4);
            if(count<0 || count>capacity || Marshal.ReadInt32(data)>capacity) throw new InvalidOperationException("Job active process cap exceeded.");
            var ids=new uint[count]; for(int i=0;i<count;i++) ids[i]=checked((uint)Marshal.ReadIntPtr(data,8+i*IntPtr.Size).ToInt64());
            return ids;
        } finally { Marshal.FreeHGlobal(data); }
    }
    static Identity ReadIdentity(uint pid,IntPtr handle) {
        Check(GetProcessTimes(handle,out long created,out _,out _,out _));
        var basic=Marshal.AllocHGlobal(Marshal.SizeOf<PBI>()); var text=Marshal.AllocHGlobal(131072);
        try {
            if(NtQueryInformationProcess(handle,0,basic,Marshal.SizeOf<PBI>(),out _)!=0) throw new InvalidOperationException("Parent identity unavailable.");
            var pbi=Marshal.PtrToStructure<PBI>(basic);
            int commandStatus=NtQueryInformationProcess(handle,60,text,131072,out _);
            if(commandStatus!=0) throw new InvalidOperationException("Command identity unavailable: NTSTATUS "+commandStatus.ToString("X8"));
            var value=Marshal.PtrToStructure<UnicodeString>(text);
            var command=Marshal.PtrToStringUni(value.buffer,value.length/2);
            if(string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("Empty command identity.");
            return new Identity { pid=pid,parentPid=checked((uint)pbi.parent.ToInt64()),createdUtc=DateTime.FromFileTimeUtc(created).ToString("O"),commandLine=command,kernelJobMember=true,handle=handle };
        } finally { Marshal.FreeHGlobal(basic); Marshal.FreeHGlobal(text); }
    }
    /// <summary>按保留句柄更新成员身份，退出中的成员延期观察。</summary>
    public Identity[] Refresh() {
        foreach(uint pid in Members()) {
            if(identities.ContainsKey(pid)) continue;
            if(identities.Count>=256) throw new InvalidOperationException("Job identity cap exceeded.");
            IntPtr handle=OpenProcess(0x101000,false,pid); // limited query + synchronize; retained against PID reuse.
            if(handle==IntPtr.Zero) continue; // exited between job enumeration and open.
            bool retained=false;
            try {
                Check(IsProcessInJob(handle,job,out bool member));
                if(!member) continue; // PID reused outside this job: never adopt or signal.
                if(WaitForSingleObject(handle,0)==0) continue;
                try { identities.Add(pid,ReadIdentity(pid,handle)); retained=true; }
                catch (InvalidOperationException error) {
                    // Process teardown can invalidate its PEB before the handle
                    // is signalled. Observation does not grant stop authority.
                    // Defer and re-read live members; final cleanup separately
                    // requires identities for all currently live members.
                    if(deferred.Count>=256) throw new InvalidOperationException("Deferred identity cap exceeded.");
                    deferred[pid]=error.Message; DeferredObservationCount++;
                }
            } finally { if(!retained) CloseHandle(handle); }
        }
        return new List<Identity>(identities.Values).ToArray();
    }
    /// <summary>重新核实当前成员身份后仅终止本 Job，不按进程名或父 PID 扩张。</summary>
    public Identity[] TerminateOwned() {
        Refresh(); var live=new List<Identity>();
        var teardownWatch=System.Diagnostics.Stopwatch.StartNew();
        foreach(uint pid in Members()) {
            if(!identities.ContainsKey(pid)) throw new InvalidOperationException("Live job member lacks complete identity: "+pid);
        }
        foreach(var identity in identities.Values) {
            if(WaitForSingleObject(identity.handle,0)==0) continue;
            Check(IsProcessInJob(identity.handle,job,out bool member));
            if(!member) throw new InvalidOperationException("Retained identity left job.");
            try {
                var current=ReadIdentity(identity.pid,identity.handle);
                if(current.createdUtc!=identity.createdUtc || current.parentPid!=identity.parentPid || current.commandLine!=identity.commandLine) throw new InvalidOperationException("Retained identity changed.");
                live.Add(current);
            } catch {
                // A terminating process can lose command metadata before its
                // retained handle signals. Wait briefly on that same handle;
                // do not infer identity or kill authority from a historical PID.
                uint grace=teardownWatch.Elapsed.TotalSeconds<10 ? 250u : 0u;
                if(WaitForSingleObject(identity.handle,grace)!=0) throw;
            }
        }
        // Kernel-enforced membership includes descendants even if their parents
        // already exited. No stale parent PID ever grants stop authority.
        Check(TerminateJobObject(job,124)); return live.ToArray();
    }
    /// <summary>释放本任务句柄；关闭 Job 触发内核成员回收。</summary>
    public void Dispose() {
        if(job!=IntPtr.Zero) { CloseHandle(job); job=IntPtr.Zero; }
        if(thread!=IntPtr.Zero) { CloseHandle(thread); thread=IntPtr.Zero; }
        if(root!=IntPtr.Zero) { CloseHandle(root); root=IntPtr.Zero; }
        foreach(var item in identities.Values) CloseHandle(item.handle); identities.Clear();
    }
}
