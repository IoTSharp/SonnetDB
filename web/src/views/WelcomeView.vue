<template>
  <div class="welcome-page">
    <header class="hero-header">
      <div class="brand-lockup" aria-label="SonnetDB">
        <div class="brand-mark" aria-hidden="true">
          <span class="brand-mark-core" />
        </div>
        <div class="brand-copy">
          <span class="brand-name">SonnetDB</span>
          <span class="brand-tagline">AI-native 多模型数据底座</span>
        </div>
      </div>
      <nav class="hero-nav" aria-label="页面导航">
        <button type="button" class="nav-link" @click="scrollToSection('overview')">产品概览</button>
        <button type="button" class="nav-link" @click="scrollToSection('database')">产品形态</button>
        <button type="button" class="nav-link" @click="scrollToSection('capabilities')">当前能力</button>
        <button type="button" class="nav-link" @click="scrollToSection('roadmap')">开发入口</button>
        <button type="button" class="nav-cta" @click="goManage">进入后台</button>
      </nav>
    </header>

    <main class="hero-main">
      <section id="overview" class="hero-panel">
        <div class="hero-copy">
          <div class="hero-eyebrow">产品定位</div>
          <h1>一套面向设备、业务、搜索和 AI 协作的多模型数据底座。</h1>
          <p class="hero-subtitle">
            SonnetDB 支持嵌入式和服务端两种运行方式，提供 SQL、ADO.NET、EF Core、CLI、HTTP API、Web Admin、MCP 和多语言连接器。
            当前版本已经覆盖时序、关系表、KV、文档、全文、向量、Hybrid Search、对象桶、消息队列和 Copilot 智能协作等能力。
          </p>

          <div class="hero-actions">
            <button type="button" class="primary-action" @click="scrollToSection('capabilities')">查看当前能力</button>
            <button type="button" class="secondary-action" @click="scrollToSection('database')">了解产品形态</button>
          </div>

          <div class="hero-badges" aria-label="产品要点">
            <article v-for="item in heroHighlights" :key="item.title" class="hero-badge">
              <span>{{ item.title }}</span>
              <strong>{{ item.description }}</strong>
            </article>
          </div>
        </div>

        <div class="hero-stage">
          <div class="stage-window">
            <div class="window-topbar" aria-hidden="true">
              <span class="window-dot" />
              <span class="window-dot" />
              <span class="window-dot" />
            </div>
            <div class="stage-grid">
              <section class="stage-card stage-card-large">
                <span class="stage-kicker">当前能力</span>
                <strong>从数据存储、SQL 查询到 AI 协作和管理后台，SonnetDB 提供一套可直接落地的数据平台能力。</strong>
                <p>
                  ADO.NET 提供程序通过 `SonnetDB` NuGet 包发布，源码项目和命名空间仍为 `SonnetDB.Data`。
                </p>
                <ul class="stage-list">
                  <li>时序数据支持 SQL 写入、范围查询、聚合、窗口函数和批量导入。</li>
                  <li>服务端内置用户、授权、Token、备份恢复、健康检查、MCP 和 Web Admin。</li>
                  <li>扩展能力覆盖 KV、关系表、JSON 文档、全文、向量、对象桶、消息队列和 Copilot。</li>
                </ul>
              </section>
              <section class="stage-card">
                <span class="stage-kicker">数据面</span>
                <strong>SQL 与批量写入</strong>
                <p>支持 INSERT、SELECT、DELETE、Line Protocol、JSON 和 bulk fast path。</p>
              </section>
              <section class="stage-card">
                <span class="stage-kicker">分析</span>
                <strong>窗口、预测与 PID</strong>
                <p>内置统计、分位数、速率、平滑、预测、异常检测和控制函数。</p>
              </section>
              <section class="stage-card stage-card-accent">
                <span class="stage-kicker">多模型</span>
                <strong>时空、搜索、对象与 AI</strong>
                <p>支持 GEOPOINT、轨迹分析、全文检索、向量检索、Hybrid Search、对象桶和 Copilot。</p>
              </section>
            </div>
          </div>
        </div>
      </section>

      <section id="database" class="section-panel">
        <div class="section-heading">
          <span class="hero-eyebrow">产品形态</span>
          <h2>嵌入式、服务端、工具链、连接器和管理后台共用同一套存储与 SQL 能力。</h2>
          <p>
            本地进程、远程服务、命令行、Web Admin 和客户端 SDK 可以按场景组合使用。
          </p>
        </div>

        <div class="info-grid">
          <article v-for="item in databaseCards" :key="item.title" class="info-card">
            <span class="info-kicker">{{ item.kicker }}</span>
            <h3>{{ item.title }}</h3>
            <p>{{ item.description }}</p>
          </article>
        </div>
      </section>

      <section id="capabilities" class="section-panel">
        <div class="section-heading">
          <span class="hero-eyebrow">当前能力</span>
          <h2>用简洁 SQL 和标准客户端访问时序、多模型、搜索、AI 和运维能力。</h2>
        </div>

        <div class="feature-grid">
          <article v-for="feature in capabilityCards" :key="feature.title" class="feature-card">
            <span class="feature-index">{{ feature.index }}</span>
            <h3>{{ feature.title }}</h3>
            <p>{{ feature.description }}</p>
          </article>
        </div>
      </section>

      <section id="roadmap" class="section-panel section-panel-tight">
        <div class="section-heading">
          <span class="hero-eyebrow">开发入口</span>
          <h2>从 NuGet、Docker、CLI 或管理后台开始接入 SonnetDB。</h2>
        </div>

        <div class="roadmap-grid">
          <article v-for="item in roadmapCards" :key="item.title" class="roadmap-card">
            <span class="roadmap-kicker">{{ item.milestone }}</span>
            <h3>{{ item.title }}</h3>
            <p>{{ item.description }}</p>
          </article>
        </div>
      </section>
    </main>
  </div>
</template>

<script setup lang="ts">
import { useRouter } from 'vue-router';
import { useAuthStore } from '../stores/auth';
import { useSetupStore } from '../stores/setup';

const router = useRouter();
const auth = useAuthStore();
const setup = useSetupStore();

const heroHighlights = [
  {
    title: 'NuGet',
    description: 'ADO.NET 安装包名为 SonnetDB，命名空间为 SonnetDB.Data',
  },
  {
    title: 'SQL',
    description: '覆盖时序写入查询、聚合、窗口、关系表和 EXPLAIN',
  },
  {
    title: '多模型',
    description: '提供 KV、关系表、文档、全文、向量、对象桶和消息队列',
  },
  {
    title: 'AI + 运维',
    description: '内置 Web Admin、Copilot、MCP、备份恢复和健康检查',
  },
];

const databaseCards = [
  {
    kicker: 'Embedded',
    title: '嵌入式引擎',
    description: '应用可以直接打开数据库目录，通过 Tsdb、SQL 执行器或 ADO.NET 在进程内访问。',
  },
  {
    kicker: 'Server',
    title: 'HTTP 服务端',
    description: '提供远程 SQL、批量写入、认证授权、控制面 API、事件流、MCP、健康检查和指标端点。',
  },
  {
    kicker: 'Client',
    title: '客户端与连接器',
    description: '支持 ADO.NET、CLI、HTTP API，以及 C、Go、Rust、Java、Python、VB6、PureBasic 等连接器。',
  },
  {
    kicker: 'Admin',
    title: 'SonnetDB Studio',
    description: '提供首次安装、数据库管理、Schema Explorer、SQL Editor、结果表格、图表、轨迹地图、写入审批和 Copilot 浮窗。',
  },
  {
    kicker: 'Package',
    title: 'NuGet 包名',
    description: 'ADO.NET 提供程序由 src/SonnetDB.Data 打包发布，但 NuGet 包名是 SonnetDB。',
  },
  {
    kicker: 'Deploy',
    title: '发布与部署',
    description: '提供 NuGet、Docker 镜像、SDK Bundle、Server Bundle、Windows MSI 和 Linux 安装包说明。',
  },
];

const capabilityCards = [
  {
    index: '01',
    title: '时序写入与查询',
    description: '支持 measurement、tag、field、time 建模，以及 SQL、Line Protocol、JSON 和批量写入。',
  },
  {
    index: '02',
    title: 'SQL 与关系表',
    description: '支持关系表、索引、约束、小事务、JOIN、子查询、聚合和查询计划解释。',
  },
  {
    index: '03',
    title: '分析函数',
    description: '覆盖基础聚合、分位数、直方图、去重统计、差分、速率、积分、补点和平滑处理。',
  },
  {
    index: '04',
    title: '预测、异常与 PID',
    description: '支持 forecast、anomaly、changepoint、pid、pid_series 和 pid_estimate 等内置函数。',
  },
  {
    index: '05',
    title: '地理空间与轨迹',
    description: '支持 GEOPOINT、距离、方位、围栏、速度、轨迹长度、重心、外包框和 GeoJSON 输出。',
  },
  {
    index: '06',
    title: 'KV、文档与搜索',
    description: '支持 KV keyspace、JSON 文档集合、JSON path、全文索引、向量 KNN、知识检索和 Hybrid Search。',
  },
  {
    index: '07',
    title: '对象桶与消息队列',
    description: '提供 S3-compatible 对象桶基础能力，以及 SonnetMQ 本地消息队列 MVP。',
  },
  {
    index: '08',
    title: '控制面与运维',
    description: '支持用户、授权、Token、数据库管理、备份恢复、维护接口、健康检查和指标端点。',
  },
  {
    index: '09',
    title: '工具链与连接器',
    description: '提供 ADO.NET、CLI、Web Admin、Docker、C、Go、Rust、Java、Python、VB6 和 PureBasic 连接器。',
  },
  {
    index: '10',
    title: 'Copilot 与 MCP',
    description: '在 Web Admin 和外部 Agent 中提供 SQL 生成、解释、修复、排障、工具调用和知识检索。',
  },
];

const roadmapCards = [
  {
    milestone: 'NuGet',
    title: 'ADO.NET 提供程序',
    description: '使用 dotnet add package SonnetDB 安装，然后在代码中引用 SonnetDB.Data 命名空间。',
  },
  {
    milestone: 'Docker',
    title: '服务端镜像',
    description: '使用 iotsharp/sonnetdb:latest 启动服务端，访问 /admin/ 完成首次安装。',
  },
  {
    milestone: 'CLI',
    title: 'sndb 命令行',
    description: '使用 SonnetDB.Cli 连接本地或远程数据库，执行 SQL、profile、备份和维护命令。',
  },
  {
    milestone: 'Docs',
    title: '帮助中心',
    description: '内置 /help/ 文档覆盖快速开始、SQL、ADO.NET、CLI、批量写入、架构和发布说明。',
  },
];

function scrollToSection(id: string): void {
  document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

async function goManage(): Promise<void> {
  try {
    await setup.ensureLoaded();
  } catch {
    // 如果健康检查或安装状态暂时不可达，仍然继续走管理入口的默认路由。
  }

  if (setup.needsSetup) {
    await router.push({ name: 'setup' });
    return;
  }

  if (auth.isAuthenticated) {
    await router.push({ name: 'dashboard' });
    return;
  }

  await router.push({ name: 'login' });
}
</script>

<style scoped>
.welcome-page {
  min-height: 100%;
  color: var(--sndb-ink-strong);
  background:
    radial-gradient(circle at top left, rgba(24, 160, 88, 0.14), transparent 28%),
    radial-gradient(circle at top right, rgba(13, 59, 102, 0.12), transparent 34%),
    linear-gradient(180deg, #f8fbff 0%, #eef5f9 100%);
}

.hero-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 24px;
  padding: 28px clamp(24px, 5vw, 56px) 12px;
}

.brand-lockup {
  display: inline-flex;
  align-items: center;
  gap: 14px;
}

.brand-mark {
  width: 52px;
  height: 52px;
  border-radius: 18px;
  display: grid;
  place-items: center;
  background: linear-gradient(135deg, #0d3b66 0%, #146c94 55%, #18a058 100%);
  box-shadow: 0 16px 30px rgba(13, 59, 102, 0.16);
}

.brand-mark-core {
  width: 22px;
  height: 22px;
  border-radius: 50%;
  border: 4px solid rgba(248, 251, 255, 0.94);
  box-shadow: inset 0 0 0 2px rgba(248, 251, 255, 0.12);
}

.brand-copy {
  display: inline-flex;
  flex-direction: column;
  gap: 2px;
}

.brand-name {
  font-size: 1.25rem;
  font-weight: 700;
  line-height: 1.1;
  letter-spacing: 0.03em;
  color: var(--sndb-ink-strong);
}

.brand-tagline {
  font-size: 0.78rem;
  line-height: 1.2;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--sndb-ink-soft);
}

.hero-nav {
  display: inline-flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.nav-link,
.nav-cta,
.primary-action,
.secondary-action {
  border: 0;
  cursor: pointer;
  transition: transform 160ms ease, box-shadow 160ms ease, background 160ms ease, color 160ms ease;
}

.nav-link {
  padding: 10px 14px;
  border-radius: 999px;
  background: transparent;
  color: var(--sndb-ink-soft);
  font: inherit;
}

.nav-link:hover,
.secondary-action:hover {
  transform: translateY(-1px);
  color: var(--sndb-ink-strong);
}

.nav-cta,
.primary-action {
  padding: 11px 18px;
  border-radius: 999px;
  background: linear-gradient(135deg, #0d3b66 0%, #18a058 100%);
  color: #f8fbff;
  font: inherit;
  font-weight: 600;
  box-shadow: 0 14px 28px rgba(13, 59, 102, 0.18);
}

.nav-cta:hover,
.primary-action:hover {
  transform: translateY(-1px);
  box-shadow: 0 20px 34px rgba(13, 59, 102, 0.22);
}

.hero-main {
  display: flex;
  flex-direction: column;
  gap: 32px;
  padding: 12px clamp(24px, 5vw, 56px) 56px;
}

.hero-panel {
  display: grid;
  grid-template-columns: minmax(0, 1.15fr) minmax(320px, 0.85fr);
  gap: 28px;
  align-items: stretch;
}

.hero-copy,
.hero-stage,
.section-panel {
  border: 1px solid rgba(13, 59, 102, 0.08);
  border-radius: 28px;
  background: rgba(248, 251, 255, 0.84);
  box-shadow: 0 22px 54px rgba(13, 59, 102, 0.08);
  backdrop-filter: blur(14px);
}

.hero-copy {
  padding: clamp(28px, 4vw, 42px);
}

.hero-eyebrow,
.roadmap-kicker,
.stage-kicker,
.info-kicker,
.feature-index {
  display: block;
  color: var(--sndb-ink-soft);
  font-size: 0.78rem;
  letter-spacing: 0.12em;
  text-transform: uppercase;
}

.hero-eyebrow {
  margin-bottom: 16px;
  color: #146c94;
  font-weight: 700;
}

.hero-copy h1,
.section-heading h2 {
  margin: 0;
  font-size: clamp(2.3rem, 5vw, 4.3rem);
  line-height: 1.02;
  letter-spacing: -0.04em;
}

.section-heading h2 {
  font-size: clamp(1.8rem, 4vw, 2.8rem);
}

.hero-subtitle,
.section-heading p,
.stage-card p,
.info-card p,
.feature-card p,
.roadmap-card p {
  margin: 18px 0 0;
  color: var(--sndb-ink-soft);
  line-height: 1.75;
}

.hero-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  margin-top: 28px;
}

.secondary-action {
  padding: 11px 18px;
  border-radius: 999px;
  background: rgba(13, 59, 102, 0.06);
  color: var(--sndb-ink-strong);
  font: inherit;
  font-weight: 600;
}

.hero-badges,
.info-grid,
.feature-grid,
.roadmap-grid {
  display: grid;
  gap: 16px;
}

.hero-badges {
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  margin-top: 28px;
}

.hero-badge,
.info-card,
.feature-card,
.roadmap-card,
.stage-card {
  border-radius: 22px;
  border: 1px solid rgba(13, 59, 102, 0.08);
  background: #ffffff;
  box-shadow: 0 14px 34px rgba(13, 59, 102, 0.06);
}

.hero-badge {
  padding: 16px 18px;
}

.hero-badge span {
  display: block;
  color: var(--sndb-ink-soft);
  font-size: 0.78rem;
  letter-spacing: 0.12em;
  text-transform: uppercase;
}

.hero-badge strong {
  display: block;
  margin-top: 10px;
  font-size: 1.02rem;
  line-height: 1.45;
}

.hero-stage {
  padding: 18px;
}

.stage-window {
  height: 100%;
  border-radius: 24px;
  background: linear-gradient(180deg, #0e2238 0%, #143f60 100%);
  color: #f8fbff;
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.06);
  overflow: hidden;
}

.window-topbar {
  display: flex;
  gap: 8px;
  padding: 16px 18px;
}

.window-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: rgba(248, 251, 255, 0.36);
}

.stage-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
  padding: 0 18px 18px;
}

.stage-card {
  padding: 18px;
  background: rgba(255, 255, 255, 0.06);
  color: #f8fbff;
}

.stage-card p,
.stage-kicker {
  color: rgba(248, 251, 255, 0.72);
}

.stage-card-large {
  grid-column: span 2;
}

.stage-card-accent {
  background: linear-gradient(135deg, rgba(24, 160, 88, 0.24), rgba(20, 108, 148, 0.16));
}

.stage-card strong,
.info-card h3,
.feature-card h3,
.roadmap-card h3 {
  display: block;
  margin-top: 10px;
  font-size: 1.08rem;
}

.stage-list {
  margin: 16px 0 0;
  padding-left: 18px;
  color: rgba(248, 251, 255, 0.82);
}

.stage-list li + li {
  margin-top: 8px;
}

.section-panel {
  padding: clamp(26px, 4vw, 38px);
}

.section-panel-tight {
  margin-bottom: 4px;
}

.section-heading {
  display: flex;
  flex-direction: column;
  gap: 12px;
  margin-bottom: 22px;
}

.info-grid {
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
}

.info-card,
.feature-card,
.roadmap-card {
  padding: 22px;
}

.info-card p,
.feature-card p,
.roadmap-card p {
  margin-top: 12px;
}

.feature-grid {
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
}

.roadmap-grid {
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
}

@media (max-width: 1100px) {
  .hero-panel,
  .info-grid,
  .feature-grid,
  .roadmap-grid {
    grid-template-columns: 1fr;
  }

  .stage-grid {
    grid-template-columns: 1fr;
  }

  .stage-card-large {
    grid-column: span 1;
  }
}

@media (max-width: 720px) {
  .hero-header {
    flex-direction: column;
    align-items: flex-start;
  }

  .hero-copy h1,
  .section-heading h2 {
    font-size: 2rem;
  }
}
</style>
