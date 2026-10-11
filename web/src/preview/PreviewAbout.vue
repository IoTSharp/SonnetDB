<template>
  <section class="preview-about" data-testid="preview-about">
    <h1>关于 SonnetDB Workbench Preview 1</h1>
    <p>SonnetDB：九种数据模型，各有原生语义，共享一套引擎。</p>
    <p>当前提供关系表、时序和有界只读 SQL；获授权用户可批准一次关系表单行插入。其它模型的专用工作台显示延期原因。</p>
    <p>每次操作最多30秒；结果最多1000行，关系表200行、时序500行。响应、保留结果和导出各最多4 MiB，输入最多64 KiB。</p>
    <p>取消不等于回滚。写入状态未知时，需要核对服务器；旧审批不会自动重放。</p>
    <p>Graph 保持 Beta。MQ 以 database + Topic 区分，数据位于实例 .system/mq；单库备份不包含实例 MQ。</p>
    <p>版本：{{ version }}；源码：{{ sourceCommit }}；profile：preview-1 / R1。</p>
    <p>仅支持同源环回连接；实际发行物首次部署与发布门禁尚待验收。</p>
  </section>
</template>
<script setup lang="ts">
const version = import.meta.env.VITE_WORKBENCH_VERSION ?? '本地开发';
const sourceCommit = import.meta.env.VITE_WORKBENCH_SOURCE_COMMIT ?? '未绑定发行源码';
</script>
<style scoped>.preview-about { max-width: 900px; padding: 32px; line-height: 1.8; }</style>
