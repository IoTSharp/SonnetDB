# 全文中文词库

SonnetDB.Core 内置 `jieba` 分词器和一个中等规模默认中文词库，用于让中文全文索引开箱可用。默认词库来自 `cppjieba` 的 `dict/jieba.dict.utf8`，已转换为 SonnetDB 的 `词项<TAB>词频` 源词库，并在构建时编译成紧凑的 `.dat` embedded resource。

默认词库来源和许可证记录在 [`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md)。如果升级词库，应固定 upstream commit，并同步更新第三方声明。

## 词库格式

SonnetDB 支持常见 Jieba / cppjieba 文本词库格式：

```text
词项<TAB>词频
词项 词频
词项 词频 词性
```

示例：

```text
SonnetDB	10000
时序数据库 9000 nz
混合检索 8000
```

同名词项重复出现时，后加载的词库覆盖前面的词频。推荐加载顺序是：基础词库、领域词库、业务用户词库。

## 公共 API

可以直接从多个文本词库创建分词器：

```csharp
using SonnetDB.FullText.Tokenizers.Jieba;

ChineseDictionary dictionary = ChineseDictionary.FromTextFiles(
    "/data/dicts/jieba.dict.txt",
    "/data/dicts/industry.dict.txt",
    "/data/dicts/user.dict.txt");

ChineseTokenizer tokenizer = new(dictionary);
```

也可以先编译成 `.dat`，后续启动直接加载二进制词典，避免每次解析大文本：

```csharp
using SonnetDB.FullText.Tokenizers.Jieba;

ChineseDictionaryCompiler.Compile(
    [
        "/data/dicts/jieba.dict.txt",
        "/data/dicts/industry.dict.txt",
        "/data/dicts/user.dict.txt",
    ],
    "/data/dicts/sonnetdb-chinese.dat");

ChineseDictionary dictionary = ChineseDictionary.FromCompiledFile(
    "/data/dicts/sonnetdb-chinese.dat");
```

## 索引重建

词库是全文索引的一部分。更换或追加词库后，同一段文本会被切成不同 token，因此既有全文索引需要重建。

```sql
REBUILD FULLTEXT INDEX ft_logs_message ON logs;
```

如果通过备份恢复、删除派生索引目录或维护任务触发重建，也会按当前运行时使用的分词器和词库重新生成索引。

## Core 与服务器版本

- `SonnetDB.Core`：内置中等规模默认词库，保持 NuGet 体积可控，并提供公共 API 让应用加载自定义词库。
- `SonnetDB Server` / 企业版：可以在发行包中叠加更完整的通用词库、THUOCL 等领域词库，以及工业、数据库、AI 等 SonnetDB 官方领域词库。
- 生产环境：建议保留业务词库文件和 `.dat` 缓存的版本记录，词库升级后安排全文索引重建窗口。
