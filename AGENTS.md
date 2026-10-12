# 开发约定

本文件写给在这个仓库里工作的人和 AI 编码助手。项目定位、快速开始见 [README.md](README.md)，整体设计见 [docs/architecture.md](docs/architecture.md)。

## 基本原则

- 代码注释、提交说明、界面文案、错误提示使用中文；标识符用英文。
- 只使用仓库已经在用的技术。不要引入 Yarn、bun、deno、NestJS、Express、Prisma、Vue、Svelte、Remix、MySQL、MongoDB 等；确实需要新依赖时先说明理由。
- 依赖用最新稳定版。移动端例外：React、React Native 等版本以 Expo SDK 锁定的为准（用 `expo install` 添加原生依赖）。
- `@open-mercato/enterprise` 是商业许可，不要阅读或复制。dify、twenty 等许可受限（AGPL/GPL 或附加条款）的项目只作设计参考，不复制代码（见 [docs/references.md](docs/references.md)）。

## 常用命令

```bash
pnpm install
pnpm db:up                 # PostgreSQL
pnpm dev:api               # 后端，http://localhost:5080
pnpm dev:web               # Web，http://localhost:3000
pnpm type-check && pnpm lint
pnpm test:api              # 后端全部测试
pnpm test:client           # 共享 TS 客户端的单元测试（node:test）
pnpm api:generate          # 接口变化后重新生成 TS 客户端
```

只跑某个测试项目：`dotnet test --project backend/AiNative.IntegrationTests/AiNative.IntegrationTests.csproj`。集成测试默认用 Testcontainers 拉起 PostgreSQL；已有数据库时设置 `AINATIVE_TEST_POSTGRES` 指向它（测试会为每个用例新建独立数据库）。

新增 EF 迁移：

```bash
cd backend
dotnet ef migrations add <名称> --project AiNative.Infrastructure --startup-project AiNative.Api --output-dir Persistence/Migrations
```

## 后端分层

依赖方向：`Api → Application → Domain`，`Infrastructure → Application`，`Contracts` 只放对外 DTO。

- **Domain**：实体与不变量。不依赖 EF、不读时钟以外的外部状态。租户数据实现 `ITenantScoped`。
- **Application**：用例写成 `ICommand<T>` / `IQuery<T>` + 处理器，处理器按约定自动注册。请求上用 `[RequireFeature("tasks.manage")]` 声明权限，管道依次执行：权限与模块检查 → FluentValidation → 日志 → 事务（只包写操作，提交成功后才执行登记的后续动作，例如实时通知、唤醒信号）。
- **Infrastructure**：EF Core、调度器、唤醒队列、模型网关等实现。
- **Api**：控制器只做路由和绑定，调用 `SendAsync` 发出请求；错误统一转成带 `code` 的 ProblemDetails。

约定：

- 处理器不要自己调用 `SaveChanges` 来提交，事务管道负责；需要在事务外执行的代码用 `UnitOfWork.RunAsync`。
- 新实体被跟踪时就会写入 `CreatedAt` 和当前租户，保存前返回的 DTO 也是准确的。
- 并发敏感的写操作用条件更新（`ExecuteUpdate` + 状态条件）或数据库锁，不要先读后写。
- JSON 统一走 `PlatformJson`：camelCase、枚举序列化为 snake_case 字符串、忽略 null、中文不转义。

## 新增业务能力

平台的扩展单位是**模块**（`IPlatformModule`）和**动作**（`IPlatformAction`）。

1. 在模块里声明功能权限（`FeatureDefinition`），权限键形如 `crm.contacts.view`。
2. 写命令/查询与处理器，标注 `[RequireFeature]`。
3. 需要让智能体或对话使用的能力，写一个 `PlatformAction<TInput, TOutput>`：`Key` 形如 `crm.create_contact`，`Description` 写给模型看（做什么、何时用、注意事项），输入类型会自动生成 JSON Schema。高风险动作设 `ApprovalPolicy.RequiredForAgents`。
4. 业务变化需要能触发工作流时，在模块的 `Events` 里声明 `PlatformEventDefinition`（键形如 `crm.contact_created`，`Description` 写清载荷字段），并在写操作的处理器里调用 `IPlatformEvents.PublishAsync`（载荷用委托惰性构造，与业务变化同一事务提交）。
5. 类放进已注册的程序集即可被扫描；独立程序集用 `services.AddPlatformModuleAssembly(assembly)`。
6. 补测试：权限、租户隔离、关键状态流转至少各一条。

同一个动作定义同时服务于界面（`/api/actions`）、智能体工具调用、对话、工作流步骤和 MCP 服务（`/mcp`），权限与审批在调用入口统一检查，不要在各处重复实现。

## 接口与前端

- 后端接口变化后运行 `pnpm api:generate`，提交 `packages/api-client/openapi.json` 和 `src/generated.ts`。不要手改生成文件。
- 前端只通过 `@ainative/api-client` 访问后端（Web 端 ESLint 禁止在 `lib/api.ts` 之外直接用 `fetch`）。
- 流式对话走 SSE（`streamChat`），实体变化走 SignalR（`RealtimeClient`），前端据此让 TanStack Query 失效重取，不要轮询。
- Web 详情页用查询参数（`/tasks/view?id=`），保证 `NEXT_OUTPUT=export` 静态导出后可以直接被 Tauri 加载；使用 `useSearchParams` 的组件要包在 `<Suspense>` 里。
- UI 组件在 `apps/web/src/components/ui`（shadcn），业务组件放 `features/<领域>`。

## 提交前检查

```bash
pnpm type-check && pnpm lint && pnpm build
pnpm test:api && pnpm test:client
```

改动 `Dockerfile`、静态页托管或启动配置时，本地跑一次 `docker build -t ainative-platform .`，再用 `deploy/docker-compose.yml` 起一套确认能打开页面（见 [docs/deployment.md](docs/deployment.md)）。

改动桌面端时在装有 Rust 的机器上跑一次 `pnpm --filter @ainative/desktop check`（即 `cargo check`；桌面端没有 TS 代码，不参与 type-check 和 lint，CI 里单独检查）；改动移动端时跑 `pnpm --filter @ainative/mobile bundle:check` 确认能打出安卓包。

<!-- BEGIN:turborepo-agent-rules -->

# This is NOT the Turborepo you know

Turborepo configuration, task behavior, and CLI commands can vary between installed versions and may differ from your training data. Resolve the `turbo` package from this file's directory or relevant workspace; in monorepos, it may not be visible from the repository root. For example, run `node -p "require.resolve('turbo/package.json')"` from a workspace that depends on `turbo`.

Read `docs/README.md` inside that installed package first, then read the relevant pages from its `docs/` directory before changing Turborepo configuration or commands. Heed deprecation notices. These bundled docs match the installed package version and are available without network access.

This block is written and re-added by `turbo` before repository-scoped commands when an AI agent is detected. In the Turborepo source repository, its template is defined in `crates/turborepo-cli/src/cli/agent_guidance.rs`. Removing the managed block while updates are enabled means a later qualifying invocation will add it again. Set `"agentGuidance": false` in the root `turbo.json` or `turbo.jsonc` to opt out; this does not remove an existing block. Keep the block committed with your work to avoid an uncommitted change on the next agent invocation.
<!-- END:turborepo-agent-rules -->
