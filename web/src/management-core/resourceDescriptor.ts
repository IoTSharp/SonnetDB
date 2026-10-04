/**
 * 工作台支持的资源模型。
 *
 * 该联合类型只描述管理工作台的资源边界，不代表服务器端存储实现。
 */
export type ResourceModel =
  | 'measurement'
  | 'table'
  | 'document'
  | 'kv'
  | 'index'
  | 'vector'
  | 'fulltext'
  | 'mq'
  | 'bucket'
  | 'graph'
  | 'backup';

/** 资源逻辑作用域与物理持久化作用域。 */
export type ResourceScope = 'database' | 'instance';

/** 资源稳定性标记。Graph 当前仅以 Beta 形式提供。 */
export type ResourceStability = 'stable' | 'beta';

/**
 * 工作台资源的稳定身份。
 *
 * name、topic 与 key 均保留调用方的原始拼写。未加引号标识符的大小写
 * 解析由现有 SQL/binder 负责，本合同不会自行折叠大小写。
 */
export interface ResourceIdentity {
  /** 所属数据库名称；实例级资源也可使用其管理上下文中的数据库名称。 */
  readonly database: string;
  /** 资源模型。 */
  readonly model: ResourceModel;
  /** 资源原始名称；MQ 资源等于 topic。 */
  readonly name: string;
  /** 兼容现有 Explorer 与路由的资源 key。 */
  readonly key: string;
  /** 兼容旧 Explorer 的 key；未提供时与 key 相同。 */
  readonly legacyKey: string;
  /** MQ 资源的原始 topic；其它模型不设置该字段。 */
  readonly topic?: string;
}

/** 资源在实例上的物理持久化边界。 */
export interface ResourcePersistenceBoundary {
  /** 物理持久化作用域。 */
  readonly scope: ResourceScope;
  /** 物理相对路径或逻辑位置，例如实例 MQ 的 .system/mq。 */
  readonly path?: string;
  /** 是否与多个数据库共享该物理边界。 */
  readonly shared: boolean;
  /** 单库备份是否覆盖该物理边界。 */
  readonly includedInDatabaseBackup: boolean;
}

/** 创建资源身份时使用的输入。 */
export interface ResourceIdentityInput {
  readonly database: string;
  readonly model: ResourceModel;
  readonly name: string;
  /** 保留已有路由或 Explorer key，例如 index:...、backup-status。 */
  readonly key?: string;
  readonly legacyKey?: string;
  /** MQ 身份必须显式同时携带 database 与 topic。 */
  readonly topic?: string;
}

/** 创建资源描述时使用的输入。 */
export interface ResourceDescriptorInput extends ResourceIdentityInput {
  /** 逻辑访问作用域；默认是 database。 */
  readonly scope?: ResourceScope;
  /** 物理持久化作用域；默认跟随 scope。 */
  readonly persistenceScope?: ResourceScope;
  readonly persistencePath?: string;
  readonly sharedPersistence?: boolean;
  readonly includedInDatabaseBackup?: boolean;
  readonly stability?: ResourceStability;
  readonly beta?: boolean;
}

/**
 * 带有边界与稳定性信息的资源描述。
 *
 * identity 同时以嵌套和扁平只读字段暴露，方便 Web、Studio、VS Code
 * 在不复制解析逻辑的情况下逐步接入；两处值由工厂保持一致。
 */
export interface ResourceDescriptor extends ResourceIdentity {
  readonly identity: ResourceIdentity;
  /** 逻辑资源作用域。 */
  readonly scope: ResourceScope;
  /** 物理持久化作用域。 */
  readonly persistenceScope: ResourceScope;
  readonly persistence: ResourcePersistenceBoundary;
  readonly stability: ResourceStability;
  /** stability 为 beta 时为 true；Graph 描述必为 true。 */
  readonly beta: boolean;
}

function requireText(value: string, field: string): string {
  if (value.length === 0) throw new Error(`${field} must not be empty`);
  return value;
}

function defaultResourceKey(input: ResourceIdentityInput): string {
  if (input.key !== undefined) return requireText(input.key, 'key');
  switch (input.model) {
    case 'measurement':
      return input.name;
    case 'table':
      return `table:${input.name}`;
    case 'document':
      return `document:${input.name}`;
    case 'kv':
      return `kv:${input.name}`;
    case 'index':
      // Index lifecycle ids are already compound keys. Preserve the supplied name verbatim.
      return input.name;
    case 'vector':
      return `vector:${input.name}`;
    case 'fulltext':
      return `fulltext:${input.name}`;
    case 'mq':
      return `mq:${input.topic ?? input.name}`;
    case 'bucket':
      return `bucket:${input.name}`;
    case 'graph':
      return `graph:${input.name}`;
    case 'backup':
      return input.name === 'backup-status' ? input.name : input.name;
  }
}

/**
 * 创建保留原始名称与兼容 key 的资源身份。
 */
export function createResourceIdentity(input: ResourceIdentityInput): ResourceIdentity {
  const database = requireText(input.database, 'database');
  const name = requireText(input.name, 'name');
  const topic = input.model === 'mq'
    ? requireText(input.topic ?? '', 'topic')
    : undefined;
  if (topic !== undefined && topic !== name) {
    throw new Error('MQ resource name must equal topic');
  }
  const key = defaultResourceKey({ ...input, database, name, topic });
  const legacyKey = input.legacyKey === undefined
    ? key
    : requireText(input.legacyKey, 'legacyKey');

  return Object.freeze({
    database,
    model: input.model,
    name,
    key,
    legacyKey,
    ...(topic === undefined ? {} : { topic }),
  });
}

/**
 * 创建资源描述并明确其逻辑与物理边界。
 */
export function createResourceDescriptor(input: ResourceDescriptorInput): ResourceDescriptor {
  const identity = createResourceIdentity(input);
  const scope = input.scope ?? 'database';
  const persistenceScope = input.persistenceScope ?? scope;
  const shared = input.sharedPersistence ?? persistenceScope === 'instance';
  const includedInDatabaseBackup = input.includedInDatabaseBackup
    ?? persistenceScope === 'database';
  const stability = input.model === 'graph' ? 'beta' : (input.stability ?? 'stable');
  const beta = input.model === 'graph' ? true : (input.beta ?? stability === 'beta');
  const persistence = Object.freeze({
    scope: persistenceScope,
    ...(input.persistencePath === undefined ? {} : { path: input.persistencePath }),
    shared,
    includedInDatabaseBackup,
  });

  return Object.freeze({
    ...identity,
    identity,
    scope,
    persistenceScope,
    persistence,
    stability,
    beta,
  });
}

/**
 * 创建 MQ 资源身份。database 与 topic 都是身份的一部分，旧 Explorer key
 * 固定保持为 `mq:${topic}`，因此 topic 中的大小写和冒号均不会丢失。
 */
export function createMqResourceIdentity(database: string, topic: string): ResourceIdentity {
  return createResourceIdentity({
    database,
    model: 'mq',
    name: topic,
    topic,
    key: `mq:${topic}`,
    legacyKey: `mq:${topic}`,
  });
}

/**
 * 创建实例 MQ 的资源描述。
 *
 * MQ 在逻辑上属于 database，但当前单例服务把共享追加日志保存在实例
 * DataRoot/.system/mq；单库备份不覆盖这段共享日志。
 */
export function createMqResourceDescriptor(database: string, topic: string): ResourceDescriptor {
  return createResourceDescriptor({
    ...createMqResourceIdentity(database, topic),
    scope: 'database',
    persistenceScope: 'instance',
    persistencePath: '.system/mq',
    sharedPersistence: true,
    includedInDatabaseBackup: false,
  });
}

/**
 * 创建 Graph 资源描述。Graph 始终明确标记为 Beta。
 */
export function createGraphResourceDescriptor(
  database: string,
  name: string,
  key?: string,
): ResourceDescriptor {
  return createResourceDescriptor({
    database,
    model: 'graph',
    name,
    ...(key === undefined ? {} : { key }),
    stability: 'beta',
    beta: true,
  });
}

/**
 * 返回资源的 Explorer/路由 key，作为统一合同入口保留旧 key。
 * 该 key 只用于路由兼容；跨数据库选择必须同时使用 resource.database。
 */
export function explorerKeyForResource(resource: ResourceIdentity | ResourceDescriptor): string {
  return resource.key;
}
