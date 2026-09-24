<template>
  <main class="copilot-oauth-callback" data-testid="copilot-oauth-callback">
    <h1>连接 AI 服务</h1>
    <p role="status" aria-live="polite">{{ status }}</p>
    <button type="button" @click="closeWindow">关闭此窗口</button>
  </main>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { completeBrowserDirectOAuthCallback } from '@/copilot/browserDirectOAuth';

const status = ref('正在完成授权…');

function closeWindow(): void {
  window.close();
}

onMounted(() => {
  try {
    status.value = completeBrowserDirectOAuthCallback()
      ? '授权结果已发送，请返回原窗口。'
      : '无法完成连接，请返回原窗口重新授权。';
  } catch {
    // Never render the callback URL, authorization code, or provider error text.
    status.value = '无法完成连接，请返回原窗口重新授权。';
  }
});
</script>

<style scoped>
.copilot-oauth-callback {
  max-width: 400px;
  margin: 15vh auto;
  padding: 24px;
  color: #263849;
}
.copilot-oauth-callback h1 { font-size: 22px; }
.copilot-oauth-callback p { line-height: 1.7; }
.copilot-oauth-callback button {
  margin-top: 12px;
  padding: 8px 16px;
  border: 1px solid #ccd5df;
  border-radius: 4px;
  background: #fff;
  color: inherit;
  cursor: pointer;
}
</style>
