<template>
  <section class="preview-overview">
    <h1>Workbench Preview 1</h1>
    <p>当前用户：{{ auth.username }} · 同源连接：{{ origin }}</p>
    <p>当前数据库：{{ previewSession.database || '尚未选择' }}</p>
    <router-link to="/admin/app/sql">选择数据库并打开工作台</router-link>
    <p>使用测试数据库评估。Graph 保持 Beta；MQ 按 database + Topic 区分，持久化位于实例 .system/mq，单库备份不包含实例 MQ。</p>
  </section>
</template>
<script setup lang="ts">
import { useAuthStore } from '@/stores/auth';
import { previewSession } from './session';
const auth = useAuthStore();
const origin = window.location.origin;
</script>
<style scoped>.preview-overview { padding: 32px; max-width: 900px; line-height: 1.8; }</style>
