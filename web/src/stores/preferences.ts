import { defineStore } from 'pinia';
import { ref, watch } from 'vue';

const key = 'sndb.preferences.v1';
export const refreshIntervals = [0, 15, 30, 60] as const;

/** 当前浏览器的诊断刷新偏好，不改变服务配置或其它用户。 */
export const usePreferencesStore = defineStore('preferences', () => {
  const refreshSeconds = ref(30);
  const storageError = ref('');
  try {
    const value = JSON.parse(localStorage.getItem(key) ?? 'null')?.refreshSeconds;
    if (refreshIntervals.includes(value)) refreshSeconds.value = value;
  } catch { storageError.value = '浏览器未能读取偏好，已使用默认值。'; }
  watch(refreshSeconds, (value) => {
    if (!refreshIntervals.includes(value as typeof refreshIntervals[number])) return;
    try {
      localStorage.setItem(key, JSON.stringify({ refreshSeconds: value }));
      storageError.value = '';
    } catch { storageError.value = '浏览器禁止保存偏好，本次会话仍然有效。'; }
  });
  return { refreshSeconds, storageError };
});
