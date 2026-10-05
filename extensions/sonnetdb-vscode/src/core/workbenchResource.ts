import type { TreeNode } from '../tree/sonnetdbTreeDataProvider';
import type { WorkbenchResource, WorkbenchResourceModel, WorkbenchTarget } from './types';

/** 将已有 Explorer 节点投影到数据库资源身份，不折叠名称或解析 SQL。 */
export function workbenchTargetFromNode(node: TreeNode | undefined): WorkbenchTarget | undefined {
  if (!node) return undefined;
  if (node.kind === 'database') {
    requireName(node.name, 'database');
    return Object.freeze({ database: node.name });
  }

  let resource: WorkbenchResource;
  switch (node.kind) {
    case 'measurement':
      resource = describeResource(node.database, 'measurement', node.measurement.name, node.measurement.name);
      break;
    case 'table':
      resource = describeResource(node.database, 'table', node.table.name, `table:${node.table.name}`);
      break;
    case 'document':
      resource = describeResource(node.database, 'document', node.collection.name, `document:${node.collection.name}`);
      break;
    case 'kvKeyspace':
      resource = describeResource(node.database, 'kv', node.keyspace, `kv:${node.keyspace}`);
      break;
    case 'index':
      resource = describeResource(node.database, 'index', node.index.name, node.index.id);
      break;
    case 'vectorIndex':
      resource = describeResource(node.database, 'vector', `${node.index.measurement}.${node.index.column}`,
        `vector:${node.index.measurement}:${node.index.column}`);
      break;
    case 'fullTextIndex':
      resource = describeResource(node.database, 'fulltext', `${node.index.collection}.${node.index.name}`,
        `fulltext:${node.index.collection}:${node.index.name}`);
      break;
    case 'mqTopic':
      resource = describeResource(node.database, 'mq', node.topic.topic, `mq:${node.topic.topic}`);
      break;
    case 'objectBucket':
      resource = describeResource(node.database, 'bucket', node.bucket.name, `bucket:${node.bucket.name}`);
      break;
    case 'graph':
      resource = describeResource(node.database, 'graph', node.graph.name, `graph:${node.graph.name}`);
      break;
    case 'backup':
      resource = describeResource(node.database, 'backup', 'backup-status', 'backup-status');
      break;
    default:
      return undefined;
  }
  return Object.freeze({ database: resource.database, resource });
}

/** 生成只导航的 Web Admin 链接；不携带凭据、查询正文或执行参数。 */
export function buildWorkbenchUrl(baseUrl: string, target: WorkbenchTarget): string {
  const url = new URL(baseUrl);
  if ((url.protocol !== 'http:' && url.protocol !== 'https:') || url.username || url.password) {
    throw new Error('Workbench requires an HTTP(S) server URL without embedded credentials.');
  }
  requireName(target.database, 'database');
  if (target.resource && target.resource.database !== target.database) {
    throw new Error('Workbench resource belongs to a different database.');
  }

  url.pathname = `${url.pathname.replace(/\/+$/u, '')}/admin/app/sql`;
  url.search = '';
  url.hash = '';
  url.searchParams.set('database', target.database);
  const resource = target.resource;
  if (resource) {
    url.searchParams.set('model', resource.model);
    if (resource.model !== 'index' && resource.model !== 'backup') {
      url.searchParams.set('tool', resource.model);
    }
    // Compound index identities use the existing route-compatible key so an
    // index display name does not choose another owner with the same name.
    const routeNode = resource.model === 'index' || resource.model === 'vector' || resource.model === 'fulltext'
      ? resource.legacyKey
      : resource.name;
    url.searchParams.set('node', routeNode);
  }
  return url.toString();
}

function describeResource(
  database: string,
  model: WorkbenchResourceModel,
  name: string,
  legacyKey: string,
): WorkbenchResource {
  requireName(database, 'database');
  requireName(name, 'resource name');
  requireName(legacyKey, 'legacy key');
  const isMq = model === 'mq';
  const persistenceScope = isMq ? 'instance' : 'database';
  return Object.freeze({
    database,
    model,
    name,
    key: legacyKey,
    legacyKey,
    ...(isMq ? { topic: name } : {}),
    scope: 'database',
    persistenceScope,
    persistence: Object.freeze({
      scope: persistenceScope,
      ...(isMq ? { path: '.system/mq' } : {}),
      shared: isMq,
      includedInDatabaseBackup: !isMq,
    }),
    stability: model === 'graph' ? 'beta' : 'stable',
    beta: model === 'graph',
  });
}

function requireName(value: string, field: string): void {
  if (value.length === 0) throw new Error(`Workbench ${field} must not be empty.`);
}
