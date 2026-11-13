# 全文完整检索与统一预算

类型化全文检索先执行完整结构过滤，再对全部匹配文档评分和稳定排序，最后分页。TotalHits 是完整过滤命中数，facet 也由全部过滤命中计算。超出预算时请求失败，不把候选窗口、已扫描前缀或 partial facet 当作完整结果。continuation 仍读取每次请求时的当前视图，不跨页固定快照。

## 公开入口

`DocumentCollectionStore.SearchFullTextComplete` 保留原方法签名，并增加以 `FullTextSearchBudget` 为首参数的 overload，以便调用方将同一预算继续传给排序、facet、投影及序列化。`SndbFullTextSearchOptions.Budget` 和 Server `FullTextSearchRequest.Budget` 是可选 init 属性，保留原构造器和 Deconstruct；旧调用无需修改。

`FullTextSearchBudgetOptions` 的字段都只能降低默认硬上限；空值使用默认值，非正数拒绝，提高请求值不会提高服务硬上限。

| 字段 | 默认上限 | 计量边界 |
| --- | --- | --- |
| MaxDocuments | 100000 | 扫描文档，包括被结构过滤掉的文档 |
| MaxDocumentBytes | 64 MiB | 扫描 ID/JSON 的 UTF-16 字节估计，不等于进程 heap |
| MaxPostingVisits | 1000000 | 倒排 posting 检查，包含模糊展开所需检查 |
| MaxWorkUnits | 10000000 | 扫描、排序、facet、投影、输出字符串与序列化计费 |
| MaxResponseBytes | 4 MiB | 完整 UTF-8 JSON 响应或远程错误/成功响应读取 |
| TimeoutMilliseconds | 10000 | 从预算建立到执行、响应编码/读取与发送的期限 |
| MaxQueryLength | 4096 | 查询文本 UTF-16 字符数 |

Server 的全文请求体另有固定 4 MiB 上限，在反序列化前分块检查；请求 shape、查询长度、fingerprint 和过滤转换在执行预算下校验。预算不进入 continuation fingerprint，因此调用方可以对后续页降低限额；排序、过滤与查询变化仍按原 token 合同拒绝。

## 失败与取消

执行、排序、facet 和投影共享取消及期限；超限抛 `FullTextSearchBudgetExceededException`，Server 在成功响应开始前返回 HTTP 422 / `fulltext_search_budget_exceeded`。非法请求/预算返回明确参数错误。remote .NET SDK 将预算错误映射为同一异常，并对成功与错误体都按总字节/工作量/期限有界读取；响应不完整时不返回可消费结果。

响应使用 source-generated `JsonTypeInfo<T>` 完整编码到有界缓冲，单个输出字符串在编码前分块校验，所有 UTF-8 字节在写入时累计。只有完整响应通过后才交给 HTTP body。发送采用同一预算令牌执行 Write 和 Flush；发送后的取消/期限中止连接，不在已开始的响应后追加错误 JSON。不能保证撤回已经交给客户端或网络的字节，不能把单次 Write 返回当作客户端收件确认。

本合同与[本阶段代码验证](../audits/capability-code-stage-20261112.md)分别记录源码、本机 Server/独立消费者和公开新版包/真实外部远端。发布、安装、固定硬件和生产资格不由本机对拍推导。
