import type { ObjectDirective } from 'vue';

export const clickoutside: ObjectDirective<HTMLElement, ((event: MouseEvent) => unknown) | undefined>;
export const mousemoveoutside: ObjectDirective<HTMLElement, ((event: MouseEvent) => unknown) | undefined>;
export const zindexable: ObjectDirective<HTMLElement, { enabled?: boolean; zIndex?: number } | undefined>;
