/** 能力在当前宿主中的合同状态。该状态不是用户权限判断。 */
export type CapabilityState = 'existing' | 'extension' | 'planned' | 'unavailable';

/**
 * 可注册的能力描述。
 *
 * id 是跨 Web、Studio 与 VS Code 的稳定标识；label/description 只用于
 * 展示合同，不授予权限，也不会触发任何真实请求。
 */
export interface CapabilityRegistration {
  readonly id: string;
  readonly state: CapabilityState;
  readonly label?: string;
  readonly description?: string;
  readonly metadata?: Readonly<Record<string, string>>;
}

/** 已注册能力的不可变快照项。 */
export interface CapabilityDescriptor extends CapabilityRegistration {
  readonly known: boolean;
}

function requireCapabilityId(id: string): string {
  if (id.length === 0) throw new Error('capability id must not be empty');
  return id;
}

function freezeCapability(registration: CapabilityRegistration, known: boolean): CapabilityDescriptor {
  const metadata = registration.metadata === undefined
    ? undefined
    : Object.freeze({ ...registration.metadata });
  return Object.freeze({
    id: requireCapabilityId(registration.id),
    state: registration.state,
    ...(registration.label === undefined ? {} : { label: registration.label }),
    ...(registration.description === undefined ? {} : { description: registration.description }),
    ...(metadata === undefined ? {} : { metadata }),
    known,
  });
}

/** 未知能力的安全回退结果，不执行任何客户端或服务器请求。 */
export function unavailableCapability(id: string): CapabilityDescriptor {
  return freezeCapability({ id, state: 'unavailable' }, false);
}

/**
 * 纯内存能力注册表。
 *
 * Registry 只回答稳定 id 对应的合同状态；它不把客户端显示状态当权限，
 * 也不包含请求执行器。register 会替换同 id 的旧描述，以支持宿主启动时
 * 根据扩展清单完成一次确定性的注册。
 */
export class CapabilityRegistry {
  private readonly values = new Map<string, CapabilityDescriptor>();

  /**
   * 创建注册表并注册初始能力。
   */
  public constructor(initial: readonly CapabilityRegistration[] = []) {
    for (const registration of initial) this.register(registration);
  }

  /** 注册或替换一个稳定 id。 */
  public register(registration: CapabilityRegistration): this {
    const descriptor = freezeCapability(registration, true);
    this.values.set(descriptor.id, descriptor);
    return this;
  }

  /** 批量注册能力，返回当前实例以便串联初始化。 */
  public registerMany(registrations: readonly CapabilityRegistration[]): this {
    for (const registration of registrations) this.register(registration);
    return this;
  }

  /** 按稳定 id 查询已注册能力；未知 id 返回 undefined。 */
  public get(id: string): CapabilityDescriptor | undefined {
    return this.values.get(id);
  }

  /**
   * 按稳定 id 查询能力。未知 id 安全返回 unavailable，供 UI 显示或规划
   * 判断使用；结果不代表用户权限，也不会发出真实请求。
   */
  public resolve(id: string): CapabilityDescriptor {
    return this.values.get(id) ?? unavailableCapability(id);
  }

  /** 判断能力是否已登记；不会把状态解释为权限。 */
  public has(id: string): boolean {
    return this.values.has(id);
  }

  /** 当前登记数。 */
  public get size(): number {
    return this.values.size;
  }

  /**
   * 取得不可变能力快照。返回数组及其元素均已冻结，后续 register 不会
   * 改变该快照。
   */
  public snapshot(): readonly CapabilityDescriptor[] {
    return Object.freeze([...this.values.values()]);
  }

  /** snapshot 的语义别名，便于宿主按“能力清单”命名。 */
  public list(): readonly CapabilityDescriptor[] {
    return this.snapshot();
  }
}

/** 创建纯内存能力注册表。 */
export function createCapabilityRegistry(
  initial: readonly CapabilityRegistration[] = [],
): CapabilityRegistry {
  return new CapabilityRegistry(initial);
}
