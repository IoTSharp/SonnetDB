## SonnetDB PureBasic 连接器：用 Include 文件动态加载 Native 引擎

PureBasic 连接器提供 `SonnetDB.pbi` include 文件，在运行时通过 OpenLibrary/GetFunction 加载 C ABI。它不另外构建一层 PureBasic wrapper DLL，应用直接管理连接和结果句柄。

本文依据当前 include、README 与 quickstart 整理。正式 4.0.0 与 main 分开核对，部署必须配对相同架构与版本的 include、程序和 Native 库。动态加载减少链接步骤，却不会消除 Native 的平台依赖。

### 加载与字符串边界

`PrototypeC` 对齐 cdecl 调用约定。输入字符串编码为 UTF-8；返回文本用 `PeekS(...,#PB_UTF8)` 转换到 PureBasic 字符串。返回的指针仍由 native result 管理，需要在释放 result 前复制。

过程包括 `SonnetDB_Load/Unload`、`Open/Close`、`Execute/ExecuteNonQuery`、`ResultNext`、typed getter、`Flush`、`Version` 和 `LastError`。

### 检查句柄和有界读取

下面程序从 `connectors/purebasic` 目录的项目文件编译，使用新的演示数据目录。

```purebasic
EnableExplicit
XIncludeFile "src/SonnetDB.pbi"
OpenConsole()
Define *connection
Define *result
Define status.l = 1
Define row.l
Define nextRow.l
Define started.q

If SonnetDB_Load() = #False
  PrintN(SonnetDB_LastError())
  End 1
EndIf
*connection = SonnetDB_Open("./purebasic-cpu-demo")
If *connection = 0
  PrintN(SonnetDB_LastError())
  Goto Cleanup
EndIf
If SonnetDB_ExecuteNonQuery(*connection, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)") < 0
  PrintN(SonnetDB_LastError())
  Goto Cleanup
EndIf
If SonnetDB_ExecuteNonQuery(*connection, "INSERT INTO cpu (time, host, usage) VALUES (1710000000000,'edge-1',0.42)") < 0
  PrintN(SonnetDB_LastError())
  Goto Cleanup
EndIf
*result = SonnetDB_Execute(*connection, "SELECT time, host, usage FROM cpu ORDER BY time LIMIT 10")
If *result = 0
  PrintN(SonnetDB_LastError())
  Goto Cleanup
EndIf
started = ElapsedMilliseconds()
For row = 0 To 9
  If ElapsedMilliseconds() - started > 5000 : Break : EndIf
  nextRow = SonnetDB_ResultNext(*result)
  If nextRow = 0 : Break : EndIf
  If nextRow < 0
    PrintN(SonnetDB_LastError())
    Goto Cleanup
  EndIf
  PrintN(Str(SonnetDB_ResultValueInt64(*result, 0)) + " " +
    SonnetDB_ResultValueText(*result, 1) + " " +
    StrD(SonnetDB_ResultValueDouble(*result, 2), 3))
Next
status = 0
Cleanup:
If *result : SonnetDB_ResultFree(*result) : EndIf
If *connection : SonnetDB_Close(*connection) : EndIf
SonnetDB_Unload()
End status
```

最大 10 行/约 5 秒约束读取循环，不能硬中断正在执行的同步 native 操作。数据含 NULL 时先检查值类型；不要把 null getter 的默认值误当成业务零值。

### 工具链与部署

先在对应平台构建 `connectors/c` 的 Native 库，之后用已经授权、与库相同位数的 PureBasic compiler 编译应用。例如 Windows x64 的已构建 DLL 放入可控部署目录后：

```powershell
pbcompiler examples\quickstart.pb --console --output quickstart.exe
```

运行时确保 Loader 可以解析 SonnetDB.Native.dll；Linux 对应 SonnetDB.Native.so。配置实际库路径或受控 PATH，不要靠宽泛搜索加载同名旧库。

本篇不调用 compiler，也不安装或运行构建。Windows/Linux 的源码路径存在，仍需分别验证 Native 加载、SQL、错误和关闭；PureBasic 本身的跨平台支持不能代替这个连接器的目标系统验收。

### CI 与适用范围

PureBasic compiler 是专有工具，公共 runner 不预装授权环境。仓库保留 include 与示例，不在 hosted CI 生成 PureBasic EXE 或 PureBasic 动态库；团队可以在自己的授权本机或 self-hosted runner 构建。

这个过程式封装适合现场工具和本地采集，但数据目录、备份、升级、写入超时与权限边界依然需要管理。include 文件小，不代表运行时数据库和工作负载没有资源预算。

参考：[PureBasic README](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/purebasic/README.md)、[include](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/purebasic/src/SonnetDB.pbi)、[示例](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/purebasic/examples/quickstart.pb)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
