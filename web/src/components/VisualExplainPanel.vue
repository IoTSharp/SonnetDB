<template>
  <section class="visual-explain">
    <header class="visual-explain__header">
      <div class="visual-explain__identity">
        <span class="visual-explain__eyebrow">Visual EXPLAIN</span>
        <h3>{{ plan.title }}</h3>
        <p>{{ plan.subtitle || 'SQL planner summary' }}</p>
      </div>
      <div class="visual-explain__score">
        <strong>{{ plan.totalRows }}</strong>
        <span>estimated rows</span>
      </div>
    </header>

    <dl class="visual-explain__metrics">
      <div
        v-for="metric in plan.metrics"
        :key="metric.label"
        class="visual-explain__metric"
        :class="`visual-explain__metric--${metric.tone}`"
      >
        <dt>{{ metric.label }}</dt>
        <dd>{{ metric.value }}</dd>
      </div>
    </dl>

    <div class="visual-explain__layout">
      <section class="visual-explain__tree" aria-label="Visual explain plan tree">
        <ol class="visual-explain__nodes">
          <VisualExplainNode :node="plan.root" />
        </ol>
      </section>

      <aside class="visual-explain__side">
        <section v-if="plan.candidates.length" class="visual-explain__panel">
          <div class="visual-explain__panel-head">
            <span>Candidate paths</span>
            <strong>{{ plan.candidates.length }}</strong>
          </div>
          <div class="visual-explain__candidates">
            <article
              v-for="candidate in plan.candidates"
              :key="candidate.id"
              class="visual-explain__candidate"
              :class="{ 'is-selected': candidate.selected }"
            >
              <div class="visual-explain__candidate-main">
                <span>{{ candidate.accessLabel }}</span>
                <strong v-if="candidate.selected">selected</strong>
              </div>
              <dl>
                <div v-if="candidate.indexName">
                  <dt>index</dt>
                  <dd>{{ candidate.indexName }}</dd>
                </div>
                <div v-if="candidate.rows">
                  <dt>rows</dt>
                  <dd>{{ candidate.rows }}</dd>
                </div>
                <div v-if="candidate.cost">
                  <dt>cost</dt>
                  <dd>{{ candidate.cost }}</dd>
                </div>
                <div v-if="candidate.pushdownFields.length">
                  <dt>pushdown</dt>
                  <dd>{{ candidate.pushdownFields.join(', ') }}</dd>
                </div>
                <div v-if="candidate.rejectReason">
                  <dt>reason</dt>
                  <dd>{{ candidate.rejectReason }}</dd>
                </div>
              </dl>
            </article>
          </div>
        </section>

        <section class="visual-explain__panel">
          <div class="visual-explain__panel-head">
            <span>Signals</span>
            <strong>{{ plan.notes.length }}</strong>
          </div>
          <ul v-if="plan.notes.length" class="visual-explain__notes">
            <li v-for="note in plan.notes" :key="note">{{ note }}</li>
          </ul>
          <p v-else class="visual-explain__empty">No planner warnings or extra hints were reported.</p>
        </section>
      </aside>
    </div>
  </section>
</template>

<script setup lang="ts">
import VisualExplainNode from '@/components/VisualExplainNode.vue';
import type { VisualExplainPlan } from '@/utils/explainPlan';

defineProps<{
  plan: VisualExplainPlan;
}>();
</script>

<style scoped>
.visual-explain {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
  padding: 2px;
}

.visual-explain__header {
  display: flex;
  align-items: stretch;
  justify-content: space-between;
  gap: 12px;
  padding: 12px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background:
    linear-gradient(135deg, rgba(13, 59, 102, 0.06), rgba(24, 160, 88, 0.04)),
    #fbfdff;
}

.visual-explain__identity {
  min-width: 0;
}

.visual-explain__eyebrow {
  display: inline-flex;
  margin-bottom: 4px;
  color: #386078;
  font-size: 10px;
  font-weight: 900;
  letter-spacing: 0;
  text-transform: uppercase;
}

.visual-explain h3 {
  margin: 0;
  color: var(--sndb-ink-strong);
  font-size: 17px;
  line-height: 1.2;
}

.visual-explain p {
  margin: 4px 0 0;
  color: var(--sndb-ink-soft);
  font-size: 12px;
  line-height: 1.45;
}

.visual-explain__score {
  display: flex;
  flex: 0 0 150px;
  flex-direction: column;
  justify-content: center;
  padding: 10px 12px;
  border-left: 1px solid rgba(13, 59, 102, 0.12);
  text-align: right;
}

.visual-explain__score strong {
  overflow: hidden;
  color: #0d3b66;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 24px;
  line-height: 1.1;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.visual-explain__score span {
  color: #60788c;
  font-size: 11px;
  font-weight: 700;
  text-transform: uppercase;
}

.visual-explain__metrics {
  display: grid;
  grid-template-columns: repeat(6, minmax(0, 1fr));
  gap: 8px;
  margin: 0;
}

.visual-explain__metric {
  min-width: 0;
  padding: 8px 10px;
  border: 1px solid rgba(15, 23, 42, 0.07);
  border-radius: 6px;
  background: #fff;
}

.visual-explain__metric dt {
  margin: 0;
  color: #6a7f91;
  font-size: 10px;
  font-weight: 800;
  text-transform: uppercase;
}

.visual-explain__metric dd {
  overflow: hidden;
  margin: 2px 0 0;
  color: #1d3448;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.visual-explain__metric--good {
  border-color: rgba(24, 160, 88, 0.22);
  background: rgba(24, 160, 88, 0.06);
}

.visual-explain__metric--warn {
  border-color: rgba(217, 130, 43, 0.24);
  background: rgba(217, 130, 43, 0.08);
}

.visual-explain__metric--danger {
  border-color: rgba(208, 48, 80, 0.24);
  background: rgba(208, 48, 80, 0.08);
}

.visual-explain__metric--muted {
  background: #f7fafc;
}

.visual-explain__layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 300px;
  gap: 12px;
  align-items: start;
}

.visual-explain__tree,
.visual-explain__panel {
  min-width: 0;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-radius: 6px;
  background: #fff;
}

.visual-explain__tree {
  padding: 12px 12px 2px;
  overflow: auto;
}

.visual-explain__nodes {
  min-width: 480px;
  margin: 0;
  padding: 0;
}

.visual-explain__side {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
}

.visual-explain__panel {
  padding: 10px;
}

.visual-explain__panel-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin-bottom: 8px;
  color: var(--sndb-ink-strong);
  font-size: 12px;
  font-weight: 900;
}

.visual-explain__panel-head strong {
  padding: 1px 7px;
  border-radius: 999px;
  background: rgba(13, 59, 102, 0.08);
  color: #31536f;
  font-size: 11px;
}

.visual-explain__candidates {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.visual-explain__candidate {
  padding: 8px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  border-left: 3px solid rgba(15, 23, 42, 0.18);
  border-radius: 5px;
  background: #fbfdff;
}

.visual-explain__candidate.is-selected {
  border-left-color: #18a058;
  background: rgba(24, 160, 88, 0.06);
}

.visual-explain__candidate-main {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin-bottom: 6px;
}

.visual-explain__candidate-main span {
  min-width: 0;
  overflow: hidden;
  color: #24384b;
  font-size: 12px;
  font-weight: 800;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.visual-explain__candidate-main strong {
  flex: 0 0 auto;
  color: #16784a;
  font-size: 10px;
  font-weight: 900;
  text-transform: uppercase;
}

.visual-explain__candidate dl {
  display: grid;
  grid-template-columns: 1fr;
  gap: 4px;
  margin: 0;
}

.visual-explain__candidate dl div {
  display: grid;
  grid-template-columns: 64px minmax(0, 1fr);
  gap: 8px;
  min-width: 0;
}

.visual-explain__candidate dt {
  color: #778898;
  font-size: 10px;
  font-weight: 800;
  text-transform: uppercase;
}

.visual-explain__candidate dd {
  overflow: hidden;
  margin: 0;
  color: #22384d;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, monospace;
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.visual-explain__notes {
  display: flex;
  flex-direction: column;
  gap: 7px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.visual-explain__notes li {
  position: relative;
  padding-left: 14px;
  color: #34495c;
  font-size: 12px;
  line-height: 1.45;
}

.visual-explain__notes li::before {
  position: absolute;
  top: 0.58em;
  left: 0;
  width: 6px;
  height: 6px;
  content: "";
  border-radius: 999px;
  background: #2c7be5;
}

.visual-explain__empty {
  margin: 0;
  color: var(--sndb-ink-soft);
  font-size: 12px;
}

@media (max-width: 1120px) {
  .visual-explain__metrics {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }

  .visual-explain__layout {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 700px) {
  .visual-explain__header {
    flex-direction: column;
  }

  .visual-explain__score {
    flex-basis: auto;
    border-top: 1px solid rgba(13, 59, 102, 0.12);
    border-left: 0;
    text-align: left;
  }

  .visual-explain__metrics {
    grid-template-columns: 1fr;
  }
}
</style>
