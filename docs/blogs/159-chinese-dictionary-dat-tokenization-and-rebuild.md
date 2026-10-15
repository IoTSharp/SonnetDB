---
title: SonnetDB 中文词库实操：编译 .dat、核对 Token 与重建边界
categories: SonnetDB
tags: FullText, Analyzer, Jieba, dotnet
draft: false
---

# SonnetDB 中文词库实操：编译 .dat、核对 Token 与重建边界

SonnetDB 的中文全文检索提供默认词库和可自定义的 Core 分词 API。本文演示如何合并文本词库、编译 `.dat`、核对分词结果，并区分“程序创建了自定义 tokenizer”与“数据库全文索引已经使用该词库”这两个步骤。

![SonnetDB 中文词库：编译 dat、核对 Token 与重建边界](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261015130746983-1254071081.png)

图：AI 主题示意图，并非实测截图；自定义 Core 分词器不代表 Server 自动加载用户词库，重建以已核运行时接线为前提。

## 1. 词库解决什么问题

同一段中文切成什么 token，会影响倒排索引中的词项及查询匹配。领域词、缩写和产品名值得单独核对，但增加词库本身不能证明相关性、召回率或模型质量改善。

SonnetDB.Core 默认使用内嵌中文词库。文档说明其来源为 cppjieba 的 `dict/jieba.dict.utf8`；上游来源与许可证见仓库第三方声明。升级词库时应固定来源提交并同步声明，不能只替换一个来历不明的 `.dat`。

本文于 **2026-10-15** 复核本地源码提交 `dcadffc7137f5653ec46c55a4171cf5f2d83de03`，引用固定到与本专题一致的公开提交 `5424b66672e37f49a2a5633fdde0b54a9b1f0290`。当日正式 Release/NuGet Core 页面为 **4.0.0**。示例按源码项目引用描述，不把当前源码当作任意已安装包的完整功能保证；Graph 仍为 Beta。

## 2. 文本词库与加载顺序

支持的常见格式包括：

```text
词项<TAB>词频
词项 词频
词项 词频 词性
```

`<TAB>` 表示真实制表符，不是字面上的反斜杠加 t。重复词项按后加载文件覆盖前面的词频；词频影响分词路径选择，不是业务优先级或搜索结果的最终分数。

建议把基础、领域、业务词库分别保存，并记录加载顺序、文件 SHA、来源和版本。先用小词表验证行为，再扩大到真实语料。

## 3. 建立独立 Core 示例

准备 .NET 10 SDK，在仓库根目录创建一个专用示例项目：

```powershell
dotnet new console --name DictionaryDemo --output ./scratch/DictionaryDemo --framework net10.0
dotnet add ./scratch/DictionaryDemo/DictionaryDemo.csproj reference ./src/SonnetDB.Core/SonnetDB.Core.csproj
```

将该示例的 `Program.cs` 替换为下面代码。它只创建自己的 `dictionary-demo` 文件夹，不更改数据库索引或全局默认词典：

```csharp
using System.Text;
using SonnetDB.FullText.Tokenization;
using SonnetDB.FullText.Tokenizers.Jieba;

var folder = Path.GetFullPath("dictionary-demo");
Directory.CreateDirectory(folder);
var basePath = Path.Combine(folder, "base.txt");
var userPath = Path.Combine(folder, "user.txt");
var datPath = Path.Combine(folder, "industry.dat");
var utf8 = new UTF8Encoding(false);
File.WriteAllText(basePath, "时序数据库 10 nz\n中文\t20\n", utf8);
File.WriteAllText(userPath, "时序数据库\t9000\n混合检索\t8000\n", utf8);

var merged = ChineseDictionary.FromTextFiles(basePath, userPath);
Console.WriteLine($"merged-frequency={merged.GetFrequency("时序数据库")}");

var result = ChineseDictionaryCompiler.Compile([basePath, userPath], datPath);
var dictionary = ChineseDictionary.FromCompiledFile(datPath);
Console.WriteLine($"terms={result.TermCount}");
Console.WriteLine($"loaded-frequency={dictionary.GetFrequency("时序数据库")}");

var tokenizer = new ChineseTokenizer(dictionary);
var sink = new CollectingTokenSink();
tokenizer.Tokenize("时序数据库 混合检索".AsSpan(), sink);
Console.WriteLine(string.Join(" / ", sink.Tokens.Select(token => token.Text)));
```

运行：

```powershell
dotnet run --project ./scratch/DictionaryDemo
Get-FileHash ./dictionary-demo/industry.dat -Algorithm SHA256
```

从这份输入和当前实现可以核对的预期为：

```text
merged-frequency=9000
terms=3
loaded-frequency=9000
时序数据库 / 混合检索
```

基础文件的“时序数据库”被用户文件覆盖，因此不是词频相加；合并后是三个不同词项。编译结果与加载后的词频都应核对。`CollectingTokenSink` 主要用于观察/测试，示例文本很短；这不是大型语料处理器。

本次发布任务没有运行这段示例。以上是公开 API 和既有测试源码支持的预期，不是本次机器输出，也没有编造编译耗时或检索指标。

## 4. 单文件编译工具与多文件 API

仓库另有一个命令行编译工具，当前入口只接受两个位置参数：输入文本词库和输出 `.dat`。

```powershell
dotnet run --project src/SonnetDB.Core/FullText/Tools/DictionaryCompiler -- ./dictionary-demo/user.txt ./dictionary-demo/user-only.dat
```

这个命令只编译 `user.txt`，不能把它当作上节多文件合并结果。多词库按顺序合并使用 `ChineseDictionaryCompiler.Compile([...], outputPath)`。`.dat` 是编译产物扩展名，不应由文件名推断它一定采用某个固定数据结构或承诺跨所有版本兼容。

同时保留源词库、加载顺序、编译源码/包版本及产物 SHA，才能回答“这次运行实际使用哪份词典”。仅比较 `.dat` 文件名没有意义。

## 5. 接入全文索引前，先核运行时入口

上节 `new ChineseTokenizer(dictionary)` 创建的是调用代码持有的实例，不会把 `ChineseDictionary.Default` 替换掉。

当前文档全文索引的 tokenizer 工厂按 `unicode`、`cjk`、`jieba` 等名称选择实现；其中 `jieba/chinese` 分支调用**默认构造函数** `new ChineseTokenizer()`。因此，不能只把自定义 `.dat` 放到某个目录，就宣称现有 SQL/Server 索引已加载它。本文没有提供不存在的 `dictionary_path` SQL 参数或全局热加载开关。

| 已完成的操作 | 可以确认 | 还需确认 |
|---|---|---|
| 加载用户词表并打印 token | 该实例的分词行为 | 数据库实际使用的 tokenizer |
| 编译并加载 `.dat` | 该文件能在对应 API 加载 | 索引/查询入口接线和版本一致性 |
| SQL 选择 `jieba` | 选择当前默认中文分词分支 | 自定义词库并未因此自动绑定 |
| 完成索引重建 | 已处理目标重建操作的结果 | 实际运行时词库及查询效果 |

默认词库更新、定制宿主或后续正式接线，应有明确实现和验证记录。不能用“Server 可以叠加领域词库”的设计说明，替代当前部署中的加载证据。

## 6. 词库改变后为什么要重建

在确认索引运行时真正使用新词库后，旧倒排索引仍保存旧 token；只改变查询分词器，可能造成建索引与查索引的分析方式不一致。

已有全文索引可按 SQL 合同安排重建，例如文档中的现有 `logs`/`ft_logs_message`：

```sql
REBUILD FULLTEXT INDEX ft_logs_message ON logs;
```

执行前应确认数据库、集合、索引原名、权限、运行时词库和维护窗口。这个语句不负责安装或绑定用户词库，也不自动建立可靠的业务回退。

验收至少保留同一组固定查询的 token、命中文档、排序及失败样例。业务词库变化应与索引重建、查询端更新、回退版本配套；不能仅因命中条数增加就宣布相关性改善。

## 7. 支持范围与复核来源

本篇覆盖自定义 Core 词库 API、编译工具、默认入口和重建前提。没有执行真实语料质量、容量、长期运行、Server 热更新或生产重建验收，也不承诺词库升级自动提升效果。

- [全文中文词库说明](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/fulltext-dictionaries.md)
- [默认词库与公共加载 API](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/FullText/Tokenizers/Jieba/ChineseDictionary.cs)
- [词库编译 API](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/FullText/Tokenizers/Jieba/ChineseDictionaryCompiler.cs)
- [文档全文索引 tokenizer 工厂](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/FullText/DocumentFullTextIndexStore.cs)
- [分词/覆盖/编译加载测试](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Core.Tests/FullText/Tokenizers/Jieba/ChineseTokenizerTests.cs)
- [第三方来源声明](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/THIRD_PARTY_NOTICES.md)
