<template>
  <div class="auto-login-page">
    <div class="auto-login-panel">
      <h1>正在打开数据库</h1>
      <p>{{ message }}</p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useAuthStore } from '@/stores/auth';

const route = useRoute();
const router = useRouter();
const auth = useAuthStore();
const message = ref('正在应用平台授权。');

onMounted(async () => {
  const username = readQuery('username') || 'admin';
  const token = readQuery('token');
  const tokenId = readQuery('tokenId') || 'platform-ticket';
  const redirect = readQuery('redirect') || '/admin/app/sql';

  if (!token) {
    message.value = '授权链接无效，请回到 sonnetdb.com 用户中心重新打开。';
    return;
  }

  auth.apply({
    username,
    token,
    tokenId,
    isSuperuser: true,
  });

  await router.replace(redirect);
});

function readQuery(key: string): string | null {
  const value = route.query[key];
  return typeof value === 'string' && value.trim() ? value.trim() : null;
}
</script>

<style scoped>
.auto-login-page {
  display: grid;
  min-height: 100%;
  place-items: center;
  background: #f6fbff;
}

.auto-login-panel {
  width: min(92vw, 420px);
  border: 1px solid rgba(13, 59, 102, 0.1);
  border-radius: 18px;
  padding: 28px;
  background: #fff;
  box-shadow: 0 18px 44px rgba(13, 59, 102, 0.12);
}

.auto-login-panel h1 {
  margin: 0;
  color: #0d3b66;
  font-size: 1.5rem;
}

.auto-login-panel p {
  margin: 12px 0 0;
  color: #55616f;
}
</style>
