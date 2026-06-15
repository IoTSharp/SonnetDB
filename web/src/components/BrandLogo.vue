<template>
  <div class="brand-logo" :class="{ compact, light }">
    <div class="brand-mark" aria-hidden="true">
      <svg viewBox="0 0 88 88" role="img">
        <defs>
          <linearGradient id="sonnetdbMarkGradient" x1="0%" y1="0%" x2="100%" y2="100%">
            <stop offset="0%" stop-color="#0d3b66" />
            <stop offset="55%" stop-color="#146c94" />
            <stop offset="100%" stop-color="#18a058" />
          </linearGradient>
        </defs>
        <rect x="6" y="6" width="76" height="76" rx="24" fill="url(#sonnetdbMarkGradient)" />
        <path
          d="M22 58 L34 46 L44 52 L56 34 L66 40"
          fill="none"
          stroke="#f8fbff"
          stroke-linecap="round"
          stroke-linejoin="round"
          stroke-width="6"
        />
        <path
          d="M22 26 H66"
          fill="none"
          stroke="rgba(255,255,255,0.36)"
          stroke-linecap="round"
          stroke-width="4"
        />
        <circle cx="66" cy="40" r="4.5" fill="#f8fbff" />
      </svg>
    </div>
    <div v-if="!compact" class="brand-copy">
      <span class="brand-name">SonnetDB</span>
      <span class="brand-tagline">AI-native 多模型数据底座</span>
    </div>
  </div>
</template>

<script setup lang="ts">
defineProps<{
  compact?: boolean;
  light?: boolean;
}>();
</script>

<style scoped>
.brand-logo {
  display: inline-flex;
  align-items: center;
  gap: 14px;
}

.brand-mark {
  width: 52px;
  height: 52px;
  border-radius: 18px;
  box-shadow: 0 16px 30px rgba(13, 59, 102, 0.16);
  overflow: hidden;
  flex: none;
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

.compact .brand-mark {
  width: 42px;
  height: 42px;
  border-radius: 14px;
}

.light .brand-name,
.light .brand-tagline {
  color: #f8fbff;
}
</style>
