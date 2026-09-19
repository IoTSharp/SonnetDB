## 从源码编译 SonnetDB：开发环境搭建指南

对于希望在 SonnetDB 基础上进行二次开发、贡献代码或深入了解内部机制的开发者，从源码编译是最佳选择。本文将详细介绍如何在本地环境中搭建 SonnetDB 的编译和开发环境。

### 环境要求

在开始之前，请确保您的开发环境满足以下要求：

- **.NET 10 SDK**：SonnetDB 基于 .NET 10 构建，您需要安装对应版本的 SDK。可以从 [dotnet.microsoft.com](https://dotnet.microsoft.com) 下载最新的 .NET 10 SDK。
- **Git**：用于克隆源代码仓库。
- **IDE 或编辑器**：推荐使用 Visual Studio 2022+、JetBrains Rider 或 VS Code 配合 C# 扩展。
- **操作系统**：Windows、Linux 或 macOS 均可，SonnetDB 是跨平台项目。

### 获取源码

首先，从 GitHub 克隆 SonnetDB 的源代码仓库：

```bash
git clone https://github.com/maikebing/SonnetDB.git
cd SonnetDB
```

仓库中包含了完整的解决方案文件。您可以使用 `git branch -a` 查看可用的分支，通常 `main` 分支是最新的稳定开发分支。

### 编译项目

Server 图片处理不需要额外申请许可证或配置构建密钥；原生资产随 NuGet 还原和发布，详见[图片处理与迁移](../image-codecs.md)。

SonnetDB 的解决方案包含多个项目，涵盖了核心引擎、HTTP API 服务、CLI 工具和 Web 管理界面。使用 .NET CLI 进行编译：

```bash
# 还原依赖
dotnet restore

# 编译整个解决方案（Release 模式）
dotnet build -c Release

# 或者编译特定项目
dotnet build -c Release src/SonnetDB/SonnetDB.csproj
```

编译完成后，您可以在各项目的输出目录中找到生成的二进制文件。核心数据库引擎的 DLL 文件可以直接被其他 .NET 项目引用。

### 运行测试

SonnetDB 包含了全面的单元测试和集成测试套件。运行测试可以帮助您验证编译结果是否正确，同时也是了解代码行为的有效途径：

```bash
# 运行所有测试
dotnet test

# 运行特定测试项目的测试
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj
```

测试框架支持并行执行，测试套件通常能在几分钟内完成。如果所有测试通过，说明您的编译环境已经正确配置。

### IDE 配置建议

**Visual Studio Code**：安装 C# Dev Kit 扩展和 .NET MAUI 扩展（用于 Web 管理界面相关开发）。打开项目根目录，VS Code 会自动加载解决方案。

**JetBrains Rider**：直接打开解决方案文件（.sln），Rider 会自动识别项目结构和依赖关系。Rider 对 .NET 项目的支持非常完善，代码分析、重构和调试体验优异。

**Visual Studio 2022+**：打开解决方案文件，确保安装了 ASP.NET 和 Web 开发工作负载。

### 编译 Web 管理界面

SonnetDB 的 Web 管理界面基于 React 构建，位于仓库的 `web/` 目录下。如果您需要修改管理界面，需要先安装 Node.js 并编译前端资源：

```bash
cd web
npm install
npm run build
```

编译后的静态资源会自动复制到服务端项目的 `wwwroot` 目录中。

### 启动开发模式

编译成功后，您可以直接启动 SonnetDB 服务进行开发调试：

```bash
# 以开发模式启动
dotnet run --project src/SonnetDB -c Debug
```

对于调试模式，您可以在 IDE 中设置断点，逐步跟踪代码执行流程，这对于理解 SonnetDB 的内部工作机制非常有帮助。建议从 `Program.cs` 入口文件开始，逐步深入到存储引擎、查询引擎和 HTTP API 层。

从源码开始探索，您不仅能获得对 SonnetDB 更深层次的理解，还能参与到开源社区中，提交 Issue、PR，共同推动项目的发展。
