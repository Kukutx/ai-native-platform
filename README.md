# AI 原生平台（实验项目）

以 AI 智能体为核心执行能力的通用业务平台。人和智能体在同一个工作空间里协作：智能体像员工一样有岗位、上级、权限和预算，被指派任务或收到消息时自动醒来干活，高风险操作交给人审批。Web、桌面、移动三端共用同一套后端接口。

## 现在能做什么

- **注册即用**：注册后自动创建工作空间，并配好一位 AI 首席助理「小智」。
- **对话**：流式回复；模型可以直接调用平台动作（建任务、指派、评论、查看团队、唤醒同事、招聘智能体），所有操作都受你的权限约束。
- **智能体员工**：岗位说明、汇报关系（组织架构图）、权限、需要审批的动作、月度预算、心跳周期。被指派任务、收到评论、审批有结果、定时心跳都会唤醒它；短时间内的多次唤醒会合并成一次执行。
- **任务协作**：看板、子任务、讨论。指派给智能体后由调度器原子领取，执行过程实时可见。
- **执行记录**：每次执行的思考、调用了哪些动作、结果、token 用量和花费。
- **审批**：智能体发起高风险动作（如招聘新智能体）先进审批队列，人批准后以申请人的身份和权限执行。
- **自动化工作流**：由事件（如任务完成）、定时（cron + 时区）、外部系统调用 Webhook 地址或手动触发，按步骤执行平台动作、派任务给智能体并等它完成、让模型处理文本、调用外部 HTTP 接口、等待；支持条件分支和遍历列表（可嵌套），步骤之间用 `{{ steps.xxx.output }}` 传递数据，可设执行条件。工作流以保存者的身份和权限运行，智能体搭建的工作流需要人批准。
- **客户管理（CRM）**：客户、联系人、商机和跟进记录；销售看板拖动推进阶段，阶段可自定义，进行中、加权预测、本月赢单金额自动合计；可以给客户、联系人、商机加自定义字段（文本、数字、日期、单选、多选、是否），从 Excel 或 CSV 批量导入客户和联系人，导入前先检查哪些行有问题。对话里可以直接说"把星火科技的商机推进到商务谈判"或"整理这段会议纪要记到王经理名下"；商机赢单等变化可以触发自动化。
- **进销存（ERP）**：产品、多仓库库存、销售订单和采购订单。销售订单确认时预留库存（并发下单不会超卖），可以分批发货、登记客户退货；采购订单可以分批收货、退给供应商；每次发货、收货、退货都有单据和流水。库存按移动加权平均计算成本，订单自动算出毛利；可以从商机直接生成销售订单，库存低于提醒线可以触发自动化。对话里可以说"给远山建设下一单 50 个 W-100 并确认"、"SO-0003 先发 20 个"或"本月毛利多少"。
- **项目管理**：项目、里程碑和成员，任务归入项目和里程碑后进度、阻塞、逾期自动统计；负责人可以是智能体，项目里有任务被阻塞、里程碑的任务全部完成时会唤醒它推进。任务可以设开始日期和前置任务，甘特图上拖动调整日期，后续任务自动顺延，排期冲突和关键路径一目了然；前置任务没完成时智能体不会抢跑，完成后自动唤醒它接手。对话里可以说"建一个官网改版项目，分设计、开发、上线三个里程碑"、"联调测试要等前端开发完成"或"前端开发推迟三天，后面的任务跟着顺延"。
- **经营分析**：概览一眼看到赢单、赢率、发货、任务完成、智能体执行和 AI 花费及环比，还有销售漏斗、库存预警、项目进度和智能体工作量；自由分析可以按负责人、客户、产品、项目等拆开看趋势，导出 CSV 或保存成报表。统计按空间时区分天。对话里可以直接问"这个月赢单多少，比上个月怎样"或"近 30 天谁完成的任务最多"。
- **知识库与长期记忆**：上传 PDF、Word、Excel、PowerPoint、网页或文本文件（自动提取成带标题和表格的 Markdown），或直接录入文档，保存后立即可检索；向量（pgvector）与关键词两路召回、融合排序，中文可用。智能体可以记下学到的事实和偏好，每次醒来自动回忆相关记忆和资料；记忆分共享、成员私有、智能体私有三种。
- **外部工具与密钥**：在「集成」里接入支持 MCP 的外部服务（GitHub、Notion、内部系统等），它们的工具会和内置动作一样出现在智能体、对话和工作流里，按服务设置智能体调用是否要审批，也可以逐个停用。令牌加密保存在密钥里，配置中写 `{{ secrets.GITHUB_TOKEN }}` 引用，任何页面和记录里都看不到明文。
- **MCP 服务**：在设置里创建个人 API 密钥，就能把平台接入 Claude Code、Claude Desktop 等 MCP 客户端，直接查任务、建任务、检索知识库，权限不超过本人。
- **多租户与权限**：一个账号可加入多个空间；角色按功能授权，支持 `tasks.*`、`*` 通配；模块可按空间启用或停用，模块之间可以声明依赖（例如进销存依赖客户管理）。
- **多模型**：Claude（官方 Anthropic SDK）以及任何 OpenAI 兼容端点（国内模型、本地 Ollama）。没配密钥时自动使用内置演示模型，整个流程照样能跑通。
- **三端**：Web（Next.js）、桌面（Tauri 2，打包 Web 静态导出）、移动（Expo）。

## 技术栈

| 层 | 选型 |
| --- | --- |
| 后端 | .NET 10、ASP.NET Core、EF Core 10 + Npgsql、PostgreSQL 17（pgvector 镜像）、SignalR、Microsoft.Extensions.AI、Serilog |
| 接口契约 | OpenAPI（NSwag）→ 生成 TypeScript 客户端 `@ainative/api-client`，三端共用 |
| Web | Next.js 16、React 19、Tailwind CSS 4、shadcn/ui（Radix）、TanStack Query |
| 桌面 | Tauri 2（Rust），内嵌 Web 的静态导出 |
| 移动 | Expo SDK 57、Expo Router、React Native |
| 工程 | pnpm 12 workspace + Turborepo，xUnit v3 + Testcontainers |

## 快速开始

前置条件：.NET 10 SDK、Node.js 24 与 pnpm 12（`corepack enable`）、Docker。桌面端另需 Rust 工具链；移动端需要 Expo Go 或开发构建。

```bash
pnpm install
pnpm db:up        # 启动 PostgreSQL（docker compose）
pnpm dev:api      # http://localhost:5080，开发环境启动时自动迁移数据库，接口文档在 /swagger
pnpm dev:web      # http://localhost:3000
```

打开 http://localhost:3000 注册，即可开始和小智对话、建任务。

### 配置模型

不配置时使用演示模型。接入 Claude 只需提供密钥（二选一）：

```bash
export ANTHROPIC_API_KEY=sk-ant-...
# 或者
dotnet user-secrets set "Ai:Providers:anthropic:ApiKey" "sk-ant-..." --project backend/AiNative.Api
```

接入 OpenAI 兼容端点（例如本地 Ollama），在配置里增加一个供应商：

```json
"Ai": {
  "Providers": {
    "ollama": { "Kind": "openai", "BaseUrl": "http://localhost:11434/v1", "Models": ["qwen3"] }
  },
  "Pricing": { "qwen3": { "InputPerMillion": 0, "OutputPerMillion": 0 } }
}
```

每个智能体可以单独选择供应商和模型；`Ai:DefaultProvider` / `Ai:DefaultModel` 决定新智能体和对话的默认值。

### 配置向量模型

不配置时知识库使用内置的离线演示向量（只看字面相似，关键词检索不受影响）。语义检索需要一个 OpenAI 兼容的向量模型，指向 `Ai:Providers` 里协议为 `openai` 的供应商即可：

```json
"Ai": {
  "Providers": {
    "openai": { "Kind": "openai", "ApiKey": "sk-..." }
  },
  "Embeddings": { "Provider": "openai", "Model": "text-embedding-3-small" }
}
```

本地也可以用 Ollama（例如 `"Provider": "ollama", "Model": "bge-m3"`）。向量统一存为 1024 维，更换模型后后台会自动逐批重建旧向量。

### 接入 Claude Code（MCP）

在 Web 端「设置 → API 密钥」创建密钥，对话框里会给出完整命令，形如：

```bash
claude mcp add --transport http ainative http://localhost:5080/mcp --header "Authorization: Bearer ank_..."
```

MCP 服务地址是 `/mcp`（Streamable HTTP）。工具就是平台动作，可用哪些取决于密钥所属成员的权限；只读密钥只能看到查询类工具。

### 桌面端

```bash
pnpm dev:desktop     # 启动 Web 开发服务器并打开桌面窗口
pnpm build:desktop   # 静态导出 Web 并打包安装程序
```

打包前用 `NEXT_PUBLIC_API_URL` 指定后端地址；后端的 `Cors:AllowedOrigins` 已包含 Tauri 的来源。

### 移动端

```bash
cp apps/mobile/.env.example apps/mobile/.env   # 真机调试时把地址改成电脑的局域网 IP
pnpm dev:mobile
```

登录页可以临时修改服务器地址。对话使用 `expo/fetch` 读取流式响应，任务和审批通过 SignalR 实时刷新。

### 生产部署

一个镜像同时提供接口、实时通道、MCP 服务和 Web 工作台，再加一个 PostgreSQL：

```bash
cd deploy
cp .env.example .env      # 填写数据库密码和令牌签名密钥
docker compose up -d --build
```

打开 http://服务器:8080 即可使用。需要多个实例时加上 `-f docker-compose.cluster.yml`（两个副本加 nginx，实例之间只靠 PostgreSQL 协调，不需要 Redis）。HTTPS 反向代理、全部配置项、关闭自助注册、多实例、备份与升级见 [docs/deployment.md](docs/deployment.md)。

## 常用命令

| 命令 | 作用 |
| --- | --- |
| `pnpm type-check` / `pnpm lint` | 全部前端包的类型检查与 lint |
| `pnpm build` | 构建 Web |
| `pnpm test:api` | 后端单元测试 + 集成测试（集成测试需要 Docker，或设置 `AINATIVE_TEST_POSTGRES` 指向现有库） |
| `pnpm api:generate` | 后端接口变更后重新导出 OpenAPI 并生成 TS 客户端 |

## 目录

```
backend/
  AiNative.Domain            领域模型（租户、成员、角色、智能体、唤醒、执行、任务、审批、对话、知识与记忆、API 密钥、工作流、客户管理、进销存、项目管理、经营分析）
  AiNative.Application       用例、权限、平台动作、模块与事件、智能体运行时、工作流、对话、知识检索、客户管理、进销存、项目管理、经营分析指标
  AiNative.Infrastructure    EF Core、调度器、工作流引擎、唤醒队列、模型网关、向量化与混合检索、令牌
  AiNative.Contracts         对外 DTO
  AiNative.Api               HTTP 控制器、SignalR、MCP 服务、认证、OpenAPI
  AiNative.OpenApi.Export    不连数据库导出 OpenAPI 文档
  AiNative.UnitTests / AiNative.IntegrationTests
packages/api-client          生成的 TS 客户端 + 流式对话 + 实时通道
apps/web                     Next.js 工作台
apps/desktop                 Tauri 2 桌面端
apps/mobile                  Expo 移动端
deploy/                      生产部署示例（docker compose + 环境变量模板），镜像见根目录 Dockerfile
docs/                        架构说明、部署说明与参考项目
```

更多设计细节见 [docs/architecture.md](docs/architecture.md)，部署见 [docs/deployment.md](docs/deployment.md)，参考过的开源项目与许可说明见 [docs/references.md](docs/references.md)，参与开发的约定见 [AGENTS.md](AGENTS.md)。
