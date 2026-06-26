using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

return await ConnectorReleaseTool.RunAsync(args);

static class ConnectorReleaseTool
{
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> SourceDirectoryExclusions = new(IgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "target",
        "__pycache__",
        ".pytest_cache"
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = ReleaseOptions.Parse(args);
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            var context = ReleaseContext.Create(options);
            if (options.Tasks.Contains("build"))
            {
                await BuildConnectorsAsync(context);
            }

            if (options.Tasks.Contains("package"))
            {
                PackageConnectors(context);
            }

            return 0;
        }
        catch (Exception ex)
        {
            var details = ex.ToString();
            Console.Error.WriteLine(details);
            Console.Error.WriteLine($"::error::{EscapeGitHubAnnotation(details)}");
            return 1;
        }
    }

    private static string EscapeGitHubAnnotation(string value)
    {
        return value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        Usage:
          dotnet run --project eng/tools/connectors-release/connectors-release.csproj -- [options]

        Options:
          --tasks <list>          build, package, or all. Default: all.
          --version <version>     Package version, usually the tag without leading v. Default: 0.0.0-dev.
          --rid <rid>             linux-x64, win-x64, or win-x86. Default: current OS x64 RID.
          --configuration <name>  Build configuration. Default: Release.
          --output-root <path>    Package output root. Default: artifacts/release/connectors.
          --skip-smoke           Compile only; skip runnable smoke examples.
          -h, --help             Show this help.
        """);
    }

    private static async Task BuildConnectorsAsync(ReleaseContext context)
    {
        await BuildCConnectorAsync(context);
        await BuildJavaConnectorAsync(context);
        await BuildVb6BridgeAsync(context);
        await BuildGoConnectorAsync(context);
        await BuildRustConnectorAsync(context);
        await BuildPythonConnectorAsync(context);
    }

    private static async Task BuildCConnectorAsync(ReleaseContext context)
    {
        WriteSection($"Building C connector ({context.Rid})");
        RequireCommand("cmake");
        RequireCommand("dotnet");

        var source = Path.Combine(context.RepoRoot, "connectors", "c");
        var binary = Path.Combine(context.BuildRoot, "c", context.Rid);
        var definitions = new Dictionary<string, string>(IgnoreCase)
        {
            ["SONNETDB_C_RID"] = context.Rid
        };

        await RunCMakeConfigureAsync(context, source, binary, GetCMakeArchitecture(context), definitions);
        await RunCMakeBuildAsync(context, binary);
    }

    private static async Task BuildJavaConnectorAsync(ReleaseContext context)
    {
        if (context.Rid is not ("linux-x64" or "win-x64"))
        {
            Console.WriteLine($"Skipping Java connector build for {context.Rid}.");
            return;
        }

        WriteSection($"Building Java connector ({context.Rid})");
        RequireCommand("cmake");
        RequireCommand("java");
        RequireCommand("javac");

        var source = Path.Combine(context.RepoRoot, "connectors", "java");
        var binary = Path.Combine(context.BuildRoot, "java", GetJavaBuildDirectoryName(context));
        var definitions = new Dictionary<string, string>(IgnoreCase)
        {
            ["SONNETDB_JAVA_BUILD_FFM"] = "ON",
            ["SONNETDB_JAVA_NATIVE_LIBRARY"] = GetNativeLibraryPath(context)
        };

        await RunCMakeConfigureAsync(context, source, binary, GetCMakeArchitecture(context), definitions);
        await RunCMakeBuildAsync(context, binary);
    }

    private static async Task BuildVb6BridgeAsync(ReleaseContext context)
    {
        if (context.Rid != "win-x86")
        {
            Console.WriteLine($"Skipping VB6 bridge build for {context.Rid}.");
            return;
        }

        WriteSection("Building VB6 bridge (win-x86)");
        RequireCommand("cmake");

        var source = Path.Combine(context.RepoRoot, "connectors", "vb6");
        var binary = Path.Combine(context.BuildRoot, "vb6", "win-x86");
        await RunCMakeConfigureAsync(context, source, binary, "Win32", new Dictionary<string, string>(IgnoreCase));
        await RunCMakeBuildAsync(context, binary);
    }

    private static async Task BuildGoConnectorAsync(ReleaseContext context)
    {
        if (context.Rid is not ("linux-x64" or "win-x64"))
        {
            Console.WriteLine($"Skipping Go connector compile for {context.Rid}; the package still includes source and native runtime files.");
            return;
        }

        WriteSection($"Compiling Go connector ({context.Rid})");
        RequireCommand("go");

        var environment = CreateNativeEnvironment(context);
        var connectorRoot = Path.Combine(context.RepoRoot, "connectors", "go");
        await RunCommandAsync("go", ["test", "./..."], connectorRoot, environment);

        var outputDir = Path.Combine(context.BuildRoot, "go", context.Rid);
        Directory.CreateDirectory(outputDir);
        var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "sonnetdb-go-quickstart.exe"
            : "sonnetdb-go-quickstart";
        await RunCommandAsync(
            "go",
            ["build", "-o", Path.Combine(outputDir, executable), "./examples/quickstart"],
            connectorRoot,
            environment);
    }

    private static async Task BuildRustConnectorAsync(ReleaseContext context)
    {
        if (context.Rid is not ("linux-x64" or "win-x64"))
        {
            Console.WriteLine($"Skipping Rust connector compile for {context.Rid}.");
            return;
        }

        WriteSection($"Compiling Rust connector ({context.Rid})");
        RequireCommand("cargo");

        await RunCommandAsync(
            "cargo",
            ["build", "--release", "--examples"],
            Path.Combine(context.RepoRoot, "connectors", "rust"),
            CreateNativeEnvironment(context));
    }

    private static async Task BuildPythonConnectorAsync(ReleaseContext context)
    {
        if (context.Rid is not ("linux-x64" or "win-x64"))
        {
            Console.WriteLine($"Skipping Python connector check for {context.Rid}.");
            return;
        }

        WriteSection($"Checking Python connector ({context.Rid})");
        var python = ResolveCommand("python", "python3");

        var environment = CreateNativeEnvironment(context);
        await RunCommandAsync(
            python,
            [
                "-m",
                "py_compile",
                Path.Combine(context.RepoRoot, "connectors", "python", "sonnetdb", "__init__.py"),
                Path.Combine(context.RepoRoot, "connectors", "python", "examples", "quickstart.py"),
                Path.Combine(context.RepoRoot, "connectors", "python", "tests", "test_connector.py")
            ],
            context.RepoRoot,
            environment);

        if (!context.SkipSmoke)
        {
            await RunCommandAsync(
                python,
                [Path.Combine(context.RepoRoot, "connectors", "python", "examples", "quickstart.py")],
                context.RepoRoot,
                environment);
        }
    }

    private static void PackageConnectors(ReleaseContext context)
    {
        WriteSection($"Packaging connectors ({context.Rid})");
        ResetDirectory(context.StagingRoot);
        Directory.CreateDirectory(context.PackageOutputRoot);

        PackageCConnector(context);
        PackageJavaConnector(context);
        PackageGoConnector(context);
        PackageRustConnector(context);
        PackagePythonConnector(context);
        PackagePureBasicConnector(context);
        PackageVb6Connector(context);
    }

    private static void PackageCConnector(ReleaseContext context)
    {
        var root = InitializePackageRoot(
            context,
            "c",
            "The C connector exposes the stable SonnetDB native ABI through sonnetdb.h and a Native AOT shared library.");

        CopyNativeRuntime(context, Path.Combine(root, "native"));
        CopyFilteredDirectory(Path.Combine(context.RepoRoot, "connectors", "c", "include"), Path.Combine(root, "connectors", "c", "include"));
        CopyFilteredDirectory(Path.Combine(context.RepoRoot, "connectors", "c", "examples"), Path.Combine(root, "connectors", "c", "examples"));
        CopyConnectorFile(context, "c", "README.md", root);
        CopyConnectorFile(context, "c", "CMakeLists.txt", root);
        CopyConnectorFile(context, "c", "CMakePresets.json", root);

        var binaryDir = Path.Combine(root, "bin");
        Directory.CreateDirectory(binaryDir);
        var executable = context.IsWindowsRid ? "sonnetdb_quickstart.exe" : "sonnetdb_quickstart";
        CopyFirstExisting(
            [
                Path.Combine(context.BuildRoot, "c", context.Rid, context.Configuration, executable),
                Path.Combine(context.BuildRoot, "c", context.Rid, executable)
            ],
            binaryDir);

        WriteRunScripts(
            context,
            root,
            windowsLines: ["\"%ROOT%bin\\sonnetdb_quickstart.exe\""],
            unixLines: ["\"$ROOT/bin/sonnetdb_quickstart\""]);
        CreateArchive(context, root);
    }

    private static void PackageJavaConnector(ReleaseContext context)
    {
        if (context.Rid is not ("linux-x64" or "win-x64"))
        {
            return;
        }

        var root = InitializePackageRoot(
            context,
            "java",
            "The Java connector provides a Java 8-compatible JNI backend and an optional JDK 21+ FFM backend over the SonnetDB C ABI.");

        CopyNativeRuntime(context, Path.Combine(root, "native"));
        CopyCHeader(context, root);
        CopyFilteredDirectory(Path.Combine(context.RepoRoot, "connectors", "java", "src"), Path.Combine(root, "connectors", "java", "src"));
        CopyFilteredDirectory(Path.Combine(context.RepoRoot, "connectors", "java", "examples"), Path.Combine(root, "connectors", "java", "examples"));
        CopyConnectorFile(context, "java", "README.md", root);
        CopyConnectorFile(context, "java", "CMakeLists.txt", root);
        CopyConnectorFile(context, "java", "CMakePresets.json", root);

        var javaBuild = Path.Combine(context.BuildRoot, "java", GetJavaBuildDirectoryName(context));
        var libDir = Path.Combine(root, "lib");
        Directory.CreateDirectory(libDir);
        File.Copy(Path.Combine(javaBuild, "sonnetdb-java.jar"), Path.Combine(libDir, "sonnetdb-java.jar"), overwrite: true);

        var exampleClasses = Path.Combine(javaBuild, "example-classes");
        if (Directory.Exists(exampleClasses))
        {
            CopyFilteredDirectory(exampleClasses, Path.Combine(root, "example-classes"));
        }

        var nativeDir = Path.Combine(root, "native");
        if (context.IsWindowsRid)
        {
            CopyIfExists(Path.Combine(javaBuild, context.Configuration, "SonnetDB.Java.Native.dll"), nativeDir);
        }
        else
        {
            CopyIfExists(Path.Combine(javaBuild, "libSonnetDB.Java.Native.so"), nativeDir);
        }

        WriteRunScripts(
            context,
            root,
            windowsLines:
            [
                "set CLASSPATH=%ROOT%lib\\sonnetdb-java.jar;%ROOT%example-classes",
                "java -Dsonnetdb.java.backend=jni -Dsonnetdb.native.path=\"%SONNETDB_NATIVE_LIBRARY%\" -Dsonnetdb.jni.path=\"%NATIVE%\\SonnetDB.Java.Native.dll\" -cp \"%CLASSPATH%\" com.sonnetdb.examples.Quickstart"
            ],
            unixLines:
            [
                "java -Dsonnetdb.java.backend=jni -Dsonnetdb.native.path=\"$SONNETDB_NATIVE_LIBRARY\" -Dsonnetdb.jni.path=\"$NATIVE/libSonnetDB.Java.Native.so\" -cp \"$ROOT/lib/sonnetdb-java.jar:$ROOT/example-classes\" com.sonnetdb.examples.Quickstart"
            ]);
        CreateArchive(context, root);
    }

    private static void PackageGoConnector(ReleaseContext context)
    {
        if (context.Rid is not ("linux-x64" or "win-x64"))
        {
            return;
        }

        var root = InitializePackageRoot(
            context,
            "go",
            "The Go connector is a cgo wrapper plus database/sql driver over the SonnetDB C ABI.");

        CopyNativeRuntime(context, Path.Combine(root, "native"));
        CopyCHeader(context, root);
        CopyFilteredDirectory(Path.Combine(context.RepoRoot, "connectors", "go"), Path.Combine(root, "connectors", "go"));

        var binaryDir = Path.Combine(root, "bin");
        Directory.CreateDirectory(binaryDir);
        var executable = context.IsWindowsRid ? "sonnetdb-go-quickstart.exe" : "sonnetdb-go-quickstart";
        CopyIfExists(Path.Combine(context.BuildRoot, "go", context.Rid, executable), binaryDir);

        WriteRunScripts(
            context,
            root,
            windowsLines:
            [
                "set CGO_ENABLED=1",
                "set CGO_LDFLAGS=-L%NATIVE%",
                "cd /d \"%ROOT%connectors\\go\"",
                "go run ./examples/quickstart"
            ],
            unixLines:
            [
                "export CGO_ENABLED=1",
                "export CGO_LDFLAGS=\"-L$NATIVE\"",
                "cd \"$ROOT/connectors/go\"",
                "go run ./examples/quickstart"
            ]);
        CreateArchive(context, root);
    }

    private static void PackageRustConnector(ReleaseContext context)
    {
        if (context.Rid is not ("linux-x64" or "win-x64"))
        {
            return;
        }

        var root = InitializePackageRoot(
            context,
            "rust",
            "The Rust connector provides hand-maintained FFI bindings and safe wrapper types over the SonnetDB C ABI.");

        CopyNativeRuntime(context, Path.Combine(root, "native"));
        CopyCHeader(context, root);
        CopyFilteredDirectory(Path.Combine(context.RepoRoot, "connectors", "rust"), Path.Combine(root, "connectors", "rust"));

        var binaryDir = Path.Combine(root, "bin");
        Directory.CreateDirectory(binaryDir);
        var executable = context.IsWindowsRid ? "quickstart.exe" : "quickstart";
        CopyIfExists(Path.Combine(context.RepoRoot, "connectors", "rust", "target", "release", "examples", executable), binaryDir);

        WriteRunScripts(
            context,
            root,
            windowsLines:
            [
                "cd /d \"%ROOT%connectors\\rust\"",
                "cargo run --release --example quickstart"
            ],
            unixLines:
            [
                "cd \"$ROOT/connectors/rust\"",
                "cargo run --release --example quickstart"
            ]);
        CreateArchive(context, root);
    }

    private static void PackagePythonConnector(ReleaseContext context)
    {
        if (context.Rid is not ("linux-x64" or "win-x64"))
        {
            return;
        }

        var root = InitializePackageRoot(
            context,
            "python",
            "The Python connector is a dependency-free ctypes wrapper over the SonnetDB C ABI.");

        CopyNativeRuntime(context, Path.Combine(root, "native"));
        CopyFilteredDirectory(Path.Combine(context.RepoRoot, "connectors", "python"), Path.Combine(root, "connectors", "python"));
        File.Copy(GetNativeLibraryPath(context), Path.Combine(root, "connectors", "python", GetNativeLibraryName(context)), overwrite: true);

        WriteRunScripts(
            context,
            root,
            windowsLines: ["python \"%ROOT%connectors\\python\\examples\\quickstart.py\""],
            unixLines: ["python \"$ROOT/connectors/python/examples/quickstart.py\""]);
        CreateArchive(context, root);
    }

    private static void PackagePureBasicConnector(ReleaseContext context)
    {
        if (context.Rid is not ("linux-x64" or "win-x64"))
        {
            return;
        }

        var root = InitializePackageRoot(
            context,
            "purebasic",
            "The PureBasic connector is a source include file that dynamically loads the SonnetDB C ABI runtime.");

        CopyNativeRuntime(context, Path.Combine(root, "native"));
        CopyFilteredDirectory(Path.Combine(context.RepoRoot, "connectors", "purebasic"), Path.Combine(root, "connectors", "purebasic"));
        File.Copy(GetNativeLibraryPath(context), Path.Combine(root, "connectors", "purebasic", GetNativeLibraryName(context)), overwrite: true);

        WriteRunScripts(
            context,
            root,
            windowsLines:
            [
                "cd /d \"%ROOT%connectors\\purebasic\"",
                "pbcompiler examples\\quickstart.pb --console --output quickstart.exe",
                "quickstart.exe"
            ],
            unixLines:
            [
                "cd \"$ROOT/connectors/purebasic\"",
                "pbcompiler examples/quickstart.pb --console --output quickstart",
                "./quickstart"
            ]);
        CreateArchive(context, root);
    }

    private static void PackageVb6Connector(ReleaseContext context)
    {
        if (context.Rid != "win-x86")
        {
            return;
        }

        var root = InitializePackageRoot(
            context,
            "vb6",
            "The Visual Basic 6 connector ships VB6 source modules plus a 32-bit stdcall bridge DLL over the SonnetDB C ABI.");

        CopyNativeRuntime(context, Path.Combine(root, "native"));
        CopyFilteredDirectory(Path.Combine(context.RepoRoot, "connectors", "vb6"), Path.Combine(root, "connectors", "vb6"));
        CopyIfExists(Path.Combine(context.BuildRoot, "vb6", "win-x86", context.Configuration, "SonnetDB.VB6.Native.dll"), Path.Combine(root, "native"));
        CreateArchive(context, root);
    }

    private static async Task RunCMakeConfigureAsync(
        ReleaseContext context,
        string source,
        string binary,
        string? architecture,
        IReadOnlyDictionary<string, string> definitions)
    {
        var arguments = new List<string>
        {
            "-S",
            source,
            "-B",
            binary
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            arguments.AddRange(["-G", GetVisualStudioCMakeGenerator()]);
            if (!string.IsNullOrWhiteSpace(architecture))
            {
                arguments.AddRange(["-A", architecture]);
            }
        }
        else
        {
            arguments.Add("-DCMAKE_BUILD_TYPE=Release");
        }

        foreach (var definition in definitions)
        {
            arguments.Add($"-D{definition.Key}={definition.Value}");
        }

        await RunCommandAsync("cmake", arguments, context.RepoRoot);
    }

    private static async Task RunCMakeBuildAsync(ReleaseContext context, string binary)
    {
        var arguments = new List<string>
        {
            "--build",
            binary
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            arguments.AddRange(["--config", context.Configuration]);
        }

        await RunCommandAsync("cmake", arguments, context.RepoRoot);
    }

    private static async Task RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var commandLine = $"> {fileName} {string.Join(' ', arguments.Select(QuoteForDisplay))}";
        Console.WriteLine(commandLine);

        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var item in environment)
            {
                startInfo.Environment[item.Key] = item.Value;
            }
        }

        var recentOutput = new Queue<string>(capacity: 80);
        object outputLock = new();
        void RecordOutput(string line, TextWriter writer)
        {
            writer.WriteLine(line);
            lock (outputLock)
            {
                if (recentOutput.Count >= 80)
                {
                    recentOutput.Dequeue();
                }

                recentOutput.Enqueue(line);
            }
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Cannot start command: {fileName}");
        Task stdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                RecordOutput(line, Console.Out);
            }
        });
        Task stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                RecordOutput(line, Console.Error);
            }
        });

        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
        {
            string tail;
            lock (outputLock)
            {
                tail = string.Join(Environment.NewLine, recentOutput);
            }

            throw new InvalidOperationException(
                $"{fileName} failed with exit code {process.ExitCode}: {commandLine}{Environment.NewLine}{tail}");
        }
    }

    private static string QuoteForDisplay(string value)
    {
        return value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
    }

    private static string? GetCMakeArchitecture(ReleaseContext context)
    {
        return context.Rid switch
        {
            "win-x86" => "Win32",
            "win-x64" => "x64",
            _ => null
        };
    }

    private static string GetVisualStudioCMakeGenerator()
    {
        var configured = Environment.GetEnvironmentVariable("SONNETDB_CMAKE_VS_GENERATOR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var vswhere = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            try
            {
                using Process process = new()
                {
                    StartInfo = new ProcessStartInfo(vswhere)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.StartInfo.ArgumentList.Add("-latest");
                process.StartInfo.ArgumentList.Add("-property");
                process.StartInfo.ArgumentList.Add("installationVersion");
                process.Start();
                string version = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && Version.TryParse(version, out var parsed) && parsed.Major >= 18)
                {
                    return "Visual Studio 18 2026";
                }
            }
            catch
            {
                // Fall through to the stable VS 2022 generator name.
            }
        }

        return "Visual Studio 17 2022";
    }

    private static string GetJavaBuildDirectoryName(ReleaseContext context)
    {
        return context.Rid == "win-x64" ? "windows-x64" : context.Rid;
    }

    private static string InitializePackageRoot(ReleaseContext context, string connector, string description)
    {
        var packageName = $"sonnetdb-connector-{connector}-{context.Version}-{context.Rid}";
        var root = Path.Combine(context.StagingRoot, packageName);
        ResetDirectory(root);

        var license = Path.Combine(context.RepoRoot, "LICENSE");
        if (File.Exists(license))
        {
            File.Copy(license, Path.Combine(root, "LICENSE"), overwrite: true);
        }

        File.WriteAllText(Path.Combine(root, "VERSION"), context.Version);
        WritePackageReadme(context, Path.Combine(root, "README.md"), connector, description);
        return root;
    }

    private static void WritePackageReadme(ReleaseContext context, string path, string connector, string description)
    {
        var nativeName = GetNativeLibraryName(context);
        var lines = new[]
        {
            $"# SonnetDB {connector} Connector {context.Version} ({context.Rid})",
            "",
            description,
            "",
            "This zip is self-contained for the target platform. It includes connector files, quickstart examples, the SonnetDB native C ABI runtime, and launch scripts where the connector can be run from source.",
            "",
            "## Layout",
            "",
            "- `connectors/` contains connector source, public headers, and examples.",
            $"- `native/` contains `{nativeName}` and, on Windows, the import library used by C/C++/cgo/Rust linkers.",
            "- `bin/` contains compiled quickstart binaries when the connector build produces them.",
            "- `run-quickstart.cmd` or `run-quickstart.sh` configures the native library path and runs the packaged example.",
            "- `LICENSE` and `VERSION` describe the package version and license.",
            "",
            "## Versioning",
            "",
            "Connector release packages are produced from Git tags named `vX.Y.Z`; the package version is the tag without the leading `v`.",
            "",
            "## Requirements",
            "",
            "Compiled C and Java packages can run with the included native files. Source-level connectors still require their language toolchain, such as Go, Rust, Python, PureBasic, or Visual Basic 6.",
            "",
            "The ODBC connector is reserved and is not packaged until its driver implementation exists."
        };
        File.WriteAllLines(path, lines);
    }

    private static void WriteRunScripts(
        ReleaseContext context,
        string root,
        IReadOnlyList<string> windowsLines,
        IReadOnlyList<string> unixLines)
    {
        var nativeName = GetNativeLibraryName(context);
        if (context.IsWindowsRid)
        {
            var lines = new List<string>
            {
                "@echo off",
                "setlocal",
                "set ROOT=%~dp0",
                "set NATIVE=%ROOT%native",
                "set SONNETDB_NATIVE_LIB_DIR=%NATIVE%",
                $"set SONNETDB_NATIVE_LIBRARY=%NATIVE%\\{nativeName}",
                "set PATH=%NATIVE%;%PATH%"
            };
            lines.AddRange(windowsLines);
            File.WriteAllLines(Path.Combine(root, "run-quickstart.cmd"), lines);
            return;
        }

        var shellLines = new List<string>
        {
            "#!/usr/bin/env bash",
            "set -euo pipefail",
            "ROOT=\"$(cd \"$(dirname \"${BASH_SOURCE[0]}\")\" && pwd)\"",
            "NATIVE=\"$ROOT/native\"",
            "export SONNETDB_NATIVE_LIB_DIR=\"$NATIVE\"",
            $"export SONNETDB_NATIVE_LIBRARY=\"$NATIVE/{nativeName}\"",
            "export LD_LIBRARY_PATH=\"$NATIVE:${LD_LIBRARY_PATH:-}\""
        };
        shellLines.AddRange(unixLines);

        var shellPath = Path.Combine(root, "run-quickstart.sh");
        File.WriteAllLines(shellPath, shellLines);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && CommandExists("chmod"))
        {
            RunCommandAsync("chmod", ["+x", shellPath], root).GetAwaiter().GetResult();
        }
    }

    private static void CreateArchive(ReleaseContext context, string root)
    {
        Directory.CreateDirectory(context.PackageOutputRoot);
        var archiveName = $"{Path.GetFileName(root)}.zip";
        var archivePath = Path.Combine(context.PackageOutputRoot, archiveName);
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        ZipFile.CreateFromDirectory(root, archivePath, CompressionLevel.Optimal, includeBaseDirectory: true);
        Console.WriteLine($"Created {archivePath}");
    }

    private static void CopyConnectorFile(ReleaseContext context, string connector, string fileName, string root)
    {
        var source = Path.Combine(context.RepoRoot, "connectors", connector, fileName);
        if (!File.Exists(source))
        {
            return;
        }

        var destinationDirectory = Path.Combine(root, "connectors", connector);
        Directory.CreateDirectory(destinationDirectory);
        File.Copy(source, Path.Combine(destinationDirectory, fileName), overwrite: true);
    }

    private static void CopyCHeader(ReleaseContext context, string root)
    {
        CopyFilteredDirectory(
            Path.Combine(context.RepoRoot, "connectors", "c", "include"),
            Path.Combine(root, "connectors", "c", "include"));
    }

    private static void CopyNativeRuntime(ReleaseContext context, string destination)
    {
        Directory.CreateDirectory(destination);
        var nativeLibrary = GetNativeLibraryPath(context);
        File.Copy(nativeLibrary, Path.Combine(destination, Path.GetFileName(nativeLibrary)), overwrite: true);

        if (!context.IsWindowsRid)
        {
            return;
        }

        var nativeDirectory = Path.GetDirectoryName(nativeLibrary) ?? throw new InvalidOperationException("Native library has no parent directory.");
        var importLibrary = Path.Combine(nativeDirectory, "SonnetDB.Native.lib");
        if (File.Exists(importLibrary))
        {
            File.Copy(importLibrary, Path.Combine(destination, "SonnetDB.Native.lib"), overwrite: true);
        }
    }

    private static string GetNativeLibraryPath(ReleaseContext context)
    {
        var name = GetNativeLibraryName(context);
        var candidates = context.IsWindowsRid
            ? new[]
            {
                Path.Combine(context.RepoRoot, "connectors", "c", "native", "SonnetDB.Native", "bin", context.Configuration, "net10.0", context.Rid, "native", name),
                Path.Combine(context.BuildRoot, "c", context.Rid, context.Configuration, name),
                Path.Combine(context.BuildRoot, "c", context.Rid, name),
                Path.Combine(context.BuildRoot, "c", context.Rid, "native", context.Rid, "publish", name)
            }
            : new[]
            {
                Path.Combine(context.BuildRoot, "c", context.Rid, name),
                Path.Combine(context.BuildRoot, "c", context.Rid, "native", context.Rid, "publish", name),
                Path.Combine(context.RepoRoot, "connectors", "c", "native", "SonnetDB.Native", "bin", context.Configuration, "net10.0", context.Rid, "native", name)
            };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new InvalidOperationException($"Cannot find {name} for {context.Rid}. Build the C connector first.");
    }

    private static string GetNativeLibraryName(ReleaseContext context)
    {
        return context.IsWindowsRid ? "SonnetDB.Native.dll" : "SonnetDB.Native.so";
    }

    private static Dictionary<string, string> CreateNativeEnvironment(ReleaseContext context)
    {
        var nativeLibrary = GetNativeLibraryPath(context);
        var nativeDirectory = Path.GetDirectoryName(nativeLibrary) ?? throw new InvalidOperationException("Native library has no parent directory.");
        var environment = new Dictionary<string, string>(IgnoreCase)
        {
            ["SONNETDB_NATIVE_LIB_DIR"] = nativeDirectory,
            ["SONNETDB_NATIVE_LIBRARY"] = nativeLibrary,
            ["CGO_ENABLED"] = "1",
            ["CGO_LDFLAGS"] = $"-L{nativeDirectory}"
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            environment["PATH"] = $"{nativeDirectory};{Environment.GetEnvironmentVariable("PATH")}";
        }
        else
        {
            environment["LD_LIBRARY_PATH"] = $"{nativeDirectory}:{Environment.GetEnvironmentVariable("LD_LIBRARY_PATH")}";
        }

        return environment;
    }

    private static void CopyFilteredDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (SourceDirectoryExclusions.Contains(name))
            {
                continue;
            }

            CopyFilteredDirectory(directory, Path.Combine(destination, name));
        }
    }

    private static void CopyFirstExisting(IReadOnlyList<string> candidates, string destination)
    {
        foreach (var candidate in candidates)
        {
            if (CopyIfExists(candidate, destination))
            {
                return;
            }
        }
    }

    private static bool CopyIfExists(string source, string destinationDirectory)
    {
        if (!File.Exists(source))
        {
            return false;
        }

        Directory.CreateDirectory(destinationDirectory);
        File.Copy(source, Path.Combine(destinationDirectory, Path.GetFileName(source)), overwrite: true);
        return true;
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static void WriteSection(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"==> {title}");
    }

    private static void RequireCommand(string command)
    {
        if (!CommandExists(command))
        {
            throw new InvalidOperationException($"Required command was not found: {command}");
        }
    }

    private static string ResolveCommand(params string[] commands)
    {
        foreach (var command in commands)
        {
            if (CommandExists(command))
            {
                return command;
            }
        }

        throw new InvalidOperationException($"Required command was not found: {string.Join(" or ", commands)}");
    }

    private static bool CommandExists(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

}

sealed class ReleaseContext
{
    private ReleaseContext(
        string repoRoot,
        string rid,
        string version,
        string configuration,
        string outputRoot,
        bool skipSmoke)
    {
        RepoRoot = repoRoot;
        Rid = rid;
        Version = version;
        Configuration = configuration;
        OutputRoot = outputRoot;
        SkipSmoke = skipSmoke;
        BuildRoot = Path.Combine(repoRoot, "artifacts", "connectors");
        StagingRoot = Path.Combine(outputRoot, "staging", rid);
        PackageOutputRoot = Path.Combine(outputRoot, rid);
        IsWindowsRid = rid.StartsWith("win-", StringComparison.Ordinal);
    }

    public string RepoRoot { get; }
    public string Rid { get; }
    public string Version { get; }
    public string Configuration { get; }
    public string OutputRoot { get; }
    public bool SkipSmoke { get; }
    public string BuildRoot { get; }
    public string StagingRoot { get; }
    public string PackageOutputRoot { get; }
    public bool IsWindowsRid { get; }

    public static ReleaseContext Create(ReleaseOptions options)
    {
        var repoRoot = RepositoryPaths.FindRepoRoot();
        var outputRoot = string.IsNullOrWhiteSpace(options.OutputRoot)
            ? Path.Combine(repoRoot, "artifacts", "release", "connectors")
            : Path.GetFullPath(options.OutputRoot);

        return new ReleaseContext(
            repoRoot,
            options.Rid,
            options.Version,
            options.Configuration,
            outputRoot,
            options.SkipSmoke);
    }
}

sealed class ReleaseOptions
{
    private ReleaseOptions(
        HashSet<string> tasks,
        string version,
        string rid,
        string configuration,
        string? outputRoot,
        bool skipSmoke,
        bool showHelp)
    {
        Tasks = tasks;
        Version = version;
        Rid = rid;
        Configuration = configuration;
        OutputRoot = outputRoot;
        SkipSmoke = skipSmoke;
        ShowHelp = showHelp;
    }

    public HashSet<string> Tasks { get; }
    public string Version { get; }
    public string Rid { get; }
    public string Configuration { get; }
    public string? OutputRoot { get; }
    public bool SkipSmoke { get; }
    public bool ShowHelp { get; }

    public static ReleaseOptions Parse(string[] args)
    {
        var tasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "build", "package" };
        var version = "0.0.0-dev";
        var rid = DefaultRid();
        var configuration = "Release";
        string? outputRoot = null;
        var skipSmoke = false;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                case "--tasks":
                    tasks = ParseTasks(ReadValue(args, ref i, arg));
                    break;
                case "--version":
                    version = ReadValue(args, ref i, arg);
                    break;
                case "--rid":
                    rid = ReadValue(args, ref i, arg);
                    break;
                case "--configuration":
                    configuration = ReadValue(args, ref i, arg);
                    break;
                case "--output-root":
                    outputRoot = ReadValue(args, ref i, arg);
                    break;
                case "--skip-smoke":
                    skipSmoke = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        ValidateRid(rid);
        return new ReleaseOptions(tasks, version, rid, configuration, outputRoot, skipSmoke, showHelp);
    }

    private static HashSet<string> ParseTasks(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(task, "all", StringComparison.OrdinalIgnoreCase))
            {
                result.Add("build");
                result.Add("package");
                continue;
            }

            if (task is not ("build" or "package"))
            {
                throw new ArgumentException($"Unsupported task: {task}");
            }

            result.Add(task);
        }

        if (result.Count == 0)
        {
            throw new ArgumentException("At least one task must be selected.");
        }

        return result;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        return args[index];
    }

    private static void ValidateRid(string rid)
    {
        if (rid is not ("linux-x64" or "win-x64" or "win-x86"))
        {
            throw new ArgumentException($"Unsupported RID: {rid}");
        }
    }

    private static string DefaultRid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "win-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux-x64";
        }

        throw new PlatformNotSupportedException("Only Windows and Linux connector packaging are currently supported.");
    }
}

static class RepositoryPaths
{
    public static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SonnetDB.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Cannot locate repository root. Run this tool from inside the SonnetDB repository.");
    }
}
