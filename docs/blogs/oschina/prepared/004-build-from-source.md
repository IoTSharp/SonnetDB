---
title: 从源码编译 SonnetDB：开发环境搭建指南
categories: SonnetDB,DotNet,开发环境
draft: false
---

从源码构建适合修改 SonnetDB、检查实现或参与贡献。本文修订旧稿中的项目路径和前端说明：当前解决方案使用 `SonnetDB.slnx`，核心库位于 `src/SonnetDB.Core`，Server 位于 `src/SonnetDB`，Web Admin 使用 Vue 3、TypeScript 和 Vite。

## 环境与版本

准备 Git、.NET 10 SDK，以及需要编译 Web Admin 时使用的 Node.js/npm。SDK 选择以仓库 `global.json` 为准，IDE 应支持相应 SDK；不依赖特定 IDE 才能完成构建。

```bash
git clone --recurse-submodules https://github.com/IoTSharp/SonnetDB.git
cd SonnetDB
```

`main` 是持续开发分支。需要研究正式的 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 时，先切换到该标签，再同步子模块：

```bash
git checkout v4.0.0
git submodule update --init --recursive
```

切换版本前应保存自己已有的修改。正式标签与最新 `main` 的功能、公开 API 和测试数量可能不同，构建记录要保存实际提交身份。

## 还原与编译

在仓库根目录执行：

```bash
dotnet restore SonnetDB.slnx
dotnet build SonnetDB.slnx -c Release --no-restore
```

只需要核心库时，可定向构建：

```bash
dotnet build src/SonnetDB.Core/SonnetDB.Core.csproj -c Release
```

Server 的图片和模型组件有自己的原生依赖，随对应还原/发布流程处理。不要把普通构建成功直接视为所有 RID 的 Native AOT 或安装验证通过。

## 运行测试

```bash
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release
```

这些分别覆盖核心和 Server 的测试项目。完整解决方案还包含需要外部服务、平台或专门运行条件的项目，应按各项目说明选择环境。通过某个测试项目不等于全部 Parity、性能、安装和长期运行验收已完成。

## 构建管理界面

```bash
cd web
npm ci
npm run build
cd ..
```

构建输出在 `web/dist`。Server 项目声明了该静态资源的内容映射；发布时使用相应打包流程，不能假定执行一次前端 build 就已经生成完整发布包。Web Admin 是 Vue 应用，开发它不需要为此安装 .NET MAUI 工作负载。

## 启动 Server

```bash
dotnet run --project src/SonnetDB/SonnetDB.csproj -c Debug
```

默认 HTTP 入口是 `http://127.0.0.1:5080/admin/`，首次使用空数据根目录时先完成初始化。进程内使用则直接调用 `Tsdb.Open`，不需要启动 HTTP Server。

参与贡献前阅读仓库 [AGENTS](https://github.com/IoTSharp/SonnetDB/blob/main/AGENTS.md)。每次代码提交前须在最终工作树执行规定的 restore 和 Format Check；修改后重新验证。工程和部署资料见 [README](https://github.com/IoTSharp/SonnetDB/blob/main/README.md)、[发布打包](https://github.com/IoTSharp/SonnetDB/blob/main/docs/releases/README.md)和[前端配置](https://github.com/IoTSharp/SonnetDB/blob/main/web/package.json)。本文示例依据文档与源码核对，未在本次文章整理中另行运行构建。
