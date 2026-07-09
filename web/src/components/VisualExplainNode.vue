<template>
  <li class="explain-node" :class="`explain-node--${node.kind}`">
    <div class="explain-node__row">
      <span class="explain-node__rail" aria-hidden="true" />
      <div class="explain-node__body">
        <div class="explain-node__head">
          <div class="explain-node__identity">
            <span class="explain-node__title">{{ node.title }}</span>
            <span v-if="node.subtitle" class="explain-node__subtitle">{{ node.subtitle }}</span>
          </div>
          <div v-if="node.badges.length" class="explain-node__badges">
            <span v-for="badge in node.badges" :key="badge" class="explain-node__badge">
              {{ badge }}
            </span>
          </div>
        </div>

        <dl v-if="node.metrics.length" class="explain-node__metrics">
          <div
            v-for="metric in node.metrics"
            :key="metric.label"
            class="explain-node__metric"
            :class="`explain-node__metric--${metric.tone}`"
          >
            <dt>{{ metric.label }}</dt>
            <dd>{{ metric.value }}</dd>
          </div>
        </dl>
      </div>
    </div>

    <ol v-if="node.children.length" class="explain-node__children">
      <VisualExplainNode
        v-for="child in node.children"
        :key="child.id"
        :node="child"
      />
    </ol>
  </li>
</template>

<script setup lang="ts">
import type { ExplainPlanNode } from '@/utils/explainPlan';

defineOptions({ name: 'VisualExplainNode' });

defineProps<{
  node: ExplainPlanNode;
}>();
</script>

<style scoped>
.explain-node {
  list-style: none;
}

.explain-node__row {
  display: grid;
  grid-template-columns: 22px minmax(0, 1fr);
  align-items: stretch;
}

.explain-node__rail {
  position: relative;
  display: block;
}

.explain-node__rail::before {
  position: absolute;
  top: 0;
  bottom: -14px;
  left: 10px;
  width: 1px;
  content: "";
  background: rgba(15, 23, 42, 0.12);
}

.explain-node__rail::after {
  position: absolute;
  top: 18px;
  left: 5px;
  width: 11px;
  height: 11px;
  content: "";
  border: 2px solid #7c9ab5;
  border-radius: 999px;
  background: #fff;
  box-shadow: 0 0 0 3px rgba(124, 154, 181, 0.1);
}

.explain-node--root > .explain-node__row > .explain-node__rail::after {
  border-color: #0d3b66;
  box-shadow: 0 0 0 4px rgba(13, 59, 102, 0.12);
}

.explain-node--index > .explain-node__row > .explain-node__rail::after,
.explain-node--catalog > .explain-node__row > .explain-node__rail::after,
.explain-node--projection > .explain-node__row > .explain-node__rail::after {
  border-color: #18a058;
  box-shadow: 0 0 0 4px rgba(24, 160, 88, 0.12);
}

.explain-node--scan > .explain-node__row > .explain-node__rail::after,
.explain-node--sort > .explain-node__row > .explain-node__rail::after {
  border-color: #d9822b;
  box-shadow: 0 0 0 4px rgba(217, 130, 43, 0.12);
}

.explain-node--join > .explain-node__row > .explain-node__rail::after,
.explain-node--vector > .explain-node__row > .explain-node__rail::after {
  border-color: #2c7be5;
  box-shadow: 0 0 0 4px rgba(44, 123, 229, 0.12);
}

.explain-node__body {
  min-width: 0;
  margin-bottom: 10px;
  padding: 10px 12px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-left: 3px solid rgba(13, 59, 102, 0.35);
  border-radius: 6px;
  background: #fff;
}

.explain-node--root > .explain-node__row > .explain-node__body {
  background: #f7fbff;
}

.explain-node--index > .explain-node__row > .explain-node__body,
.explain-node--catalog > .explain-node__row > .explain-node__body,
.explain-node--projection > .explain-node__row > .explain-node__body {
  border-left-color: #18a058;
}

.explain-node--scan > .explain-node__row > .explain-node__body,
.explain-node--sort > .explain-node__row > .explain-node__body {
  border-left-color: #d9822b;
}

.explain-node--join > .explain-node__row > .explain-node__body,
.explain-node--vector > .explain-node__row > .explain-node__body {
  border-left-color: #2c7be5;
}

.explain-node__head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
}

.explain-node__identity {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.explain-node__title {
  color: var(--sndb-ink-strong);
  font-size: 13px;
  font-weight: 800;
  line-height: 1.3;
}

.explain-node__subtitle {
  overflow: hidden;
  color: var(--sndb-ink-soft);
  font-size: 12px;
  line-height: 1.35;
  text-overflow: ellipsis;
}

.explain-node__badges {
  display: flex;
  flex: 0 0 auto;
  gap: 5px;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.explain-node__badge {
  padding: 1px 6px;
  border-radius: 999px;
  background: rgba(13, 59, 102, 0.08);
  color: #31536f;
  font-size: 10px;
  font-weight: 800;
  text-transform: uppercase;
}

.explain-node__metrics {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
  gap: 6px;
  margin: 9px 0 0;
}

.explain-node__metric {
  min-width: 0;
  padding: 6px 8px;
  border-radius: 5px;
  background: #f7fafc;
}

.explain-node__metric dt {
  margin: 0;
  color: #6a7f91;
  font-size: 10px;
  font-weight: 700;
  text-transform: uppercase;
}

.explain-node__metric dd {
  overflow: hidden;
  margin: 1px 0 0;
  color: #22384d;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.explain-node__metric--good {
  background: rgba(24, 160, 88, 0.08);
}

.explain-node__metric--warn {
  background: rgba(217, 130, 43, 0.1);
}

.explain-node__metric--danger {
  background: rgba(208, 48, 80, 0.1);
}

.explain-node__metric--muted {
  opacity: 0.72;
}

.explain-node__children {
  margin: 0 0 0 22px;
  padding: 0;
}

.explain-node__children > .explain-node:last-child .explain-node__rail::before {
  bottom: 50%;
}

@media (max-width: 760px) {
  .explain-node__head {
    flex-direction: column;
  }

  .explain-node__badges {
    justify-content: flex-start;
  }
}
</style>
