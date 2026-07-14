<template>
  <section class="async-workbench-loading" role="status" aria-live="polite">
    <span class="async-workbench-loading__bar" />
    <span>正在加载工作台...</span>
  </section>
</template>

<style scoped>
.async-workbench-loading {
  display: grid;
  place-content: center;
  gap: 12px;
  min-width: 0;
  min-height: 0;
  height: 100%;
  color: #596574;
  font-size: 14px;
  background: #fff;
}

.async-workbench-loading__bar {
  position: relative;
  width: min(240px, 45vw);
  height: 3px;
  overflow: hidden;
  background: #dce3ec;
}

.async-workbench-loading__bar::after {
  position: absolute;
  inset: 0;
  width: 42%;
  content: '';
  background: #0f6cbd;
  animation: async-workbench-progress 1s ease-in-out infinite alternate;
}

@keyframes async-workbench-progress {
  from { transform: translateX(-20%); }
  to { transform: translateX(160%); }
}

@media (prefers-reduced-motion: reduce) {
  .async-workbench-loading__bar::after {
    width: 100%;
    animation: none;
  }
}
</style>
