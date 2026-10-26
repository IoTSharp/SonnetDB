<template>
  <n-config-provider :theme="null">
    <n-message-provider>
      <n-notification-provider>
        <n-dialog-provider>
          <router-view />
        </n-dialog-provider>
      </n-notification-provider>
    </n-message-provider>
  </n-config-provider>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { NConfigProvider, NMessageProvider, NNotificationProvider, NDialogProvider } from 'naive-ui';
import { AuthExpiredEvent } from '@/api/client';

const route = useRoute();
const router = useRouter();

function onAuthExpired(): void {
  if (!route.meta.app) return;
  void router.replace({ name: 'login', query: { redirect: route.fullPath } });
}

onMounted(() => window.addEventListener(AuthExpiredEvent, onAuthExpired));
onBeforeUnmount(() => window.removeEventListener(AuthExpiredEvent, onAuthExpired));
</script>
