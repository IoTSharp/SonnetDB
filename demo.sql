-- ============================================================
--  SonnetDB 功能演示脚本
--  涵盖：建库、用户、权限、建表、写入、查询、聚合、GROUP BY 时间桶、
--        HNSW 向量索引、窗口函数（含空值处理）、PID 控制、
--        预测/异常/变点检测、向量检索（函数+运算符）、地理空间查询、EXPLAIN、元数据查询
--
--  执行方式（控制面语句需 superuser，数据面语句需对应数据库权限）：
--    sndb remote --url http://127.0.0.1:5080 --token <admin-token> \
--         --command "$(cat demo.sql)"
--  或在管理后台 SQL 编辑器中逐段执行。
-- ============================================================


-- ============================================================
-- 第一部分：控制面 —— 建库、建用户、授权
-- ============================================================

-- 1-1  创建演示数据库
CREATE DATABASE demo;

-- 1-2  创建普通用户（只读 / 读写）
CREATE USER viewer   WITH PASSWORD 'viewer123';
CREATE USER writer   WITH PASSWORD 'writer456';
CREATE USER dbadmin  WITH PASSWORD 'admin789'  SUPERUSER;

-- 1-3  授权
GRANT READ  ON DATABASE demo TO viewer;
GRANT WRITE ON DATABASE demo TO writer;
GRANT ADMIN ON DATABASE demo TO dbadmin;

-- 1-4  查看用户与授权
SHOW USERS;
SHOW GRANTS;
SHOW GRANTS FOR viewer;

-- 1-5  为 writer 签发 API Token（明文仅返回一次，请妥善保存）
ISSUE TOKEN FOR writer;

-- 1-6  查看 Token 列表（不含明文）
SHOW TOKENS;
SHOW TOKENS FOR writer;

-- 1-7  查看所有数据库
SHOW DATABASES;

USE demo;
-- ============================================================
-- 第二部分：数据面 —— 建表（CREATE MEASUREMENT）
-- 以下语句在数据库 demo 内执行
-- ============================================================

-- 2-1  CPU 使用率监控表
--      TAG  : host（服务器）、region（地域）
--      FIELD: usage（CPU 使用率 %）、cores（核数）、throttled（是否限速）、label（备注）
CREATE MEASUREMENT cpu (
    host      TAG,
    region    TAG,
    usage     FIELD FLOAT,
    cores     FIELD INT,
    throttled FIELD BOOL,
    label     FIELD STRING
);

-- 2-1b CPU 稀疏样本表：同一 measurement 中部分行缺失 usage / label，用于 fill / locf / interpolate
CREATE MEASUREMENT cpu_sparse (
    host      TAG,
    region    TAG,
    usage     FIELD FLOAT,
    cores     FIELD INT,
    throttled FIELD BOOL,
    label     FIELD STRING
);

-- 2-2  内存监控表
CREATE MEASUREMENT mem (
    host   TAG,
    region TAG,
    used   FIELD FLOAT,
    total  FIELD FLOAT,
    cached FIELD FLOAT
);

-- 2-3  工业反应器温度表（用于 PID 演示）
CREATE MEASUREMENT reactor (
    device  TAG,
    plant   TAG,
    temperature FIELD FLOAT,
    pressure    FIELD FLOAT,
    setpoint    FIELD FLOAT
);

-- 2-4  信号变点检测表
CREATE MEASUREMENT signal (
    source TAG,
    value  FIELD FLOAT
);

-- 2-4b 预测、异常和变点检测专用表
CREATE MEASUREMENT meter (
    device TAG,
    value  FIELD FLOAT
);

CREATE MEASUREMENT meter_seasonal (
    device TAG,
    value  FIELD FLOAT
);

CREATE MEASUREMENT cpu_anomaly (
    host  TAG,
    usage FIELD FLOAT
);

CREATE MEASUREMENT cpu_shift (
    host  TAG,
    value FIELD FLOAT
);

-- 2-5  文档向量检索表（4 维向量，实际生产可用 1536 维）
CREATE MEASUREMENT documents (
    source    TAG,
    category  TAG,
    title     FIELD STRING,
    score     FIELD FLOAT,
    embedding FIELD VECTOR(4)
);

-- 2-6  查看所有 measurement
SHOW MEASUREMENTS;
SHOW TABLES;

-- 2-7  查看表结构
DESCRIBE MEASUREMENT cpu;
DESCRIBE mem;
DESC reactor;

-- 2-8  创建带 HNSW 向量索引的表（m=16 邻居数，ef=200 搜索广度）
CREATE MEASUREMENT doc_indexed (
    source    TAG,
    title     FIELD STRING,
    embedding FIELD VECTOR(4) WITH INDEX hnsw(m=16, ef=200)
);

DESCRIBE MEASUREMENT doc_indexed;

-- 2-9 慢查询样本表：用于 EXPLAIN 与运维诊断演示
CREATE MEASUREMENT slow_query_log (
    db           TAG,
    latency_ms   FIELD INT,
    rows_scanned FIELD INT
);


-- ============================================================
-- 第三部分：写入数据（INSERT INTO ... VALUES）
-- 时间戳单位：Unix 毫秒
-- 基准时间 2024-04-21 00:00:00 UTC = 1713657600000
-- ============================================================

-- 3-1  CPU 数据（server-01，cn-hz，每分钟一条，共 10 条）
INSERT INTO cpu (time, host, region, usage, cores, throttled, label) VALUES
    (1713657600000, 'server-01', 'cn-hz', 0.42, 8, FALSE, 'normal'),
    (1713657660000, 'server-01', 'cn-hz', 0.55, 8, FALSE, 'normal'),
    (1713657720000, 'server-01', 'cn-hz', 0.61, 8, FALSE, 'normal'),
    (1713657780000, 'server-01', 'cn-hz', 0.78, 8, TRUE,  'high'),
    (1713657840000, 'server-01', 'cn-hz', 0.91, 8, TRUE,  'critical'),
    (1713657900000, 'server-01', 'cn-hz', 0.85, 8, TRUE,  'critical'),
    (1713657960000, 'server-01', 'cn-hz', 0.73, 8, FALSE, 'high'),
    (1713658020000, 'server-01', 'cn-hz', 0.60, 8, FALSE, 'normal'),
    (1713658080000, 'server-01', 'cn-hz', 0.48, 8, FALSE, 'normal'),
    (1713658140000, 'server-01', 'cn-hz', 0.39, 8, FALSE, 'normal');

-- 3-2  CPU 数据（server-02，cn-sh，每分钟一条，共 10 条）
INSERT INTO cpu (time, host, region, usage, cores, throttled, label) VALUES
    (1713657600000, 'server-02', 'cn-sh', 0.20, 16, FALSE, 'idle'),
    (1713657660000, 'server-02', 'cn-sh', 0.22, 16, FALSE, 'idle'),
    (1713657720000, 'server-02', 'cn-sh', 0.35, 16, FALSE, 'normal'),
    (1713657780000, 'server-02', 'cn-sh', 0.40, 16, FALSE, 'normal'),
    (1713657840000, 'server-02', 'cn-sh', 0.38, 16, FALSE, 'normal'),
    (1713657900000, 'server-02', 'cn-sh', 0.45, 16, FALSE, 'normal'),
    (1713657960000, 'server-02', 'cn-sh', 0.50, 16, FALSE, 'normal'),
    (1713658020000, 'server-02', 'cn-sh', 0.47, 16, FALSE, 'normal'),
    (1713658080000, 'server-02', 'cn-sh', 0.33, 16, FALSE, 'normal'),
    (1713658140000, 'server-02', 'cn-sh', 0.28, 16, FALSE, 'idle');

-- 3-3  内存数据（server-01）
INSERT INTO mem (time, host, region, used, total, cached) VALUES
    (1713657600000, 'server-01', 'cn-hz', 6.2,  16.0, 1.1),
    (1713657660000, 'server-01', 'cn-hz', 6.5,  16.0, 1.2),
    (1713657720000, 'server-01', 'cn-hz', 7.0,  16.0, 1.3),
    (1713657780000, 'server-01', 'cn-hz', 8.1,  16.0, 1.4),
    (1713657840000, 'server-01', 'cn-hz', 9.3,  16.0, 1.5),
    (1713657900000, 'server-01', 'cn-hz', 10.2, 16.0, 1.6),
    (1713657960000, 'server-01', 'cn-hz', 9.8,  16.0, 1.5),
    (1713658020000, 'server-01', 'cn-hz', 8.7,  16.0, 1.4),
    (1713658080000, 'server-01', 'cn-hz', 7.5,  16.0, 1.3),
    (1713658140000, 'server-01', 'cn-hz', 6.8,  16.0, 1.2);

-- 3-4  反应器数据（r1，plant-A，每 10 秒一条，共 20 条，含阶跃响应）
INSERT INTO reactor (time, device, plant, temperature, pressure, setpoint) VALUES
    (1713657600000, 'r1', 'plant-A', 60.0, 1.01, 75.0),
    (1713657610000, 'r1', 'plant-A', 61.2, 1.02, 75.0),
    (1713657620000, 'r1', 'plant-A', 63.5, 1.03, 75.0),
    (1713657630000, 'r1', 'plant-A', 66.1, 1.04, 75.0),
    (1713657640000, 'r1', 'plant-A', 68.8, 1.05, 75.0),
    (1713657650000, 'r1', 'plant-A', 71.0, 1.05, 75.0),
    (1713657660000, 'r1', 'plant-A', 72.9, 1.06, 75.0),
    (1713657670000, 'r1', 'plant-A', 74.1, 1.06, 75.0),
    (1713657680000, 'r1', 'plant-A', 74.8, 1.07, 75.0),
    (1713657690000, 'r1', 'plant-A', 75.2, 1.07, 75.0),
    (1713657700000, 'r1', 'plant-A', 75.5, 1.07, 75.0),
    (1713657710000, 'r1', 'plant-A', 75.3, 1.07, 75.0),
    (1713657720000, 'r1', 'plant-A', 75.1, 1.07, 75.0),
    (1713657730000, 'r1', 'plant-A', 75.0, 1.07, 75.0),
    (1713657740000, 'r1', 'plant-A', 74.9, 1.07, 75.0),
    (1713657750000, 'r1', 'plant-A', 75.0, 1.07, 75.0),
    (1713657760000, 'r1', 'plant-A', 75.1, 1.07, 75.0),
    (1713657770000, 'r1', 'plant-A', 75.0, 1.07, 75.0),
    (1713657780000, 'r1', 'plant-A', 74.9, 1.07, 75.0),
    (1713657790000, 'r1', 'plant-A', 75.0, 1.07, 75.0);

-- 3-5  信号数据（含明显变点：前段均值≈10，后段均值≈30）
INSERT INTO signal (time, source, value) VALUES
    (1713657600000, 's-1', 10.1),
    (1713657610000, 's-1', 9.8),
    (1713657620000, 's-1', 10.3),
    (1713657630000, 's-1', 10.0),
    (1713657640000, 's-1', 9.9),
    (1713657650000, 's-1', 10.2),
    (1713657660000, 's-1', 10.1),
    (1713657670000, 's-1', 10.0),
    (1713657680000, 's-1', 9.7),
    (1713657690000, 's-1', 10.4),
    (1713657700000, 's-1', 29.8),
    (1713657710000, 's-1', 30.2),
    (1713657720000, 's-1', 30.1),
    (1713657730000, 's-1', 29.9),
    (1713657740000, 's-1', 30.3),
    (1713657750000, 's-1', 30.0),
    (1713657760000, 's-1', 30.1),
    (1713657770000, 's-1', 29.8),
    (1713657780000, 's-1', 30.2),
    (1713657790000, 's-1', 30.0);

-- 3-6  向量数据（4 维嵌入，用于 KNN 检索）
INSERT INTO documents (time, source, category, title, score, embedding) VALUES
    (1713657600000, 'wiki',  'tech',    '时序数据库简介',     0.92, [0.10, 0.20, 0.30, 0.40]),
    (1713657601000, 'wiki',  'tech',    '向量检索原理',       0.88, [0.80, 0.10, 0.05, 0.05]),
    (1713657602000, 'blog',  'tech',    'SonnetDB 快速入门',  0.95, [0.11, 0.21, 0.29, 0.39]),
    (1713657603000, 'blog',  'ops',     '监控系统搭建实践',   0.80, [0.50, 0.50, 0.00, 0.00]),
    (1713657604000, 'paper', 'science', 'PID 控制律综述',     0.75, [0.90, 0.05, 0.03, 0.02]),
    (1713657605000, 'paper', 'science', '工业物联网数据采集', 0.70, [0.12, 0.22, 0.28, 0.38]);

-- 3-7  稀疏 CPU 数据（部分行缺失 usage 或 label，用于 fill / locf / interpolate）
INSERT INTO cpu_sparse (time, host, region, usage, cores, throttled, label) VALUES
    (1713657600000, 'server-01', 'cn-hz', 0.41, 8, FALSE, 'ok'),
    (1713658080000, 'server-01', 'cn-hz', 0.54, 8, TRUE,  'warming'),
    (1713658200000, 'server-01', 'cn-hz', 0.63, 8, TRUE,  'warm'),
    (1713658440000, 'server-01', 'cn-hz', 0.58, 8, FALSE, 'recover');

INSERT INTO cpu_sparse (time, host, region, usage, cores, throttled) VALUES
    (1713657840000, 'server-01', 'cn-hz', 0.46, 8, FALSE);

INSERT INTO cpu_sparse (time, host, region, cores, throttled, label) VALUES
    (1713657720000, 'server-01', 'cn-hz', 8, FALSE, 'gap'),
    (1713657960000, 'server-01', 'cn-hz', 8, TRUE,  'gap');

INSERT INTO cpu_sparse (time, host, region, cores, throttled) VALUES
    (1713658320000, 'server-01', 'cn-hz', 8, FALSE);

-- 3-8  预测样本（线性预测）
INSERT INTO meter (time, device, value) VALUES
    (1713657600000, 'm1', 12.0),
    (1713657660000, 'm1', 13.8),
    (1713657720000, 'm1', 15.6),
    (1713657780000, 'm1', 17.4),
    (1713657840000, 'm1', 19.2),
    (1713657900000, 'm1', 21.0),
    (1713657960000, 'm1', 22.8),
    (1713658020000, 'm1', 24.6),
    (1713658080000, 'm1', 26.4),
    (1713658140000, 'm1', 28.2),
    (1713658200000, 'm1', 30.0),
    (1713658260000, 'm1', 31.8),
    (1713658320000, 'm1', 33.6),
    (1713658380000, 'm1', 35.4),
    (1713658440000, 'm1', 37.2),
    (1713658500000, 'm1', 39.0),
    (1713658560000, 'm1', 40.8),
    (1713658620000, 'm1', 42.6),
    (1713658680000, 'm1', 44.4),
    (1713658740000, 'm1', 46.2);

-- 3-9  周期性预测样本（Holt-Winters）
INSERT INTO meter_seasonal (time, device, value) VALUES
    (1713657600000, 'm1', 10.0),
    (1713658500000, 'm1', 20.0),
    (1713659400000, 'm1', 30.0),
    (1713660300000, 'm1', 20.0),
    (1713661200000, 'm1', 10.0),
    (1713662100000, 'm1', 20.0),
    (1713663000000, 'm1', 30.0),
    (1713663900000, 'm1', 20.0),
    (1713664800000, 'm1', 10.0),
    (1713665700000, 'm1', 20.0),
    (1713666600000, 'm1', 30.0),
    (1713667500000, 'm1', 20.0),
    (1713668400000, 'm1', 10.0),
    (1713669300000, 'm1', 20.0),
    (1713670200000, 'm1', 30.0),
    (1713671100000, 'm1', 20.0),
    (1713672000000, 'm1', 10.0),
    (1713672900000, 'm1', 20.0),
    (1713673800000, 'm1', 30.0),
    (1713674700000, 'm1', 20.0),
    (1713675600000, 'm1', 10.0),
    (1713676500000, 'm1', 20.0),
    (1713677400000, 'm1', 30.0),
    (1713678300000, 'm1', 20.0);

-- 3-10  异常检测样本
INSERT INTO cpu_anomaly (time, host, usage) VALUES
    (1713657600000, 'edge-01', 10),
    (1713657660000, 'edge-01', 11),
    (1713657720000, 'edge-01', 9),
    (1713657780000, 'edge-01', 10),
    (1713657840000, 'edge-01', 12),
    (1713657900000, 'edge-01', 100),
    (1713657960000, 'edge-01', 11),
    (1713658020000, 'edge-01', 9);

-- 3-11  变点检测样本
INSERT INTO cpu_shift (time, host, value) VALUES
    (1713657600000, 'edge-01', 10.0),
    (1713657660000, 'edge-01', 10.2),
    (1713657720000, 'edge-01', 9.8),
    (1713657780000, 'edge-01', 10.1),
    (1713657840000, 'edge-01', 10.0),
    (1713657900000, 'edge-01', 9.9),
    (1713657960000, 'edge-01', 10.3),
    (1713658020000, 'edge-01', 10.0),
    (1713658080000, 'edge-01', 9.7),
    (1713658140000, 'edge-01', 10.1),
    (1713658200000, 'edge-01', 20.0),
    (1713658260000, 'edge-01', 20.1),
    (1713658320000, 'edge-01', 19.9),
    (1713658380000, 'edge-01', 20.2),
    (1713658440000, 'edge-01', 20.0),
    (1713658500000, 'edge-01', 19.8),
    (1713658560000, 'edge-01', 20.1),
    (1713658620000, 'edge-01', 19.9),
    (1713658680000, 'edge-01', 20.0),
    (1713658740000, 'edge-01', 20.1);

-- 3-12  HNSW 索引向量样本
INSERT INTO doc_indexed (time, source, title, embedding) VALUES
    (1713657610000, 'wiki',  '时序数据库简介',     [0.10, 0.20, 0.30, 0.40]),
    (1713657611000, 'wiki',  '向量索引调优',       [0.13, 0.19, 0.31, 0.37]),
    (1713657612000, 'wiki',  '向量检索原理',       [0.80, 0.10, 0.05, 0.05]),
    (1713657613000, 'blog',  'SonnetDB 快速入门',  [0.11, 0.21, 0.29, 0.39]),
    (1713657614000, 'blog',  '监控系统搭建实践',   [0.50, 0.50, 0.00, 0.00]),
    (1713657615000, 'paper', 'PID 控制律综述',     [0.90, 0.05, 0.03, 0.02]),
    (1713657616000, 'paper', '工业物联网数据采集', [0.12, 0.22, 0.28, 0.38]),
    (1713657617000, 'wiki',  '观测性告警手册',     [0.20, 0.30, 0.25, 0.35]);

-- 3-13  车辆轨迹数据（多车辆、多城市路线，用于地图轨迹测试）
-- 含 GEOPOINT 字段，地理空间查询样例见第十部分；建表须在写入前完成。
CREATE MEASUREMENT vehicle (
    device   TAG,
    position FIELD GEOPOINT,
    speed    FIELD FLOAT
);

INSERT INTO vehicle (time, device, position, speed) VALUES
    (1713657600000, 'car-1', POINT(31.2989, 120.5853), 0),
    (1713657660000, 'car-1', POINT(31.3856, 120.9810), 35),
    (1713657720000, 'car-1', POINT(31.1139, 121.3420), 42),
    (1713657780000, 'car-1', POINT(31.2210, 121.5440), 38),
    (1713657840000, 'car-1', POINT(30.7465, 120.7555), 50),
    (1713657900000, 'car-1', POINT(30.2741, 120.1551), 45),
    (1713657960000, 'car-1', POINT(29.8683, 121.5440), 52),
    (1713658020000, 'car-1', POINT(31.2304, 121.4737), 0),
    (1713657600000, 'car-2', POINT(31.2304, 121.4737), 0),
    (1713657720000, 'car-2', POINT(31.2346, 121.4903), 12),
    (1713657840000, 'car-2', POINT(31.2399, 121.5012), 18),
    (1713657960000, 'car-2', POINT(31.2485, 121.4712), 22),
    (1713658080000, 'car-2', POINT(31.2579, 121.4347), 28),
    (1713658200000, 'car-2', POINT(31.2281, 121.4099), 14),
    (1713658320000, 'car-2', POINT(31.2125, 121.4444), 10),
    (1713658440000, 'car-2', POINT(31.2208, 121.4875), 0),
    (1713657600000, 'truck-1', POINT(22.5431, 114.0579), 0),
    (1713657960000, 'truck-1', POINT(22.5900, 114.0850), 18),
    (1713658320000, 'truck-1', POINT(22.8150, 113.2800), 40),
    (1713658680000, 'truck-1', POINT(23.1291, 113.2644), 32),
    (1713659040000, 'truck-1', POINT(23.0215, 113.1214), 28),
    (1713659400000, 'truck-1', POINT(22.2707, 113.5767), 38),
    (1713659760000, 'truck-1', POINT(22.5431, 114.0579), 0);

-- 3-14  慢查询样本
INSERT INTO slow_query_log (time, db, latency_ms, rows_scanned) VALUES
    (1713657600000, 'alpha', 120, 1000),
    (1713657780000, 'alpha', 260, 2400),
    (1713657960000, 'alpha', 420, 4200),
    (1713658140000, 'beta',  180, 1600);


-- ============================================================
-- 第四部分：基础查询
-- ============================================================

-- 4-1  查询所有列（按时间升序）
SELECT * FROM cpu WHERE host = 'server-01';

-- 4-2  指定列投影 + 时间范围过滤
SELECT time, host, usage
FROM cpu
WHERE host = 'server-01'
  AND time >= 1713657600000
  AND time <  1713658200000;

-- 4-3  标量函数：abs / round / sqrt / log / coalesce
SELECT
    abs(usage - 0.5)            AS deviation,
    round(usage * 100, 1)       AS usage_pct,
    sqrt(cores)                 AS sqrt_cores,
    log(cores, 2)               AS log2_cores,
    coalesce(label, 'unknown')  AS safe_label
FROM cpu
WHERE host = 'server-01';

-- 4-4  分页查询（LIMIT / OFFSET 风格）
SELECT time, host, usage
FROM cpu
WHERE host = 'server-01'
LIMIT 5 OFFSET 0;

-- 4-5  分页查询（SQL 标准 FETCH 风格）
SELECT time, host, usage
FROM cpu
WHERE host = 'server-01'
OFFSET 5 ROWS FETCH NEXT 5 ROWS ONLY;

-- 4-6  多 tag 过滤（server-02）
SELECT * FROM cpu WHERE host = 'server-02' AND region = 'cn-sh';

-- 4-7  投影中直接算术表达式
SELECT time, usage, usage * 100 AS usage_pct, cores + 2 AS adjusted_cores
FROM cpu WHERE host = 'server-01';

-- 4-8  一元负号表达式
SELECT time, -usage AS neg_usage
FROM cpu WHERE host = 'server-01';

-- 4-9  标量函数嵌套调用
SELECT time, round(abs(usage - 0.5), 2) AS rounded_deviation
FROM cpu WHERE host = 'server-01';


-- ============================================================
-- 第五部分：聚合查询
-- ============================================================

-- 5-1  基础聚合（count / sum / min / max / avg / first / last）
SELECT
    count(usage)  AS cnt,
    sum(usage)    AS total,
    min(usage)    AS min_usage,
    max(usage)    AS max_usage,
    avg(usage)    AS avg_usage,
    first(usage)  AS first_usage,
    last(usage)   AS last_usage
FROM cpu
WHERE host = 'server-01';

-- 5-2  count(*)
SELECT count(*) FROM cpu WHERE host = 'server-01';

-- 5-3  扩展聚合（stddev / variance / spread / median / mode）
SELECT
    stddev(usage)   AS std,
    variance(usage) AS var,
    spread(usage)   AS spread,
    median(usage)   AS median,
    mode(usage)     AS mode
FROM cpu
WHERE host = 'server-01';

-- 5-4  T-Digest 分位数聚合
SELECT
    percentile(usage, 50)  AS p50,
    percentile(usage, 90)  AS p90,
    percentile(usage, 95)  AS p95,
    percentile(usage, 99)  AS p99,
    p50(usage)             AS p50_alias,
    p90(usage)             AS p90_alias,
    p95(usage)             AS p95_alias,
    p99(usage)             AS p99_alias,
    distinct_count(usage)  AS distinct_cnt
FROM cpu
WHERE host = 'server-01';

-- 5-5  直方图聚合（将 usage 分成 5 个桶）
SELECT histogram(usage, 5) AS hist
FROM cpu
WHERE host = 'server-01';

-- 5-6  GROUP BY time(2m) —— 每 2 分钟一个时间桶
SELECT
    avg(usage)   AS avg_usage,
    max(usage)   AS max_usage,
    count(usage) AS cnt
FROM cpu
WHERE host = 'server-01'
GROUP BY time(2m);

-- 5-7  GROUP BY time(1m) —— 每 1 分钟聚合内存使用率
SELECT
    avg(used)  AS avg_used,
    max(used)  AS peak_used,
    min(used)  AS min_used
FROM mem
WHERE host = 'server-01'
GROUP BY time(1m);

-- 5-8  向量质心聚合（centroid，各维度算术平均）
SELECT centroid(embedding) AS dim_avg FROM documents;

-- 5-9  T-Digest 内部状态（返回 JSON：压缩参数、质心列表、计数）
SELECT tdigest_agg(usage) AS tdigest_state FROM cpu WHERE host = 'server-01';


-- ============================================================
-- 第六部分：窗口函数（行级，不改变行数）
-- ============================================================

-- 6-1  差分 / 变化量
SELECT time, difference(usage) AS diff_usage
FROM cpu
WHERE host = 'server-01';

SELECT time, delta(usage) AS delta_usage
FROM cpu
WHERE host = 'server-01';

-- 6-2  变化率（每秒）
SELECT time, derivative(usage) AS rate_per_sec
FROM cpu
WHERE host = 'server-01';

SELECT time, non_negative_derivative(usage) AS nn_rate
FROM cpu
WHERE host = 'server-01';

-- 6-3  累积求和
SELECT time, cumulative_sum(usage) AS cumsum
FROM cpu
WHERE host = 'server-01';

-- 6-4  移动平均（窗口 = 3 个点）
SELECT time, moving_average(usage, 3) AS ma3
FROM cpu
WHERE host = 'server-01';

-- 6-5  指数加权移动平均（α = 0.3）
SELECT time, ewma(usage, 0.3) AS ewma_usage
FROM cpu
WHERE host = 'server-01';

-- 6-6  状态变化检测（throttled 字段）
SELECT time, state_changes(throttled) AS changed
FROM cpu
WHERE host = 'server-01';

-- 6-7  状态持续时长（throttled = TRUE 的持续毫秒数）
SELECT time, state_duration(throttled) AS duration_ms
FROM cpu
WHERE host = 'server-01';

-- 6-8  计时器型变化率（rate 与 irate，同 non_negative_derivative 和 derivative）
SELECT time, rate(usage) AS rate_sec, irate(usage) AS irate_sec
FROM cpu WHERE host = 'server-01';

-- 6-9  计数器增量（increase = max(0, diff)，抑制计数器回零毛刺）
SELECT time, increase(usage) AS incr
FROM cpu WHERE host = 'server-01';

-- 6-10 梯形累积积分（integral，默认时间单位 1 秒）
SELECT time, integral(usage) AS integral_area
FROM cpu WHERE host = 'server-01';

-- 6-11 双指数平滑（Holt 加法，alpha=0.5 水平平滑，beta=0.1 趋势平滑）
SELECT time, holt_winters(temperature, 0.5, 0.1) AS smoothed
FROM reactor WHERE device = 'r1';

-- 6-12 空值线性插值（interpolate，相邻非空值间线性填充）
SELECT
    time,
    coalesce(label, 'unknown') AS label,
    fill(usage, -1) AS filled,
    locf(usage) AS locf_value,
    interpolate(usage) AS interpolated
FROM cpu_sparse
WHERE host = 'server-01';

-- 6-13 空值常量填充（fill，将 NULL 替换为指定常量值）
SELECT time, fill(usage, -1) AS filled
FROM cpu_sparse WHERE host = 'server-01';

-- 6-14 最后观测值前向填充（locf，NULL 处复用上一个非空值）
SELECT time, locf(usage) AS locf_filled
FROM cpu_sparse WHERE host = 'server-01';


-- ============================================================
-- 第七部分：PID 控制律
-- ============================================================

-- 7-1  行级 PID 窗口函数（pid_series）
--      目标温度 75.0，Kp=0.6，Ki=0.1，Kd=0.05
SELECT
    time,
    temperature,
    pid_series(temperature, 75.0, 0.6, 0.1, 0.05) AS valve
FROM reactor
WHERE device = 'r1';

-- 7-2  桶级 PID 聚合（pid + GROUP BY time）
--      每 30 秒桶输出桶末控制量
SELECT
    pid(temperature, 75.0, 0.6, 0.1, 0.05) AS valve_agg
FROM reactor
WHERE device = 'r1'
GROUP BY time(30s);

-- 7-3  阶跃响应自动整定（pid_estimate）
--      使用 IMC 方法，阶跃幅度 1.0，首尾各取 10% 样本估计稳态
SELECT
    pid_estimate(temperature, 'imc', 1.0, 0.1, 0.1, NULL) AS tuning_json
FROM reactor
WHERE device = 'r1'
  AND time >= 1713657600000
  AND time <  1713657800000;

-- 7-4  Ziegler-Nichols 整定
SELECT
    pid_estimate(temperature, 'zn', 1.0, 0.1, 0.1, NULL) AS tuning_zn
FROM reactor
WHERE device = 'r1';

-- 7-5  Cohen-Coon 整定
SELECT
    pid_estimate(temperature, 'cc', 1.0, 0.1, 0.1, NULL) AS tuning_cc
FROM reactor
WHERE device = 'r1';


-- ============================================================
-- 第八部分：预测 / 异常检测 / 变点检测
-- ============================================================

-- 8-1  线性外推未来 5 步（forecast TVF）
SELECT *
FROM forecast(meter, value, 5, 'linear')
WHERE device = 'm1';

-- 8-2  Holt-Winters 预测未来 6 步（无季节项）
SELECT *
FROM forecast(reactor, temperature, 6, 'holt_winters')
WHERE device = 'r1';

-- 8-3  Holt-Winters 带季节项（季节周期 = 4 个采样点）
SELECT *
FROM forecast(meter_seasonal, value, 6, 'holt_winters', 4)
WHERE device = 'm1';

-- 8-4  异常检测 —— Z-Score 方法（阈值 2.0）
SELECT
    time,
    usage,
    anomaly(usage, 'zscore', 2.0) AS is_outlier_zscore
FROM cpu_anomaly
WHERE host = 'edge-01';

-- 8-5  异常检测 —— MAD 方法（推荐，鲁棒性更强）
SELECT
    time,
    usage,
    anomaly(usage, 'mad', 2.5) AS is_outlier_mad
FROM cpu_anomaly
WHERE host = 'edge-01';

-- 8-6  异常检测 —— IQR 方法（Tukey 箱线图风格）
SELECT
    time,
    usage,
    anomaly(usage, 'iqr', 1.5) AS is_outlier_iqr
FROM cpu_anomaly
WHERE host = 'edge-01';

-- 8-7  变点检测 —— CUSUM（阈值 4.0，漂移容忍 0.5）
SELECT
    time,
    value,
    changepoint(value, 'cusum', 4.0) AS shift_detected
FROM cpu_shift
WHERE host = 'edge-01';

-- 8-8  变点检测 —— 更保守的阈值（5.0）
SELECT
    time,
    value,
    changepoint(value, 'cusum', 5.0, 0.5) AS shift_conservative
FROM cpu_shift
WHERE host = 'edge-01';


-- ============================================================
-- 第九部分：向量检索（KNN 表值函数）
-- ============================================================

-- 9-1  余弦距离 KNN，查询最近 3 条（默认 cosine）
SELECT *
FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 3);

-- 9-1b HNSW 索引表的向量检索
SELECT *
FROM knn(doc_indexed, embedding, [0.10, 0.20, 0.30, 0.40], 3);

-- 9-2  L2 欧几里得距离 KNN
SELECT *
FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 3, 'l2');

-- 9-3  内积（负内积）距离 KNN
SELECT *
FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 3, 'inner_product');

-- 9-4  带 tag 过滤：只在 source='wiki' 中检索
SELECT *
FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 5, 'cosine')
WHERE source = 'wiki';

-- 9-5  带时间范围过滤
SELECT *
FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 5)
WHERE time >= 1713657600000 AND time < 1713657605000;

-- 9-6  标量向量函数（在普通 SELECT 中使用）
SELECT
    cosine_distance(embedding, [0.10, 0.20, 0.30, 0.40]) AS cos_dist,
    l2_distance(embedding, [0.10, 0.20, 0.30, 0.40])     AS l2_dist,
    inner_product(embedding, [0.10, 0.20, 0.30, 0.40])   AS dot_prod,
    vector_norm(embedding)                                AS norm
FROM documents
WHERE source = 'wiki';

-- 9-7  pgvector 兼容运算符语法（<=> 余弦距离, <-> L2 距离, <#> 负内积）
SELECT embedding <=> [0.10, 0.20, 0.30, 0.40] AS cos_dist
FROM documents WHERE source = 'wiki';

SELECT embedding <-> [0.10, 0.20, 0.30, 0.40] AS l2_dist
FROM documents WHERE source = 'wiki';

SELECT embedding <#> [0.10, 0.20, 0.30, 0.40] AS neg_inner_product
FROM documents WHERE source = 'wiki';


-- ============================================================
-- 第十部分：地理空间查询（GEOPOINT）
-- 支持 GEOPOINT 数据类型、POINT(lat, lon) 字面量、
-- 球面距离/方位/范围判断、空间索引（GeoHash32）过滤、
-- 轨迹聚合（长度、质心、包络矩形、速度分位数）
-- ============================================================

-- 10-1  车辆轨迹表 vehicle 已在第三部分（3-13 前）创建并写入数据，
--       这里直接使用，不再重复建表。

-- 10-2  车辆轨迹数据已在第三部分统一写入，这里保留后续地图查询样例。

-- 10-3  提取经纬度标量（lat / lon 函数将 GEOPOINT 拆为两个 FLOAT）
SELECT time, device,
       lat(position) AS latitude,
       lon(position) AS longitude
FROM vehicle WHERE device = 'car-1';

-- 10-4  球面距离与方位角（geo_distance 返回米，geo_bearing 返回度）
SELECT
    geo_distance(
        POINT(39.9042, 116.4074),
        POINT(31.2304, 121.4737)
    ) AS beijing_to_shanghai_m,
    geo_bearing(
        POINT(39.9042, 116.4074),
        POINT(31.2304, 121.4737)
    ) AS bearing_deg
FROM vehicle WHERE device = 'car-1'
LIMIT 1;

-- 10-4b 坐标系转换函数（WGS84 / GCJ-02 / BD-09）
SELECT
    geo_wgs84_to_gcj02(position) AS gcj02_position,
    geo_gcj02_to_wgs84(geo_wgs84_to_gcj02(position)) AS roundtrip_wgs84,
    geo_wgs84_to_bd09(position) AS bd09_position,
    geo_bd09_to_wgs84(geo_wgs84_to_bd09(position)) AS roundtrip_wgs84_from_bd09
FROM vehicle
WHERE device = 'car-1'
LIMIT 1;

-- 10-4c 通用坐标系转换接口（支持 GPS / AMap / Tencent / Baidu 别名）
SELECT
    geo_transform(position, 'gps', 'gcj02') AS gps_to_gcj02,
    geo_transform(position, 'gcj02', 'wgs84') AS gcj02_to_wgs84,
    geo_transform(position, 'wgs84', 'baidu') AS wgs84_to_bd09
FROM vehicle
WHERE device = 'car-1'
LIMIT 1;

-- 10-5  ST_ 兼容别名（ST_Distance / ST_Within / ST_DWithin，方便从 PostGIS 迁移）
SELECT
    ST_Distance(position, POINT(39.9042, 116.4074)) AS dist_from_beijing_m,
    ST_Within(position, 31.2304, 121.4737, 50000)   AS near_shanghai_50km,
    ST_DWithin(position, 31.2304, 121.4737, 50000)  AS dwithin_shanghai_50km
FROM vehicle WHERE device = 'car-1';

-- 10-6  瞬时速度估算（geo_speed 根据前后两点经纬度差 ÷ 时间差算出 m/s）
SELECT geo_speed(
    POINT(31.2989, 120.5853),
    POINT(31.3856, 120.9810),
    300000
) AS speed_ms
FROM vehicle
WHERE device = 'car-1'
LIMIT 1;

-- 10-7  geo_within 空间范围过滤 —— 查找上海中心 50 km 半径内的轨迹点
SELECT time, device,
       lat(position) AS lat,
       lon(position) AS lon
FROM vehicle
WHERE geo_within(position, 31.2304, 121.4737, 50000);

-- 10-8  geo_bbox 矩形范围过滤 —— 查找华东区域（lat∈[30,35], lon∈[113,122]）内的轨迹点
SELECT time, device,
       lat(position) AS lat,
       lon(position) AS lon
FROM vehicle
WHERE geo_bbox(position, 30.0, 113.0, 35.0, 122.0);

-- 10-9  轨迹聚合 —— 整条轨迹累积球面距离（米）
SELECT device,
       trajectory_length(position) AS total_length_m
FROM vehicle WHERE device = 'car-1';

-- 10-10 轨迹聚合 —— 轨迹质心（返回 GEOPOINT，各点经纬度算术平均）
SELECT device,
       trajectory_centroid(position) AS centroid
FROM vehicle WHERE device = 'car-2';

-- 10-11 轨迹聚合 —— 轨迹包络矩形（返回 JSON：min_lat/min_lon/max_lat/max_lon）
SELECT device,
       trajectory_bbox(position) AS bbox_json
FROM vehicle WHERE device = 'car-2';

-- 10-12 轨迹速度统计 —— 最大 / 平均 / P95 速度（m/s）
SELECT
    trajectory_speed_max(position, time) AS speed_max_ms,
    trajectory_speed_avg(position, time) AS speed_avg_ms,
    trajectory_speed_p95(position, time) AS speed_p95_ms
FROM vehicle WHERE device = 'car-1';

-- 10-13 按时间桶分组轨迹聚合（每 2 分钟一段子轨迹）
SELECT
    trajectory_length(position)      AS seg_length_m,
    trajectory_speed_avg(position, time) AS seg_avg_speed_ms
FROM vehicle
WHERE device = 'car-1'
GROUP BY time(2m);

-- 10-14 空间过滤 + 轨迹聚合联用：只统计华东区域内的轨迹长度
SELECT trajectory_length(position) AS length_in_east_china_m
FROM vehicle
WHERE geo_bbox(position, 30.0, 113.0, 35.0, 122.0);

-- 10-15 SQL Console 地图预览测试：position 需可读、地图可切换瓦片服务商
SELECT time, device, position, speed
FROM vehicle
LIMIT 100;


-- ============================================================
-- 第十一部分：元数据查询
-- ============================================================

-- 11-1  列出所有 measurement
SHOW MEASUREMENTS;
SHOW TABLES;

-- 11-2  描述表结构
DESCRIBE MEASUREMENT cpu;
DESCRIBE MEASUREMENT cpu_sparse;
DESCRIBE MEASUREMENT mem;
DESCRIBE MEASUREMENT reactor;
DESCRIBE MEASUREMENT signal;
DESCRIBE MEASUREMENT meter;
DESCRIBE MEASUREMENT meter_seasonal;
DESCRIBE MEASUREMENT cpu_anomaly;
DESCRIBE MEASUREMENT cpu_shift;
DESCRIBE MEASUREMENT documents;
DESCRIBE MEASUREMENT doc_indexed;
DESCRIBE MEASUREMENT vehicle;
DESCRIBE MEASUREMENT slow_query_log;

-- 11-3  查看用户与授权（控制面）
SHOW USERS;
SHOW GRANTS;
SHOW GRANTS FOR writer;
SHOW TOKENS FOR writer;

-- 11-4  运维诊断：慢查询计划
EXPLAIN SELECT latency_ms
FROM slow_query_log
WHERE db = 'alpha'
  AND time >= 1713657600000
  AND time <  1713658500000;


-- ============================================================
-- 第十二部分：删除演示（DELETE）
-- ============================================================

-- 12-1  按时间范围删除（tombstone 机制，不原地改写）
DELETE FROM cpu
WHERE host = 'server-01'
  AND time >= 1713658080000
  AND time <= 1713658140000;

-- 12-2  验证删除效果
SELECT * FROM cpu WHERE host = 'server-01';

-- 12-3  按 tag 删除整个序列
DELETE FROM signal WHERE source = 's-1';

-- 12-4  验证删除效果
SELECT * FROM signal WHERE source = 's-1';


-- ============================================================
-- 第十三部分：清理（可选，演示结束后执行）
-- ============================================================

-- 13-1  吊销 writer 的 Token（需先从 SHOW TOKENS FOR writer 获取 token_id）
-- REVOKE TOKEN 'tok_xxxxxx';

-- 13-2  修改用户密码
ALTER USER viewer WITH PASSWORD 'newviewer999';

-- 13-3  撤销授权
REVOKE ON DATABASE demo FROM viewer;

-- 13-4  删除用户
DROP USER viewer;
DROP USER writer;

-- 13-5  删除数据库（不可逆，谨慎执行）
-- DROP DATABASE demo;


-- ============================================================
-- END OF DEMO
-- ============================================================
