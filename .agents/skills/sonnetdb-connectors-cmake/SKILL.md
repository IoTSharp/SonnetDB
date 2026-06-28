---
name: sonnetdb-connectors-cmake
description: Build, verify, or troubleshoot SonnetDB C and Java native connectors that use CMake, NativeAOT, JNI, or JDK 21 FFM. Use when Codex needs to run connector quickstarts, check VS2026/VS2022 C++ toolchains, diagnose missing cmake/cl/JDK tools, or validate connectors/c and connectors/java changes.
---

# SonnetDB Connectors CMake

Use this skill for SonnetDB repository connector work under `connectors/c` and `connectors/java`.

## Quick Workflow

1. Start from the repository root.
2. Run the helper first:

```powershell
.\.agents\skills\sonnetdb-connectors-cmake\scripts\check-sonnetdb-connectors.ps1
```

3. If `cmake` or C/C++ tools are missing, still run the fallback checks:

```powershell
.\.agents\skills\sonnetdb-connectors-cmake\scripts\check-sonnetdb-connectors.ps1 -RunNativePublish -RunJavaCompile -RunJavaFfmQuickstart
```

4. If CMake and MSVC are available, run the full Windows CMake smoke:

```powershell
.\.agents\skills\sonnetdb-connectors-cmake\scripts\check-sonnetdb-connectors.ps1 -RunCMake -VisualStudioGenerator "Visual Studio 18 2026"
```

Use `"Visual Studio 17 2022"` when the machine has VS2022 instead of VS2026.

## Toolchain Expectations

- Required for all fallback checks: `dotnet`, `java`, `javac`.
- Required for CMake quickstarts: `cmake` plus a C compiler/linker.
- On this workstation, Visual Studio Professional 2026 is installed at `C:\Program Files\Microsoft Visual Studio\18\Professional`; its bundled CMake lives under `Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe`, and MSVC `cl.exe` lives under `VC\Tools\MSVC\...\bin\Hostx64\x64\cl.exe`. Do not report CMake/MSVC as unavailable only because a normal PowerShell session lacks them on `PATH`; use `vswhere` or this skill's helper script to discover the VS-bundled tools.
- On Windows, prefer VS2026 Build Tools/Professional with C++ workload. VS2022 Build Tools is also acceptable.
- Current C connector presets may target `Visual Studio 17 2022`; when only VS2026 is installed, use the helper script with `-VisualStudioGenerator "Visual Studio 18 2026"` or configure CMake manually with `-G "Visual Studio 18 2026" -A x64`.

## Manual Commands

NativeAOT fallback check:

```powershell
dotnet publish connectors/c/native/SonnetDB.Native/SonnetDB.Native.csproj `
  --configuration Release `
  --runtime win-x64 `
  /p:SelfContained=true `
  --output artifacts/connectors/c/dotnet-publish-win-x64
```

C connector CMake with VS2026:

```powershell
cmake -S connectors/c `
  -B artifacts/connectors/c/win-x64-vs2026 `
  -G "Visual Studio 18 2026" `
  -A x64 `
  -DSONNETDB_C_RID=win-x64
cmake --build artifacts/connectors/c/win-x64-vs2026 --config Release
```

Java connector CMake with VS2026:

```powershell
cmake -S connectors/java `
  -B artifacts/connectors/java/windows-x64-vs2026 `
  -G "Visual Studio 18 2026" `
  -A x64 `
  -DSONNETDB_JAVA_BUILD_FFM=ON `
  -DSONNETDB_JAVA_NATIVE_LIBRARY="$PWD/artifacts/connectors/c/win-x64-vs2026/Release/SonnetDB.Native.dll"
cmake --build artifacts/connectors/java/windows-x64-vs2026 --config Release
cmake --build artifacts/connectors/java/windows-x64-vs2026 --target run_sonnetdb_java_quickstart --config Release
cmake --build artifacts/connectors/java/windows-x64-vs2026 --target run_sonnetdb_java_quickstart_ffm --config Release
```

## Reporting

Always report:

- Which toolchain was detected (`cmake`, `cl`, `java`, `javac`, CMake generator).
- Which checks ran and passed.
- Which checks were skipped because a tool was missing.
- The exact failing command and first useful error lines if a check fails.

Do not treat missing Go/Rust/ODBC tests as failures unless those connectors have real source/test entry points. They are currently reserved directories.
