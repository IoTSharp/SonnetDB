import { createApp } from 'vue';
import { createPinia } from 'pinia';
import naive from 'naive-ui';
import App from './App.vue';
import router from './router';
import './styles.css';
import './styles/studio-workbench.css';
import { verifyPreviewIdentity } from './preview/identity';

void verifyPreviewIdentity().then(() => {
  const app = createApp(App);
  app.use(createPinia());
  app.use(router);
  app.use(naive);
  app.mount('#app');
}).catch(() => {
  for (const key of ['sndb.auth', 'sndb.sql.console.tabs.v1', 'sndb.workbench.history.v1']) localStorage.removeItem(key);
  const target = document.querySelector('#app');
  if (target) {
    const heading = document.createElement('h1');
    heading.textContent = 'Preview 版本不匹配';
    const message = document.createElement('p');
    message.textContent = '无法核验同一候选的 Server/Web。请停止操作并重新解压完整匹配包；旧结果与审批不会重放。';
    target.replaceChildren(heading, message);
  }
});
