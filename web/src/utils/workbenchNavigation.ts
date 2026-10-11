import type { Router } from 'vue-router';

/** 校验显式部署路径，不根据入站 URL 推测代理前缀。 */
export function normalizeWebBasePath(value: string | undefined): string {
  const candidate = value ?? '/';
  if (candidate === '/') return '/';
  if (!candidate.startsWith('/') || /[\\?#%\s\u0000-\u001f\u007f]/u.test(candidate)
    || candidate.includes('//') || candidate.split('/').some((part) => part === '.' || part === '..')) {
    throw new Error('SONNETDB_WEB_BASE_PATH must be an absolute pathname without URL, query, hash or dot segments.');
  }
  return candidate.endsWith('/') ? candidate : `${candidate}/`;
}

/** 只允许已注册的管理页面成为登录返回目标；保持旧 SQL 查询参数。 */
export function validatedLoginRedirect(value: unknown, router: Pick<Router, 'resolve'>): string | null {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//')
    || /[\\\u0000-\u0020\u007f]/u.test(value)) return null;
  const pathname = value.split(/[?#]/u, 1)[0];
  try {
    const decoded = decodeURIComponent(pathname);
    if (decoded.includes('\\') || decoded.includes('//') || decoded.split('/').some((part) => part === '.' || part === '..')) return null;
    const target = router.resolve(value);
    if (!target.matched.length || !target.meta.app || target.meta.anon) return null;
    return target.fullPath;
  } catch {
    return null;
  }
}
