import '@tanstack/react-query';

/** 写操作可以声明的附加行为，由 Providers 里的 MutationCache 统一处理。 */
export type AppMutationMeta = {
  /** 成功后弹出的提示，例如「已批准」。 */
  successMessage?: string;
  /** 返回 true 的错误由界面自己展示（例如编辑冲突的处理选项），不再弹出全局错误提示。 */
  handledError?: (error: unknown) => boolean;
};

declare module '@tanstack/react-query' {
  interface Register {
    mutationMeta: AppMutationMeta;
  }
}
