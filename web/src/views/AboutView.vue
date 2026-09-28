<template>
  <div class="about-page">
    <header class="about-page__header">
      <div>
        <div class="about-page__eyebrow">SonnetDB Studio</div>
        <h1>关于 SonnetDB</h1>
        <p>产品信息与当前服务主机环境</p>
      </div>
      <n-button secondary :loading="loading" @click="loadAbout">
        <template #icon><RefreshCw :size="16" /></template>
        刷新信息
      </n-button>
    </header>

    <n-alert v-if="lastError" type="error" :title="lastError" closable style="margin-bottom: 16px;" />

    <n-spin :show="loading && !about">
      <section class="about-section about-product">
        <div class="about-product__identity">
          <BrandLogo />
          <div>
            <div class="about-product__name">SonnetDB</div>
            <div class="about-product__tagline">九种数据模型 · 原生语义 · 一套引擎</div>
          </div>
        </div>
        <div class="about-product__meta">
          <div class="about-meta-item">
            <span>当前版本</span>
            <strong>{{ about?.serverVersion ?? '读取中…' }}</strong>
          </div>
          <div class="about-meta-item">
            <span>版权</span>
            <strong>Copyright (c) 2026 maikebing</strong>
          </div>
          <div class="about-meta-item">
            <span>许可证</span>
            <strong>MIT License</strong>
          </div>
        </div>
      </section>

      <div class="about-link-grid">
        <a class="about-link" href="https://github.com/IoTSharp/SonnetDB" target="_blank" rel="noreferrer">
          <span class="about-link__icon"><Github :size="19" /></span>
          <span><strong>GitHub 仓库</strong><small>github.com/IoTSharp/SonnetDB</small></span>
          <ExternalLink :size="16" />
        </a>
        <a class="about-link" href="https://gitee.com/IoTSharp/SonnetDB" target="_blank" rel="noreferrer">
          <span class="about-link__icon about-link__icon--gitee">G</span>
          <span><strong>Gitee 仓库</strong><small>gitee.com/IoTSharp/SonnetDB</small></span>
          <ExternalLink :size="16" />
        </a>
        <div class="about-link about-link--group">
          <span class="about-link__icon"><MessageCircle :size="19" /></span>
          <span><strong>企微交流群</strong><small>扫码加入 SonnetDB 群聊</small></span>
          <img src="/qr-group.png" alt="SonnetDB 企微群二维码" />
        </div>
      </div>

      <section class="about-section about-system">
        <div class="about-section__heading">
          <div>
            <div class="about-page__eyebrow">运行环境</div>
            <h2>服务主机</h2>
          </div>
          <n-tag v-if="about" type="success" size="small">实时快照</n-tag>
        </div>

        <div class="about-summary-grid">
          <div class="about-summary-item"><span>主机名</span><strong>{{ about?.hostName ?? '读取中…' }}</strong></div>
          <div class="about-summary-item"><span>操作系统</span><strong>{{ about?.osDescription ?? '读取中…' }}</strong></div>
          <div class="about-summary-item"><span>系统版本</span><strong>{{ about?.osVersion ?? '读取中…' }}</strong></div>
          <div class="about-summary-item"><span>运行时</span><strong>{{ about?.runtimeDescription ?? '读取中…' }}</strong></div>
        </div>

        <div v-if="about" class="about-detail-grid">
          <article class="about-detail-block">
            <div class="about-detail-block__title"><Cpu :size="17" /> CPU</div>
            <dl>
              <div><dt>型号</dt><dd>{{ about.cpu.name }}</dd></div>
              <div><dt>逻辑处理器</dt><dd>{{ about.cpu.logicalProcessors }} 核</dd></div>
              <div><dt>架构</dt><dd>{{ about.osArchitecture }} / {{ about.processArchitecture }}</dd></div>
              <div><dt>当前频率</dt><dd>{{ formatSpeed(about.cpu.speedMHz) }}</dd></div>
            </dl>
          </article>

          <article class="about-detail-block">
            <div class="about-detail-block__title"><MemoryStick :size="17" /> 内存</div>
            <dl>
              <div><dt>总内存</dt><dd>{{ formatBytes(about.memory.totalBytes) }}</dd></div>
              <div><dt>可用内存</dt><dd>{{ formatBytes(about.memory.availableBytes) }}</dd></div>
              <div><dt>数据来源</dt><dd>{{ about.memory.source }}</dd></div>
            </dl>
          </article>

          <article class="about-detail-block about-detail-block--wide">
            <div class="about-detail-block__title"><HardDrive :size="17" /> 磁盘</div>
            <div v-if="about.disks.length" class="about-disk-list">
              <div v-for="disk in about.disks" :key="`${disk.name}:${disk.format}`" class="about-disk-row">
                <strong>{{ disk.name }}</strong>
                <span>{{ disk.format }}</span>
                <span>{{ formatBytes(disk.availableBytes) }} 可用 / {{ formatBytes(disk.totalBytes) }}</span>
              </div>
            </div>
            <div v-else class="about-empty">未检测到可用磁盘信息</div>
          </article>

          <article class="about-detail-block about-detail-block--wide">
            <div class="about-detail-block__title"><Monitor :size="17" /> GPU</div>
            <div v-if="about.gpus.length" class="about-gpu-list">
              <div v-for="gpu in about.gpus" :key="`${gpu.name}:${gpu.pciId}`" class="about-gpu-row">
                <strong>{{ gpu.name }}</strong>
                <span v-if="gpu.driver">驱动 {{ gpu.driver }}</span>
                <span v-if="gpu.pciId">PCI {{ gpu.pciId }}</span>
              </div>
            </div>
            <div v-else class="about-empty">当前系统未提供 GPU 识别信息</div>
          </article>
        </div>

        <div v-if="about" class="about-captured-at">采集时间：{{ formatCapturedAt(about.capturedAtUtc) }}</div>
      </section>
    </n-spin>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { NAlert, NButton, NSpin, NTag } from 'naive-ui';
import {
  Cpu,
  ExternalLink,
  Github,
  HardDrive,
  MemoryStick,
  MessageCircle,
  Monitor,
  RefreshCw,
} from 'lucide-vue-next';
import BrandLogo from '@/components/BrandLogo.vue';
import { fetchSystemAbout, type SystemAboutResponse } from '@/api/about';
import { useAuthStore } from '@/stores/auth';

const auth = useAuthStore();
const about = ref<SystemAboutResponse | null>(null);
const loading = ref(false);
const lastError = ref('');

async function loadAbout(): Promise<void> {
  loading.value = true;
  lastError.value = '';
  try {
    about.value = await fetchSystemAbout(auth.api);
  } catch (error) {
    lastError.value = error instanceof Error ? error.message : '读取服务器信息失败。';
  } finally {
    loading.value = false;
  }
}

function formatBytes(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return '未检测到';
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let index = 0;
  let current = value;
  while (current >= 1024 && index < units.length - 1) {
    current /= 1024;
    index += 1;
  }
  return `${current.toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

function formatSpeed(value: number | null): string {
  return value && value > 0 ? `${value.toLocaleString('zh-CN')} MHz` : '未检测到';
}

function formatCapturedAt(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('zh-CN', { hour12: false });
}

onMounted(() => { void loadAbout(); });
</script>

<style scoped>
.about-page { max-width: 1320px; margin: 0 auto; }
.about-page__header, .about-section__heading { display: flex; align-items: flex-start; justify-content: space-between; gap: 18px; }
.about-page__header { margin-bottom: 20px; }
.about-page h1, .about-page h2 { margin: 0; color: var(--sndb-ink-strong); font-weight: 680; letter-spacing: 0; }
.about-page h1 { font-size: 24px; }
.about-page h2 { font-size: 17px; }
.about-page__header p { margin: 6px 0 0; color: var(--sndb-ink-muted); }
.about-page__eyebrow { margin-bottom: 6px; color: var(--sndb-interactive); font-size: 11px; font-weight: 700; letter-spacing: 0.11em; text-transform: uppercase; }
.about-section { border: 1px solid var(--sndb-border); border-radius: 6px; background: var(--sndb-surface-strong); box-shadow: 0 4px 14px rgba(36, 36, 36, 0.04); }
.about-product { display: grid; grid-template-columns: minmax(260px, 1fr) minmax(420px, 1.4fr); align-items: center; gap: 28px; padding: 24px; }
.about-product__identity { display: flex; align-items: center; gap: 16px; }
.about-product__name { font-size: 23px; font-weight: 700; }
.about-product__tagline { margin-top: 5px; color: var(--sndb-ink-muted); }
.about-product__meta { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 1px; border: 1px solid var(--sndb-border); background: var(--sndb-border); }
.about-meta-item { min-width: 0; padding: 14px; background: #fff; }
.about-meta-item span, .about-summary-item span { display: block; color: var(--sndb-ink-subtle); font-size: 12px; }
.about-meta-item strong { display: block; margin-top: 6px; overflow-wrap: anywhere; font-size: 13px; }
.about-link-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; margin: 12px 0 16px; }
.about-link { display: flex; align-items: center; gap: 11px; min-width: 0; min-height: 68px; padding: 12px 14px; border: 1px solid var(--sndb-border); border-radius: 6px; background: #fff; color: inherit; text-decoration: none; transition: border-color .15s ease, background .15s ease; }
.about-link:hover { border-color: var(--sndb-interactive); background: #f8fbff; }
.about-link > span:nth-child(2) { display: flex; flex: 1; flex-direction: column; min-width: 0; gap: 4px; }
.about-link strong { font-size: 13px; }
.about-link small { overflow-wrap: anywhere; color: var(--sndb-ink-muted); font-size: 11px; }
.about-link > svg { flex: none; color: var(--sndb-ink-subtle); }
.about-link__icon { display: grid; flex: none; place-items: center; width: 34px; height: 34px; border-radius: 5px; background: #eef4f8; color: var(--sndb-interactive); }
.about-link__icon--gitee { font-weight: 800; color: #c71d23; }
.about-link--group { position: relative; padding-right: 74px; }
.about-link--group img { position: absolute; right: 12px; width: 50px; height: 50px; border: 1px solid var(--sndb-border); border-radius: 4px; object-fit: cover; }
.about-system { padding: 22px 24px 18px; }
.about-summary-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 1px; margin-top: 18px; border: 1px solid var(--sndb-border); background: var(--sndb-border); }
.about-summary-item { min-width: 0; padding: 13px 14px; background: #fff; }
.about-summary-item strong { display: block; margin-top: 5px; overflow-wrap: anywhere; font-size: 13px; line-height: 1.4; }
.about-detail-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; margin-top: 16px; }
.about-detail-block { min-width: 0; padding: 15px 16px; border: 1px solid var(--sndb-border); border-radius: 5px; background: #fbfcfd; }
.about-detail-block--wide { grid-column: span 2; }
.about-detail-block__title { display: flex; align-items: center; gap: 7px; margin-bottom: 13px; color: var(--sndb-ink-soft); font-size: 13px; font-weight: 700; }
.about-detail-block__title svg { color: var(--sndb-interactive); }
.about-detail-block dl { display: grid; gap: 9px; margin: 0; }
.about-detail-block dl > div { display: grid; grid-template-columns: 90px minmax(0, 1fr); gap: 12px; }
.about-detail-block dt { color: var(--sndb-ink-subtle); font-size: 12px; }
.about-detail-block dd { min-width: 0; margin: 0; overflow-wrap: anywhere; color: var(--sndb-ink-strong); font-size: 13px; }
.about-disk-list, .about-gpu-list { display: grid; gap: 7px; }
.about-disk-row, .about-gpu-row { display: grid; grid-template-columns: minmax(90px, .6fr) minmax(90px, .4fr) minmax(0, 1.2fr); gap: 12px; padding: 9px 10px; border: 1px solid var(--sndb-border); background: #fff; font-size: 12px; }
.about-gpu-row { grid-template-columns: minmax(120px, 1fr) minmax(100px, .5fr) minmax(100px, .7fr); }
.about-disk-row span, .about-gpu-row span { color: var(--sndb-ink-muted); overflow-wrap: anywhere; }
.about-empty { color: var(--sndb-ink-subtle); font-size: 12px; }
.about-captured-at { margin-top: 14px; color: var(--sndb-ink-subtle); font-size: 11px; }
@media (max-width: 900px) { .about-product { grid-template-columns: 1fr; } .about-link-grid, .about-summary-grid { grid-template-columns: 1fr 1fr; } .about-link--group { grid-column: span 2; } }
@media (max-width: 640px) { .about-page__header { align-items: stretch; flex-direction: column; } .about-product, .about-system { padding: 16px; } .about-product__meta, .about-link-grid, .about-summary-grid, .about-detail-grid { grid-template-columns: 1fr; } .about-link--group, .about-detail-block--wide { grid-column: auto; } .about-disk-row, .about-gpu-row { grid-template-columns: 1fr; gap: 4px; } }
</style>
