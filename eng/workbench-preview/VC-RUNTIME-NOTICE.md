# Microsoft Visual C++ runtime redistribution notice

This candidate includes the x64 MSVCP140, MSVCP140_1, VCRUNTIME140 and VCRUNTIME140_1 DLLs required by its shipped native dependencies. They are Microsoft software, not covered by SonnetDB's MIT license. Only required files are copied from the Visual Studio installation's registered VC/Redist/MSVC version and x64 Microsoft.VC*.CRT directory. Actual source paths, registered version, file/product versions, import edges and SHA256 hashes appear in native-dependencies.json and dependency-licenses.json.

Microsoft's redistribution guidance: https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files

Redistribution is governed by the applicable Microsoft Visual Studio license and redistributable-code list. Original Community 2026 terms, the redistribution list, and Visual C++ V14 runtime terms are included in licenses/microsoft and bound by microsoft-license-provenance.json. Read MICROSOFT-REDISTRIBUTION-TERMS.md before starting or redistributing; the launcher requires explicit acceptance before running Server. The publisher reported a valid Visual Studio 2026 Community license. This notice does not by itself clear P07 or authorize a public release.

Windows 11 x64 supplies UCRT and the named Windows/API-set dependencies. Those OS components are not copied into this package. Import-table closure does not prove a clean-machine user journey or discover arbitrary dynamically loaded plugins; M47-P05 remains pending.
