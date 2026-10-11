export {
  createGraphResourceDescriptor,
  createMqResourceDescriptor,
  createMqResourceIdentity,
  createResourceDescriptor,
  createResourceIdentity,
  explorerKeyForResource,
} from './resourceDescriptor';
export type {
  ResourceDescriptor,
  ResourceDescriptorInput,
  ResourceIdentity,
  ResourceIdentityInput,
  ResourceModel,
  ResourcePersistenceBoundary,
  ResourceScope,
  ResourceStability,
} from './resourceDescriptor';

export {
  CapabilityRegistry,
  createCapabilityRegistry,
  unavailableCapability,
} from './capabilityRegistry';
export type {
  CapabilityDescriptor,
  CapabilityRegistration,
  CapabilityState,
} from './capabilityRegistry';
