# 5.0 Parity full 镜像构建磁盘峰值修复

日期：2026-11-04（Asia/Shanghai）。本报告对应 `29f206fc9238919721f314a7763a66a835cab842` 之后、尚未提交的修复工作树；最终发布候选 SHA 由统一集成流程确定。本次只处理 Parity hosted job 的构建空间峰值，不表示新的候选已经取得远端 full PASS。

## 已确认的失败

- [Parity run 41632722292 / full job 125906104134](https://github.com/IoTSharp/SonnetDB/actions/runs/41632722292/job/125906104134) 在 `Start parity stack` 构建 SonnetDB 镜像的最终导出/解压阶段失败。
- 原始日志 `D:/source/SonnetDB/artifacts/release-5.0-20261103-pipeline/new-29f206fc/parity-failure/full-job.log` 第 2700、2704 行显示：提取 `sha256:edb19faf3fcc6faa779074e6ec5e80d2099fed66fbb76dcc75daa5a4f9a76286` 时，向 `/var/lib/containerd/io.containerd.snapshotter.v1.overlayfs/snapshots/250/fs/app/docs/design/m47-unified-management-workbench/screenshots/desktop-1280.png` 写入触发 `no space left on device`。
- 该失败属于 `stack_start_failed`；SQL/reference runner 未执行，不能记为 SQL 对照失败。原日志第 2726 行的 reliability 为 **84 PASS / 0 FAIL / 0 SKIP**，只关闭该次 CrashTests 门禁。主流水线归档的 light success 与本次 full 失败分别保留。
- 现有 `compose up --build` 允许本地 SonnetDB、MinIO 构建缓存/导出空间与完整参考镜像拉取同时占用磁盘。之前候选的 full 成功不证明本次候选具有稳定的空间余量。

## 最小变更

修改 `.github/workflows/parity.yml`，新增 `eng/prepare-parity-images.sh` 与 `eng/test-parity-image-preparation.sh`：

1. 先按 `sonnetdb`、`minio` 顺序构建两个本地 target。每个 target 单独使用 `docker-container` BuildKit builder，`buildx bake --file docker-compose.parity.yml --load <target>` 读取原 Compose 的 context、Dockerfile 和 image tag。
2. 每个 target 加载后，使用原 profile 的 `compose config --images <service>` 与 `docker image inspect` 确认 Compose 所需镜像已在本地，再移除该 target 的专属 builder/cache。
3. 两个 builder 均成功回收后，工作流执行原服务列表的 `compose up -d --no-build`，再执行原就绪探针、Parity、reliability、raw 汇总、上传与结果门禁。
4. 在任何真实构建前执行新的轻量合同，验证成功和失败路径。合同使用模拟 Docker，不拉镜像、不构建产品、不启动参考服务。

保留 light 的 5 个显式服务、full 的 12 个显式服务，以及全部 10 个必需 reference 报告门禁。full 服务列表仍为 `sonnetdb postgres redis minio nats influxdb victoriametrics mosquitto meilisearch qdrant clickhouse mongodb`；未启动的 `harness` 定义仍由宿主 `dotnet test` 路径替代。未修改 Compose、SonnetDB/MinIO Dockerfile、参考镜像版本、测试数据规模、原报告/汇总合同或 raw gate。

## 资源归属和有界清理

- Builder 名称为 `sonnetdb-parity-<GITHUB_RUN_ID>-<GITHUB_RUN_ATTEMPT>-<light|full>-<sonnetdb|minio>`。run ID/attempt 必须为数字，profile 只接受既有两个值。
- 创建前先 inspect；同名已存在时直接失败，不认领、不删除。只有本进程 `buildx create` 成功后才设置 owned builder。创建失败或超时时明确保留“归属未确认”，不猜测清理。
- 正常、构建失败、镜像加载检查失败和 TERM/INT 路径，只对上述已认领的精确 builder 执行一次 `buildx rm --force`，不使用 `--keep-state`。删除对象是该 builder 及其私有 BuildKit cache/volume。清理失败会阻断后续 reference 拉取，并保留失败；不盲目重试未知删除结果。
- 没有执行全局 Docker prune、共享缓存清空、预装 SDK/`/opt/hostedtoolcache` 删除或用户目录清理。已加载的最终镜像保留给原 Compose 使用。
- 配置目录由 `mktemp -d` 在 runner 临时目录内新建；只删除本任务的两个精确文件后 `rmdir`，不递归清理共享目录。
- 两项构建共享 **1200 秒**准备期限；每项 build 最多 **900 秒**，inspect/config/image 检查最多 30 秒、create 最多 60 秒。builder 清理另有最多 60 秒和 10 秒终止宽限。没有无界重试。
- BuildKit OCI 并发最多 2，builder 的 CPU quota 为 2 CPU。每个长命令记录 PID、父 PID、启动时间、argv 和 timeout；取消只终止本进程仍在等待的 timeout 子进程，由其转发到所属命令组。
- `image-preparation-capacity.txt` 记录构建前、加载后和清理后的 `df -h` / `docker system df`；`image-preparation-processes.txt` 记录命令身份。二者位于原 `stack-diagnostics` 上传路径。

强制终止整个 hosted VM 无法由脚本完成本地 finally；正常取消/超时由 trap 处理，VM 最终销毁仍归 hosted runner 生命周期。没有建立跨 job 的共享 builder 或持久缓存。

## 本机验证

| 检查 | 结果 | 证据与边界 |
| --- | --- | --- |
| 真实 `docker buildx bake --print --file docker-compose.parity.yml sonnetdb minio` | PASS | 只展开配置，无 build/pull。SonnetDB context 为 `../..`、Dockerfile 为 `src/SonnetDB/Dockerfile`、tag 为 `iotsharp/sonnetdb:parity`；MinIO context 为 `docker/minio`、tag 为 `sonnetdb-parity-minio:RELEASE.2024-09-22T00-33-43Z`。 |
| 真实 full `docker compose ... config --format json`，两个单服务 `config --images` | PASS | context/Dockerfile/tag 与 Bake 一致；full 保留原 13 个定义（含不启动的 harness）及原显式 12 服务集合。 |
| Linux Bash 语法检查 | PASS | 对两个新脚本分别执行 `bash -n`。 |
| Linux 编排/归属合同 | PASS，8 场景 | light/full 成功时的单 target build→load→rm 顺序；同名 builder 拒绝；create 失败不认领；build 失败清理；缺失 loaded image 清理；cleanup 失败阻断下一项；TERM 取消清理。另确认 foreign fixture 保留与私有配置目录回收。 |
| 新工作流 YAML、UTF-8/LF、限定 diff/空白检查 | PASS | YAML 可解析；三个源文件均严格 UTF-8、LF；`git diff --check` 通过。 |
| 本机完整 SonnetDB/MinIO 镜像构建与 full 栈 | NOT_RUN | 按授权边界避免再次构建完整栈；没有本机真实磁盘峰值/节省 GB 数值。 |
| 最终候选远端 light/full/raw gate | NOT_RUN | 必须由统一集成后的精确 SHA 重跑，不能继承 `29f206fc` 或旧候选结果。 |
| 完整 solution restore / Format / 提交 | NOT_RUN（本 worker） | 其他 worker 正在共用 bin/obj，且 Git/index 由主流程独占；主流程须在最终工作树执行仓库规定的 restore + Format。 |

Linux 合同使用已安装的 `postgres:15.1`，冻结镜像 ID 为 `sha256:02547253a07e6edd0c070caba1d2a019b7dc7df98b948dc9a909e1808eb77024`（linux/amd64），仅作为 Bash/coreutils 载体。通过 `--pull never --network none --cpus 1 --memory 256m --read-only` 运行，四个必要文件复制到独占 staging 后只读挂载，写入仅位于该容器的 `/tmp` tmpfs；未挂载仓库、密钥或 Docker socket。saved PowerShell 7 domain 入口使用结构化 argv，90 秒等待，try/finally 在核对精确 container ID 和独占 label 后删除容器，再核对 staging 绝对路径并回收。

最终合同容器 `d7d28df85c4cbff6652285b69306f67068fbea1d4b7497167c24ef2a12fff090` 已确认删除；按本任务 label 查询没有遗留容器，staging 已回收，Cordonix 报告 `cleanup_complete=true`、`active_processes_after_cleanup=0`。最终回执位于 `artifacts/release-5.0-20261104-parity-capacity/linux-contract-result.json`，包含输入 SHA256、镜像身份、容器身份及清理结果。

保留的执行失败：首次 Linux 夹具因 tmpfs 默认 noexec，模拟 Docker 执行返回 126；改为仅该独占容器 `/tmp` 可执行后复验通过，原失败保留为 `linux-contract-failure-1.json`，其容器同样按 label 回收。此前宿主 Bash/WSL 被 Cordonix 的直接执行 guard 拒绝；临时 PS→宿主 Bash 入口随后停用并清理，其结果未计入 Linux PASS 清单。未修改 Cordonix 或安装宿主工具。

## 集成顺序及剩余边界

1. 主流程合并此修复与其他独立 worker 的改动，更新根 HANDOFF/CHANGELOG；本 worker 未写这些共享文件、Git index、分支、tag 或远端。
2. 最终树完成规定 restore/Format、提交/推送；重新运行所有发布必需门禁。
3. 对同一新 SHA 检查 light/full：两个本地镜像加载与精确 builder 清理成功；所有 reference 启动；原 Parity/reliability/raw 门禁通过；容量诊断保留各阶段实际空闲空间。
4. 如果 full 仍然 ENOSPC，保留该 FAIL 与容量日志，继续诊断实际峰值；不能以缩小 profile、跳过参考端、删除 SDK 或替换 SQL 对照证据放行。

此修复降低构建缓存与 reference 镜像的同时驻留量；实际空间余量和新 builder 冷构建所需时间尚需最终 hosted full 验证。长期/固定硬件、连续 scheduled 证据、生产或物理设备验收均不由本合同推出。
