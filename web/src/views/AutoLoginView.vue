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
  const ticket = readFragment('ticket');
  const apiBaseUrl = readFragment('api');
  const redirect = readFragment('redirect') || '/admin/app/sql';

  if (!ticket || !apiBaseUrl) {
    message.value = '授权链接无效，请回到 sonnetdb.com 用户中心重新打开。';
    return;
  }

  const credential = await redeemTicket(apiBaseUrl, ticket);
  if (!credential) {
    message.value = '授权票据已过期或已使用，请回到 sonnetdb.com 用户中心重新打开。';
    return;
  }

  auth.apply({
    username: credential.username,
    token: credential.token,
    tokenId: credential.tokenId,
    isSuperuser: true,
  });

  await router.replace(redirect);
});

function readFragment(key: string): string | null {
  const fragment = route.hash.startsWith('#') ? route.hash.slice(1) : route.hash;
  const value = new URLSearchParams(fragment).get(key);
  return value?.trim() || null;
}

async function redeemTicket(apiBaseUrl: string, ticket: string): Promise<OpenTicketCredential | null> {
  try {
    const response = await fetch(`${apiBaseUrl.replace(/\/+$/, '')}/api/v1/managed-instances/open-ticket/redeem`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
      },
      body: JSON.stringify({ ticket }),
    });

    if (!response.ok) {
      return null;
    }

    return (await response.json()) as OpenTicketCredential;
  } catch {
    return null;
  }
}

type OpenTicketCredential = {
  username: string;
  token: string;
  tokenId: string;
};
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
