# Microsoft Visual C++ runtime redistribution notice

This candidate includes the x64 MSVCP140, MSVCP140_1, VCRUNTIME140 and VCRUNTIME140_1 DLLs required by its shipped native dependencies. They are Microsoft software, not covered by SonnetDB's MIT license. Only required files are copied from the Visual Studio installation's registered VC/Redist/MSVC version and x64 Microsoft.VC*.CRT directory. Actual source paths, registered version, file/product versions, import edges and SHA256 hashes appear in native-dependencies.json and dependency-licenses.json.

Microsoft's redistribution guidance: https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files

Redistribution is governed by the applicable Microsoft Visual Studio license and redistributable-code list. This notice records provenance and is not a substitute for those terms. Original licensing terms and redistributable-code eligibility must be reviewed in M47-P07 before public distribution; that review has not been completed. No public release is authorized by this build.

Windows 11 x64 supplies UCRT and the named Windows/API-set dependencies. Those OS components are not copied into this package. Import-table closure does not prove a clean-machine user journey or discover arbitrary dynamically loaded plugins; M47-P05 remains pending.
