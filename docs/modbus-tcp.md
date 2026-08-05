---
layout: default
title: "Modbus TCP 内建映射表合同"
description: "Milestone 34 Phase A 的 SQL DDL、地址归一化、类型编解码、写入审批与运行时安全边界。"
---

# Modbus TCP 内建映射表合同

本文记录 Milestone 34 Phase A（#288～#290）已经落地的 SQL、安全、catalog、地址校验与编解码合同，也是后续 Modbus TCP runtime、管理面和 parity 测试的共同输入。

> 当前状态：Phase A 的 DDL、Parser/AST、独立版本化 catalog、`SHOW/DESCRIBE MODBUS`、地址冲突校验和类型编解码已经可用。TCP client 轮询、远端写寄存器、TCP server 监听、外部写 staging/审批 runtime、诊断和管理界面仍属于 #291～#296，当前不会连接、监听或收发 Modbus TCP 数据。

## 角色与方向

SonnetDB 只定义两个互不混淆的 Modbus TCP 角色：

| SQL 对象 | Modbus 角色 | 网络方向 | 列映射 | 数据语义 |
| --- | --- | --- | --- | --- |
| `MODBUS SOURCE` | client / master | SonnetDB 主动连接外部 PLC、RTU 或仪表 | `FROM MODBUS` | #291 周期读取外部地址并写入本地表；#292 经审批后可写远端 Coil 或 Holding Register |
| `MODBUS ENDPOINT` | server / slave | SonnetDB 监听端口，外部 client / master 连接 | `EXPOSE AS MODBUS` | #294 把本地表当前值编码为地址空间；#295 将外部写入拒绝或先放入待审批队列 |

`SOURCE` 和 `ENDPOINT` 的上下文已经决定角色。第一版 DDL 不再接受冗余的 `ROLE MASTER` 或 `ROLE SLAVE`，也不允许用同一个对象同时承担两个方向。

后续 Modbus runtime 的合同是全局默认关闭。Phase A 创建 source、endpoint 或映射表只持久化 catalog，不启动连接、轮询或监听；#291/#294 落地后，服务端也必须通过运行时配置显式启用 Modbus，DDL 不能绕过该全局门禁。

普通 `SELECT`、`SHOW` 和 `DESCRIBE` 始终读取 SonnetDB 本地状态，不在查询线程中连接 PLC，也不等待现场设备响应。#291 落地后，实时采集将由后台轮询 runtime 完成；显式维护命令也不得改变普通查询的这一合同。

## 上下文语法

`MODBUS`、`SOURCE`、`ENDPOINT`、`FROM MODBUS`、`EXPOSE AS MODBUS` 和 `USING MODBUS` 是上下文关键字。Lexer 不应把它们全局保留；Parser 只在下列产生式中赋予特殊含义，现有同名表、列或别名继续遵循普通标识符规则。

### Source DDL

```sql
CREATE MODBUS SOURCE <source_name>
WITH (
    TRANSPORT TCP,
    ENDPOINT '<host>:<port>',
    UNIT_ID <integer>,
    POLL_INTERVAL '<duration>',
    TIMEOUT '<duration>',
    RETRY <integer>,
    ADDRESSING ZERO_BASED | ONE_BASED | MODICON,
    BYTE_ORDER BIG_ENDIAN | LITTLE_ENDIAN,
    WORD_ORDER BIG_ENDIAN | LITTLE_ENDIAN,
    ENABLED TRUE | FALSE
);
```

`TRANSPORT` 第一版只能是 `TCP`。source 级 `ADDRESSING`、`BYTE_ORDER` 和 `WORD_ORDER` 是其列映射的默认值；列可以覆盖字节序和字序，但不能覆盖寻址模式。

`ENDPOINT`、`BYTE_ORDER` 和 `WORD_ORDER` 必须显式声明。`TRANSPORT` 可省略且固定为 `TCP`；其余默认值为 `UNIT_ID 1`、`POLL_INTERVAL '1s'`、`TIMEOUT '3s'`、`RETRY 3`、`ADDRESSING MODICON`、`ENABLED FALSE`。`ENABLED` 只是 catalog 中的对象期望状态，不能打开全局 runtime 门禁。Phase A 没有协议 runtime，因此即使保存为 `TRUE`，稳定元数据中的 `runtime_enabled` 仍为 `FALSE`。

主站采集表使用以下形式：

```sql
CREATE TABLE <table_name> (
    <column_definition>
        FROM MODBUS <area>(<address> [, <count>]) [ .BIT(<bit_index>) ]
        AS <wire_type>
        [ BYTE_ORDER BIG_ENDIAN | LITTLE_ENDIAN ]
        [ WORD_ORDER BIG_ENDIAN | LITTLE_ENDIAN ]
        [ SCALE <number> ]
        [ OFFSET <number> ]
        [ ACCESS READ | WRITE | READ_WRITE ],
    ...
)
USING MODBUS SOURCE <source_name>
WITH (
    TABLE_MODE HISTORY | LATEST,
    ON_ERROR KEEP_LAST | NULL | SKIP | MARK_BAD,
    STORE HISTORY
);
```

`TABLE_MODE`、`ON_ERROR` 和 `STORE HISTORY` 都可以省略，也可以按任意顺序声明。默认使用 `TABLE_MODE LATEST`、`ON_ERROR KEEP_LAST`，且不额外保存 history；只有显式声明 `STORE HISTORY` 才额外保留每次成功采样的历史行。

一张表只能绑定一个 Modbus source 或 endpoint。绑定对象必须已经存在于同一数据库，映射在建表时完成地址归一化和冲突校验。

### Endpoint DDL

```sql
CREATE MODBUS ENDPOINT <endpoint_name>
WITH (
    TRANSPORT TCP,
    BIND '<ip>:<port>',
    UNIT_ID <integer>,
    ADDRESSING ZERO_BASED | ONE_BASED | MODICON,
    BYTE_ORDER BIG_ENDIAN | LITTLE_ENDIAN,
    WORD_ORDER BIG_ENDIAN | LITTLE_ENDIAN,
    ALLOWLIST ('<ip-or-cidr>' [, ...]),
    MAX_CONNECTIONS <integer>,
    WRITE_POLICY REJECT | STAGED,
    ENABLED TRUE | FALSE
);
```

endpoint 级 `WRITE_POLICY` 默认是 `STAGED`。Phase A 只持久化以下两种策略；#294/#295 runtime 落地后按对应合同执行：

- `REJECT`：后续 runtime 拒绝全部外部 Modbus 写请求，并记录拒绝审计。
- `STAGED`：#295 runtime 先校验并持久化不可变的待审批请求；协议接受只表示请求已可靠进入待审批队列，不表示业务表已经改变。

后续 runtime 不存在 endpoint 级 `UPDATE_TABLE` 入口策略，也不存在 `AUDIT FALSE`。审计是不可关闭的运行时不变量。

`BIND`、`BYTE_ORDER` 和 `WORD_ORDER` 必须显式声明。`TRANSPORT` 可省略且固定为 `TCP`；其余默认值为 `UNIT_ID 1`、`ADDRESSING MODICON`、`MAX_CONNECTIONS 32`、`WRITE_POLICY STAGED`、空 `ALLOWLIST` 和 `ENABLED FALSE`。非回环 `BIND` 必须配置非空 `ALLOWLIST`；回环地址可以使用空 allowlist。`ENABLED` 不能绕过全局 runtime 门禁。解析器仅为旧草案兼容接受冗余的 `AUDIT TRUE`，`AUDIT FALSE` 始终拒绝；新 DDL 不应写 `AUDIT`。

从站暴露表使用以下形式：

```sql
CREATE TABLE <table_name> (
    <column_definition>
        EXPOSE AS MODBUS <area>(<address> [, <count>]) [ .BIT(<bit_index>) ]
        AS <wire_type>
        [ BYTE_ORDER BIG_ENDIAN | LITTLE_ENDIAN ]
        [ WORD_ORDER BIG_ENDIAN | LITTLE_ENDIAN ]
        [ SCALE <number> ]
        [ OFFSET <number> ]
        [ ACCESS READ | WRITE | READ_WRITE ],
    ...
)
USING MODBUS ENDPOINT <endpoint_name>
WITH (
    ROW KEY <primary_key_value>,
    ON_EXTERNAL_WRITE STAGE_ONLY | UPDATE_TABLE
);
```

`ROW KEY` 固定 endpoint 暴露的单行。第一版不从 Unit ID 或寄存器块推导关系表游标。

`ON_EXTERNAL_WRITE` 可以省略，默认是 `STAGE_ONLY`。`STAGE_ONLY` 只记录审批结果，不更新绑定表；`UPDATE_TABLE` 定义后续 #295 runtime 在审批通过后的应用动作：授权审批者确认待审批请求后，SonnetDB 才按同一份已校验映射更新固定行。两种动作都不能覆盖 endpoint 的 `REJECT`，也不能把 `STAGED` 变成直接写表；Phase A 只持久化并展示该配置，不会接收或审批外部写请求。

## Catalog 持久化与恢复边界

关系表 schema 和 Modbus 定义分别保存在 `tables` 与 `modbus` 两个独立的版本化 catalog 中。正常运行时，映射表创建、关系表 schema 变更、Modbus DDL 和在线备份共享同一个数据库级 schema 锁，因此并发操作不会让备份或其它 DDL 观察到半次映射表创建。单个 catalog 使用临时文件原子替换；任一 catalog 在落盘后、内存发布前失败时会恢复旧文件，绑定提交失败时创建语句还会回滚刚创建的关系表。

这把锁提供的是 DDL 与备份的串行边界，不是两个无锁 catalog 读接口的联合快照。直接分别读取 `TableCatalog` 和 `ModbusCatalog` 的调用方，可能在一次成功映射表创建的两个内存发布点之间短暂观察到“表已存在、绑定尚未发布”；需要跨 catalog 一致性的维护操作必须使用数据库级 schema 边界，不能把两次独立读取解释为原子快照。

Phase A 尚未提供跨两个 catalog 的崩溃 journal。进程被强制终止或掉电若恰好发生在关系表 catalog 已替换、Modbus catalog 尚未替换之间，磁盘上可能留下一个 schema 完整但没有 Modbus 绑定的普通关系表。重启后执行同名的 `CREATE TABLE IF NOT EXISTS ... USING MODBUS` 会明确拒绝这个不完整状态，不会把它报告为成功；应先删除该无绑定表后重试完整 DDL，或通过受控维护流程恢复已知正确的绑定。此边界不能解释为跨 catalog 的掉电原子性。

## 地址空间与 PDU 归一化

Catalog 同时保存 DDL 中的声明地址和归一化后的零基 PDU 地址。网络 runtime 只能使用归一化地址，不得在收发路径再次猜测地址基准。

### 寻址模式

| 模式 | DDL 地址 | PDU 归一化 |
| --- | --- | --- |
| `ZERO_BASED` | `0..65535` | `pdu = declared` |
| `ONE_BASED` | `1..65536` | `pdu = declared - 1` |
| `MODICON` | 传统区域引用号，见下表 | 先校验区域前缀，再减去对应区域基值 |

`MODICON` 支持传统五位引用号，也支持覆盖完整 16 位 PDU 地址的六位扩展引用号：

| 区域 | 五位或常用形式 | 六位扩展形式 | PDU 归一化 |
| --- | --- | --- | --- |
| `COIL` | `1..9999`；前导 `0` 只用于显示 | `1..65536` | `declared - 1` |
| `DISCRETE_INPUT` | `10001..19999` | `100001..165536` | 分别减 `10001` 或 `100001` |
| `INPUT_REGISTER` | `30001..39999` | `300001..365536` | 分别减 `30001` 或 `300001` |
| `HOLDING_REGISTER` | `40001..49999` | `400001..465536` | 分别减 `40001` 或 `400001` |

例如，`HOLDING_REGISTER(40001)`、`HOLDING_REGISTER(400001)`、`ONE_BASED` 下的 `HOLDING_REGISTER(1)` 和 `ZERO_BASED` 下的 `HOLDING_REGISTER(0)` 都归一化为 Holding Register PDU 地址 `0`。

五位范围只表达前 9,999 个地址；更高地址必须使用六位扩展形式。Modicon 区域前缀与 `area` 不匹配时 DDL 失败，不能静默按低位地址解释。

### 范围、位与冲突

- `count` 省略时由 wire type 推导；显式提供时必须与 wire type 所需寄存器数完全一致。
- 归一化后的 `pdu + count - 1` 必须不超过 `65535`。
- Coil、Discrete Input、Holding Register 和 Input Register 是四个独立地址空间；不同空间中的相同 PDU 地址不冲突。
- 同一表绑定中的同一区域映射不得重叠。完整寄存器映射与其 `.BIT(n)` 映射也视为重叠。
- `.BIT(n)` 只适用于 Holding Register 或 Input Register，`n` 必须是 `0..15`，且固定占用一个寄存器。
- 寄存器 `.BIT(n)` 第一版只读，必须声明或继承 `ACCESS READ`。可写单 bit 需要原子 mask-write 或并发安全的 read-modify-write 合同，不在第一版中暗含实现。

## 四类 Modbus 区域

| 区域 | 读取功能码 | 写入功能码 | 可映射值 | 第一版访问上限 |
| --- | --- | --- | --- | --- |
| `COIL(n)` | 01 | 05 / 15 | `BIT` | `READ`、`WRITE`、`READ_WRITE` |
| `DISCRETE_INPUT(n)` | 02 | 无 | `BIT` | 仅 `READ` |
| `HOLDING_REGISTER(n[, count])` | 03 | 06 / 16 | 整数、浮点、BCD、STRING、只读寄存器 BIT | 除寄存器 BIT 外可读写 |
| `INPUT_REGISTER(n[, count])` | 04 | 无 | 整数、浮点、BCD、STRING、只读寄存器 BIT | 仅 `READ` |

所有列的默认访问都是 `ACCESS READ`。`DISCRETE_INPUT`、`INPUT_REGISTER` 或寄存器 `.BIT(n)` 声明为 `WRITE` / `READ_WRITE` 时，DDL 必须失败；runtime 不得等到收到写请求后才发现非法映射。

“四类寄存器读写对拍”在验收中表示四类地址空间都验证读取，只有协议本身可写的 Coil 和完整 Holding Register 验证写入；只读区域必须验证稳定拒绝写入。

## Wire type

| Wire type | 寄存器数 | SQL 语义 | 有效范围或编码 |
| --- | ---: | --- | --- |
| `BIT` | 1 bit | `BOOL` | `0` / `1` |
| `INT16` | 1 | `INT` | 有符号二补码 16 位 |
| `UINT16` | 1 | `INT` | `0..65535` |
| `INT32` | 2 | `INT` | 有符号二补码 32 位 |
| `UINT32` | 2 | `INT` | `0..4294967295` |
| `FLOAT32` | 2 | `FLOAT` | IEEE-754 binary32，必须为有限值 |
| `FLOAT64` | 4 | `FLOAT` | IEEE-754 binary64，必须为有限值 |
| `BCD16` | 1 | `INT` | 无符号 packed BCD，`0..9999` |
| `BCD32` | 2 | `INT` | 无符号 packed BCD，`0..99999999` |
| `STRING(n)` | `ceil(n / 2)` | `STRING` | 固定 `n` 字节 ASCII |

BCD 第一版不定义符号位；任一 nibble 为 `A..F` 时解码失败。`STRING(n)` 中的 `n` 是字节数而不是字符或寄存器数，输入只允许 ASCII `0x01..0x7F`；编码到固定宽度后使用 `0x00` NUL 补齐。解码在第一个 NUL 处结束并移除其后的 padding。超长字符串、非 ASCII 字符和嵌入 NUL 必须拒绝，不能截断或替换。

## 字节序与字序

source 或 endpoint 必须显式声明 `BYTE_ORDER` 和 `WORD_ORDER`。列级设置覆盖对象默认值。单寄存器值不受 `WORD_ORDER` 影响，但 catalog 仍记录继承后的有效设置，确保 metadata 稳定。

以 32 位规范字节 `A B C D` 为例，其中 `A` 是最高有效字节：

| `BYTE_ORDER` | `WORD_ORDER` | 线上的两寄存器布局 | 常用简称 |
| --- | --- | --- | --- |
| `BIG_ENDIAN` | `BIG_ENDIAN` | `AB CD` | `ABCD` |
| `LITTLE_ENDIAN` | `BIG_ENDIAN` | `BA DC` | `BADC` |
| `BIG_ENDIAN` | `LITTLE_ENDIAN` | `CD AB` | `CDAB` |
| `LITTLE_ENDIAN` | `LITTLE_ENDIAN` | `DC BA` | `DCBA` |

对 64 位值，`BYTE_ORDER` 分别控制每个 16 位寄存器内的两个字节，`WORD_ORDER LITTLE_ENDIAN` 反转全部 16 位寄存器的顺序。`STRING(n)` 先形成固定宽度的规范 ASCII 字节流并补 NUL，再应用相同的寄存器内字节序和寄存器顺序。

现有 `modbus_int32`、`modbus_uint32`、`modbus_float32` SQL 函数接受 `ABCD/BADC/CDAB/DCBA`；上表是它们与新 DDL 两级字节序合同的唯一对应关系。

## 缩放与写入逆变换

`SCALE` 默认 `1`，`OFFSET` 默认 `0`。读取公式固定为：

```text
sql_value = raw_value * scale + offset
```

`scale` 和 `offset` 必须是有限数，`scale` 不能为 `0`。写入使用唯一逆变换：

```text
raw_value = (sql_value - offset) / scale
```

写入前必须完成全部校验，且逆变换结果必须能被目标 wire type 精确表示：

- 整数与 BCD 目标要求结果是精确整数，并位于目标范围内。
- `FLOAT32` 要求值能转换为有限 binary32，并按读取公式回算为原 SQL 值；不允许静默降精度。
- `FLOAT64` 只接受有限 binary64；以 `long`、`ulong` 或 `decimal` 提供的有限数值必须能无损转换为 binary64，转换后无法保持原值时拒绝写入。
- `BIT` 只接受布尔值。
- `STRING(n)` 不应用数值缩放。
- 任何隐式四舍五入、截断、饱和、整数溢出、无穷大或 NaN 都必须在 preview 和真实执行前以同一稳定错误拒绝。

preview、dry-run 和提交必须调用同一编解码与校验实现，不能出现“预览可写、提交时换一套转换规则”。

## 采集错误策略

> 后续运行时合同：Phase A 仅解析、校验并持久化 `ON_ERROR`。下列采集失败后的数据、质量和诊断行为由 #293 实现，当前没有轮询任务会执行这些策略。

source 表的 `ON_ERROR` 使用以下四个稳定名称：

| 策略 | 本地数据行为 | 质量与诊断行为 |
| --- | --- | --- |
| `KEEP_LAST` | 保留该列最后一次成功值 | 标记为 stale，并记录本次失败 |
| `NULL` | 本次列值写为 `NULL` | 标记为 bad，并记录本次失败 |
| `SKIP` | 本轮不追加 history 行，也不更新 latest 行 | source health 和失败计数仍更新 |
| `MARK_BAD` | 有可解码响应时保存本次值；无可解码值时保存 `NULL` | 明确标记为 bad，不能伪装成正常样本 |

`KEEP_LAST` 不能把旧值重新标为 good。错误码、最后成功时间、连续失败数和字段质量的公开 metadata 由 #293 在不改变上述四种数据行为的前提下扩展。

## 写入、权限、审批与审计

> 后续运行时合同：Source 远端写、preview 和确认属于 #292；Endpoint 外部写 staging、审批、应用及相关审计属于 #295。Phase A（#288～#290）只持久化和校验 DDL，不会执行本节描述的远端写、staging、审批或运行时审计。

### Source 的受限 SQL 写（#292 后续合同）

#292 实现联网写入后，只有映射到 Coil 或完整 Holding Register 且声明 `ACCESS WRITE` / `READ_WRITE` 的列才允许产生远端写。受限 SQL 写必须在联网前证明只命中一个 Modbus 绑定、一个当前逻辑行和一个映射列；无法证明、命中零行、可能命中多行或同时修改多个映射列时必须直接拒绝，不能展开成无界现场写入。

#292 的写入流程必须固定为：

1. 检查当前数据库身份的表写权限和 Modbus 控制写权限。
2. 使用实际 catalog 版本生成 preview / dry-run，列出 source、Unit ID、功能码、声明地址、PDU 地址、wire type、编码后的 bit/register 和缩放结果。
3. 服务端签发绑定用户、数据库、source、表、列、规范化值、catalog 版本和过期时间的一次性确认；客户端传入普通 `confirmed=true` 不构成授权。
4. 确认有效后执行远端写，收到成功响应后才记录成功；超时、连接失败、Modbus exception 或部分写失败均按失败处理。
5. 历史样本永不因控制写而回写。latest/shadow 状态只有在远端成功后才能更新，并保留“控制写”来源；后续轮询仍是现场状态的权威确认。

远端失败不得伪造本地表成功、成功审计或 good quality。

### Endpoint 的外部写（#295 后续合同）

Modbus TCP 没有可等价为数据库用户的内建身份。#295 实现 Endpoint 外部写后，`ALLOWLIST` 仍只能作为网络边界，不能授予业务表写权限；外部写不得以对端 IP、Unit ID 或 function code 冒充数据库 principal。

#295 必须让持久化后的 `STAGED` 请求至少绑定 endpoint、对端、Unit ID、transaction、function code、声明地址、PDU 地址、原始寄存器、解码值、映射表与 catalog 版本。审批者必须同时具有目标表写权限和 Modbus 审批权限。映射变化、目标行变化、请求过期或权限撤销后，旧请求必须拒绝并重新 staging。

#295 执行审批后的 `UPDATE_TABLE` 时仍必须走普通表约束、事务和审计；约束失败不得报告为已应用。`REJECT`、staging 持久化失败、审批拒绝、审批过期和应用失败都不能改变表值。

### 审计不变量（#292/#295 后续合同）

#292/#295 落地后，运行时审计必须覆盖 runtime 启停、source 写 preview/确认/成功/失败，以及 endpoint 外部写拒绝/staged/批准/拒批/应用/失败；现有 DDL 审计继续沿用服务端审计入口。运行时事件至少包含操作 ID、时间、数据库身份或远端 peer、source/endpoint、表列、Unit ID、功能码、声明地址、PDU 地址、结果、错误码、审批 ID 和 catalog 版本；值载荷遵循现有脱敏策略。

#292/#295 不得提供通过 DDL、配置或调用参数关闭上述审计的入口。需要产生运行时审计的写操作在审计持久化不可用时必须失败关闭，不执行远端写或本地业务表更新。

## 完整示例

### SonnetDB 作为 client / master

```sql
CREATE MODBUS SOURCE line1_plc
WITH (
    TRANSPORT TCP,
    ENDPOINT '192.168.1.50:502',
    UNIT_ID 1,
    POLL_INTERVAL '1s',
    TIMEOUT '800ms',
    RETRY 3,
    ADDRESSING MODICON,
    BYTE_ORDER BIG_ENDIAN,
    WORD_ORDER BIG_ENDIAN
);

CREATE TABLE pump_runtime (
    sample_time DATETIME SAMPLE_TIME,

    running BOOL
        FROM MODBUS COIL(1)
        AS BIT
        ACCESS READ_WRITE,

    fault BOOL
        FROM MODBUS DISCRETE_INPUT(10002)
        AS BIT,

    speed_rpm INT
        FROM MODBUS HOLDING_REGISTER(40001)
        AS UINT16
        SCALE 1
        OFFSET 0
        ACCESS READ_WRITE,

    temperature FLOAT
        FROM MODBUS INPUT_REGISTER(30001)
        AS INT16
        SCALE 0.1
        OFFSET 0,

    flow_rate FLOAT
        FROM MODBUS HOLDING_REGISTER(40010, 2)
        AS FLOAT32
        BYTE_ORDER BIG_ENDIAN
        WORD_ORDER LITTLE_ENDIAN,

    alarm_bit BOOL
        FROM MODBUS HOLDING_REGISTER(40020).BIT(3)
        AS BIT
        ACCESS READ,

    device_name STRING
        FROM MODBUS HOLDING_REGISTER(40030, 8)
        AS STRING(16),

    PRIMARY KEY (sample_time)
)
USING MODBUS SOURCE line1_plc
WITH (
    TABLE_MODE HISTORY,
    ON_ERROR MARK_BAD
);
```

该配置中 `DISCRETE_INPUT(10002)` 归一化为 Discrete Input PDU 地址 `1`，`HOLDING_REGISTER(40001)` 和 `INPUT_REGISTER(30001)` 分别归一化为各自地址空间的 PDU 地址 `0`。`flow_rate` 的有效布局是 `CDAB`。

### SonnetDB 作为 server / slave

```sql
CREATE MODBUS ENDPOINT local_line_shadow
WITH (
    TRANSPORT TCP,
    BIND '192.168.10.20:1502',
    UNIT_ID 1,
    ADDRESSING MODICON,
    BYTE_ORDER BIG_ENDIAN,
    WORD_ORDER BIG_ENDIAN,
    ALLOWLIST ('192.168.10.0/24'),
    MAX_CONNECTIONS 32,
    WRITE_POLICY STAGED
);

CREATE TABLE line_shadow (
    id INT NOT NULL,

    running BOOL
        EXPOSE AS MODBUS COIL(1)
        AS BIT
        ACCESS READ_WRITE,

    speed_rpm INT
        EXPOSE AS MODBUS HOLDING_REGISTER(40001)
        AS UINT16
        ACCESS READ_WRITE,

    temperature FLOAT
        EXPOSE AS MODBUS INPUT_REGISTER(30001)
        AS INT16
        SCALE 0.1
        ACCESS READ,

    PRIMARY KEY (id)
)
USING MODBUS ENDPOINT local_line_shadow
WITH (
    ROW KEY 1,
    ON_EXTERNAL_WRITE UPDATE_TABLE
);
```

上述示例当前只保存 #295 的未来运行时合同，Phase A 不会监听端口或接收外部 client 请求。#295 落地后，外部 client 对 Coil 1 或 Holding Register 40001 的写请求必须先进入 staging，只有授权审批完成后才更新 `line_shadow` 的主键 `1` 行；Input Register 30001 始终只读，DDL 不得提供关闭审计或绕过 staging 的选项。

## SHOW / DESCRIBE 稳定合同

Phase A 固定以下只读语句：

```sql
SHOW MODBUS SOURCES;
SHOW MODBUS ENDPOINTS;
DESCRIBE MODBUS SOURCE line1_plc;
DESCRIBE MODBUS ENDPOINT local_line_shadow;
DESCRIBE MODBUS TABLE pump_runtime;
```

`SHOW MODBUS SOURCES` 至少稳定返回：

```text
name, transport, endpoint, unit_id, addressing, byte_order, word_order,
poll_interval, timeout, retry, runtime_enabled, health,
last_success_at, last_error_code
```

当前 `poll_interval` 和 `timeout` 以 Int64 毫秒返回。结果还追加 `configured_enabled`、`configuration_source` 和 `catalog_revision`；Phase A 的 `runtime_enabled` 固定为 `FALSE`、`health` 为 `disabled`，不会因为执行元数据查询而探测网络。

`SHOW MODBUS ENDPOINTS` 至少稳定返回：

```text
name, transport, bind, unit_id, addressing, byte_order, word_order,
write_policy, allowlist, max_connections, runtime_enabled, health,
last_error_code
```

Endpoint 结果同样追加 `configured_enabled`、`configuration_source` 和 `catalog_revision`。`DESCRIBE MODBUS SOURCE` / `ENDPOINT` 使用与对应 `SHOW` 相同的列合同并只返回指定对象的一行。

`DESCRIBE MODBUS SOURCE` 和 `DESCRIBE MODBUS ENDPOINT` 返回对应对象的全部有效配置及配置来源，不返回凭据或其他敏感值。Phase A 将 `runtime_enabled` 硬编码为 `FALSE`；同时反映全局门禁与对象可运行状态的语义只在 #291 的 source runtime 和 #294 的 endpoint runtime 落地后适用，DDL 存在不等于 runtime 已启用。

`DESCRIBE MODBUS TABLE` 每个映射列返回一行，至少稳定包含：

```text
column_name, direction, area, declared_address, pdu_address,
register_count, bit_index, wire_type, byte_order, word_order,
scale, offset, access, table_mode, on_error, external_write_action
```

`direction` 只能是 `FROM` 或 `EXPOSE`。继承的默认值必须展开为有效值，不能要求调用方读取内部 catalog 后自行合并。后续版本只能以 extend-only 方式增加 metadata 列；不得改名、改变含义或依赖内部 catalog 文件布局。

## 第一版非目标

- 不支持 Modbus RTU、ASCII、串口网关或 RTU-over-TCP；只做 Modbus TCP。
- 不把 OPC UA、S7、三菱、FINS、Allen-Bradley 或 MTConnect 合并进 M34。
- 不实现完整 SCADA、PLC 工程站、边缘工作流、发布或回滚系统。
- 不默认监听 TCP 502，不允许空 allowlist 的公网 endpoint，也不提供匿名直接写表。
- 不用普通 `SELECT` 触发实时 PLC 读取。
- 不支持可写的 Holding/Input Register bit、Unit ID 到关系行的动态映射或寄存器地址空间分页游标。
- 不承诺 Modbus Security/TLS profile；生产网络隔离、VPN 或 TLS 终止属于部署边界。
- 不允许 IoTSharp 或其他上层系统读取 SonnetDB 内部 catalog 文件。集成只能消费稳定 SQL metadata、公开 DTO/API、审批和审计合同；Product、Collection Template、Gateway、EdgeNode 和 ReleaseTask 的业务编排在上层仓库维护。

## 后续实现门禁

Phase A 后续实现不得弱化本文合同：

1. Parser/AST/catalog round-trip 必须覆盖所有 contextual 产生式、三种寻址模式和 SHOW/DESCRIBE，并保持 catalog 版本兼容。
2. codec 必须对四类区域、全部 wire type、四种 32 位字节布局、缩放逆变换、边界与非法输入进行 encode/decode 对拍。
3. 模拟 PLC parity 必须验证四类读取、Coil/Holding 写入、只读区域拒绝、远端失败不提交本地成功。
4. endpoint parity 必须验证 allowlist、连接上限、`REJECT`、`STAGED`、审批后应用、过期/映射变化拒绝及全路径审计。
5. runtime 必须验证默认关闭、取消、超时、退避、重连和普通 `SELECT` 零网络访问。
6. Web/Studio 和 IoTSharp 只能消费公开稳定合同，不能依赖内部 catalog 表或文件。
