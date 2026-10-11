## SonnetDB Visual Basic 6 连接器：让经典 Windows 应用接入时序数据

SonnetDB VB6 连接器为经典 32 位 Windows 应用提供源模块与 stdcall bridge，保留熟悉的 Connection/Result 编程形态。它面向仍在维护的设备工具、桌面采样和产线辅助程序，不要求把 VB6 应用改写为托管程序。

本文依据当前 VB6 README、类模块与桥接源码整理。正式 4.0.0 与 main 分开核对；源码和 x86 构建预设存在不等于目标机 Native AOT 库、VB6 程序与 bridge 已经完成实机验收。

### 为什么需要两层 DLL

```text
VB6 Declare / stdcall / 32 位
    -> SonnetDB.VB6.Native.dll
    -> SonnetDB.Native.dll / cdecl / 同样 32 位
```

bridge 转发 C ABI，并处理 VB6 Unicode String 与 UTF-8。x86 进程无法直接加载 x64 Native DLL；先确认实际 win-x86 Native 产物能够构建和加载，再开始部署 bridge，不能靠文件同名推断 ABI 兼容。

### 小样本与错误清理

把 `.bas` 和两个 `.cls` 模块加入自己的授权 VB6 工程，使用一个新的演示目录：

```vb
Private Sub RunDemo()
    On Error GoTo Failed
    Dim connection As SonnetDbConnection
    Dim result As SonnetDbResult
    Dim row As Long
    Dim started As Single

    Set connection = New SonnetDbConnection
    connection.Open App.Path & "\vb6-cpu-demo"
    connection.ExecuteNonQuery "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)"
    connection.ExecuteNonQuery _
        "INSERT INTO cpu (time, host, usage) VALUES " & _
        "(1710000000000,'edge-1',0.42),(1710000001000,'edge-1',0.73)"
    Set result = connection.Execute( _
        "SELECT time, host, usage FROM cpu ORDER BY time LIMIT 10")
    started = Timer
    For row = 1 To 10
        If Timer < started Or Timer - started > 5 Then Exit For
        If Not result.NextRow Then Exit For
        Debug.Print result.GetInt64Text(0), result.GetString(1), result.GetDouble(2)
    Next row
    GoTo Cleanup
Failed:
    Debug.Print Err.Number, Err.Description
Cleanup:
    On Error Resume Next
    If Not result Is Nothing Then result.Close
    If Not connection Is Nothing Then connection.Close
End Sub
```

`Timer` 跨午夜时示例直接结束读取，最大 10 行/约 5 秒；同步 native 调用本身不能由这个检查硬中断。生产错误处理需要保留原错误与关闭错误，不能将 `On Error Resume Next` 扩大到正常执行区域。

### 64 位整数的精度

VB6 `Long` 是 32 位，不能保存现代 Unix 毫秒时间戳。连接器的 `GetInt64` 以 Double 方便消费，但 Double 只保证一定范围内的整数精度；完整 64 位计数应使用 `GetInt64Text`。

本例直接打印文本时间戳，避免先转换 Double 再转 String。字段为空时还要按结果类型判断，不能把空值和零混在一起。

### 构建、部署与 CI

仓库为 C Native 和 VB6 C bridge 提供 Windows x86 CMake 预设：

```powershell
cmake -S connectors/c --preset windows-x86
cmake --build artifacts/connectors/c/win-x86 --config Release
cmake -S connectors/vb6 --preset windows-x86
cmake --build artifacts/connectors/vb6/win-x86 --config Release
```

generator、.NET Native AOT RID 支持和 Win32 C++ 工具链需在自己的环境逐项确认。本篇不执行这些命令，也不宣称本机有已经验证的 x86 Native 发行物。

运行时使用匹配的两份 DLL，检查进程位数、库加载路径和必要系统运行库。数据库目录应独立于只读安装目录，备份与升级保留完整数据边界。

公共 CI 没有授权的 VB6 IDE/compiler，因此仓库不在 GitHub-hosted runner 编译 VB6 工程或 VB6 产出的库。C bridge 可由标准 Win32 C 工具链构建，VB6 EXE 则由用户自己的授权环境生成。源模块提供不等于整套商业工具链可由自动化随意安装。

参考：[VB6 连接器](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/vb6/README.md)、[结果类](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/vb6/src/SonnetDbResult.cls)、[bridge](https://github.com/IoTSharp/SonnetDB/blob/main/connectors/vb6/native/sonnetdb_vb6_bridge.c)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
