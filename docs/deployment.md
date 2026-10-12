# 部署

## 组成

- **一个应用镜像**（仓库根目录的 `Dockerfile`）：ASP.NET Core 后端，同时提供接口、实时通道（SignalR）、MCP 服务和 Web 工作台。Web 工作台以静态导出放在镜像的 `wwwroot` 里，页面和接口同源，不需要跨域配置，也不需要单独的 Web 服务器。容器监听 8080，以非 root 用户运行。
- **PostgreSQL 17 + pgvector**。

静态资源在构建镜像时预先压缩成 br 和 gzip，按浏览器支持直接发送；文件名带哈希的脚本和样式永久缓存，页面每次向服务器确认是否有新版本，升级后刷新即可拿到新界面。

## 用 Docker Compose 部署

需要 Docker（含 Compose 插件）。

```bash
git clone <仓库地址> ai-native-platform
cd ai-native-platform/deploy
cp .env.example .env      # 填写 POSTGRES_PASSWORD 和 Auth__SigningKey
docker compose up -d --build
docker compose logs -f app
```

日志里出现 `Application started` 后打开 `http://服务器:8080`，注册第一个空间。首次启动会自动建表（数据库迁移）。

生成密钥：

```bash
openssl rand -hex 24      # 数据库密码
openssl rand -base64 48   # 令牌签名密钥
```

对公网提供服务前，请先完成下面的 HTTPS 和「关闭自助注册」两步。

## 配置

所有配置都可以用环境变量覆盖，层级之间用两个下划线：`Auth:SigningKey` 写成 `Auth__SigningKey`，数组写成 `Cors__AllowedOrigins__0`。Compose 部署时把变量写进 `deploy/.env` 即可，应用容器会原样读取。

| 变量 | 说明 |
| --- | --- |
| `Auth__SigningKey` | **必填**。登录令牌的签名密钥，至少 32 个字符。缺失、太短或沿用仓库里的开发密钥时拒绝启动。更换后所有成员需要重新登录（API 密钥不受影响）；没有设置 `Secrets__MasterKey` 时，「集成」里已保存的密钥也会解不开。 |
| `Auth__AllowRegistration` | 是否允许自助注册新空间，默认 `true`。私有部署建好自己的空间后设为 `false`，之后由管理员在「设置 → 成员」里添加成员（可设置初始密码）。 |
| `Auth__AccessTokenLifetime` | 登录有效期，默认 `7.00:00:00`（7 天）。 |
| `ConnectionStrings__Postgres` | 数据库连接字符串。Compose 里已按 `POSTGRES_PASSWORD` 拼好。 |
| `Database__MigrateOnStartup` | 启动时自动迁移数据库。开发环境默认开，其他环境默认关；示例 Compose 里打开。 |
| `ANTHROPIC_API_KEY` | Claude 的密钥。不配置任何模型时使用内置演示模型。 |
| `Ai__Providers__<名称>__Kind` / `__BaseUrl` / `__ApiKey` / `__Models__0` | 增加模型供应商。`Kind` 为 `anthropic` 或 `openai`（OpenAI 及兼容端点，如 DeepSeek、通义、Ollama、vLLM）。 |
| `Ai__DefaultProvider` / `Ai__DefaultModel` | 新智能体和对话的默认模型。 |
| `Ai__Embeddings__Provider` / `Ai__Embeddings__Model` | 知识库语义检索用的向量模型（协议须为 `openai`）。不配置时只用关键词与字面相似检索。 |
| `Ai__Pricing__<模型>__InputPerMillion` / `__OutputPerMillion` | 自定义模型单价（美元 / 百万 token），用于预算和用量统计。 |
| `Scheduler__MaxConcurrentRuns` | 同时执行的智能体数量上限，默认 4。 |
| `OutboundNetwork__AllowPrivateNetworkRequests` | 是否允许工作流的「HTTP 请求」步骤和接入的外部 MCP 服务访问内网和本机地址，默认 `false`。需要调用同一内网里的服务时再打开。（旧名 `Workflows__AllowPrivateNetworkRequests` 已不再生效。） |
| `Workflows__MaxEventRunsPerMinute` | 单个工作流每分钟最多被事件或 Webhook 触发的次数，默认 30。 |
| `Secrets__MasterKey` | 加密「集成 → 密钥」的主密钥，32 字节随机数的 Base64，用 `openssl rand -base64 32` 生成。不设置时由 `Auth__SigningKey` 派生，能用，但之后再更换签名密钥会让已保存的密钥全部解不开，所以生产环境建议设置。主密钥不进数据库，请和数据库备份分开保存。 |
| `Secrets__PreviousMasterKeys__0` | 更换主密钥时把旧主密钥放这里，旧密钥照样能解开；在界面上重新保存一遍各个密钥后就可以去掉。先用派生密钥、后来才设置 `Secrets__MasterKey` 的部署不需要这一项（派生的那把总会参与解密，只要签名密钥没换）。 |
| `Integrations__McpConnectTimeout` / `Integrations__McpCallTimeout` | 连接外部 MCP 服务、调用一次外部工具的超时，默认 `00:00:30`、`00:01:00`。 |
| `Integrations__MaxToolResultChars` | 外部工具返回文本的最大长度，超出部分截断，默认 50000。 |
| `Cors__AllowedOrigins__N` | 允许跨域访问的来源。默认已包含桌面端（Tauri）的来源；镜像自带的 Web 页面同源，不需要配置。只有把 Web 单独部署到另一个域名时才需要加。 |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | 信任反向代理传来的 `X-Forwarded-For` / `X-Forwarded-Proto`。示例 Compose 里已打开，此时应用端口只应通过反向代理访问。 |
| `Serilog__MinimumLevel__Default` | 日志级别，默认 `Information`。日志写到标准输出，用 `docker compose logs` 查看。 |

接入 OpenAI 兼容端点的例子（写进 `deploy/.env`）：

```bash
Ai__Providers__deepseek__Kind=openai
Ai__Providers__deepseek__BaseUrl=https://api.deepseek.com/v1
Ai__Providers__deepseek__ApiKey=sk-...
Ai__Providers__deepseek__Models__0=deepseek-chat
Ai__DefaultProvider=deepseek
Ai__DefaultModel=deepseek-chat
```

## HTTPS 与反向代理

登录令牌和 API 密钥都放在请求头里，公网访问必须走 HTTPS。在应用前面放一个反向代理终止 TLS，同时把应用端口只绑定到本机：在 `.env` 里写 `APP_PORT=127.0.0.1:8080`。

工作流的 Webhook 地址（`/api/hooks/workflows/…`）需要能从外部系统访问到，反向代理不要拦截这个路径。

需要转发的除了普通请求，还有 WebSocket（`/hubs/realtime`，实时刷新）和流式响应（对话回复、`/mcp`）。应用对流式响应带了 `X-Accel-Buffering: no`，nginx 不会缓冲它们。

nginx 示例：

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 443 ssl;
    http2 on;
    server_name ai.example.com;
    ssl_certificate     /etc/ssl/ai.example.com/fullchain.pem;
    ssl_certificate_key /etc/ssl/ai.example.com/privkey.pem;

    # 知识库文档单篇最多 100 万字，请求体可达数 MB
    client_max_body_size 10m;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        # 智能体长时间执行时保持实时连接和流式回复
        proxy_read_timeout 1h;
    }
}
```

Caddy 会自动申请证书，WebSocket 和流式响应无需额外配置：

```
ai.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

## 客户端接入

- **Web**：直接打开站点地址。
- **桌面端**：打包时指定后端地址，例如 `NEXT_PUBLIC_API_URL=https://ai.example.com pnpm build:desktop`。后端默认允许桌面端的跨域来源。
- **移动端**：登录页填写服务器地址（例如 `https://ai.example.com`），也可以在构建时用 `EXPO_PUBLIC_API_URL` 设置默认值。
- **MCP 客户端**：服务地址是 `https://ai.example.com/mcp`。在「设置 → API 密钥」创建密钥时，对话框里给出的命令已经带上当前站点的完整地址。

## 数据库

- **迁移**：示例 Compose 打开了 `Database__MigrateOnStartup`，升级后启动时自动执行；EF Core 迁移时会加数据库锁，多个实例同时启动也只执行一次。想在发布流程里单独迁移，关掉这个开关，在源码目录生成幂等脚本后用 psql 执行：

  ```bash
  cd backend
  dotnet ef migrations script --idempotent --project AiNative.Infrastructure --startup-project AiNative.Api -o migrate.sql
  ```

- **使用已有的 PostgreSQL**：需要 PostgreSQL 17 和 pgvector 扩展。迁移会执行 `CREATE EXTENSION vector`，数据库用户没有建扩展的权限时，请先由管理员在目标库里建好。连接字符串通过 `ConnectionStrings__Postgres` 传入，此时可以去掉 Compose 里的 `postgres` 服务。

## 备份与恢复

数据全部在 PostgreSQL 里（包括知识库文档），备份数据库即可：

```bash
cd deploy
docker compose exec -T postgres pg_dump -U ainative -Fc ainative > ainative-$(date +%F).dump

# 恢复到当前库（会覆盖现有数据）
docker compose exec -T postgres pg_restore -U ainative -d ainative --clean --if-exists < ainative-2026-10-12.dump
```

## 升级

```bash
git pull
cd deploy
docker compose up -d --build
```

新镜像启动时自动迁移数据库。升级前建议先备份。

## 健康检查

- `/health/live`：进程存活。镜像自带的 Docker 健康检查用它。
- `/health/ready`：能连上数据库，适合负载均衡或 Kubernetes 的就绪探针。

## 当前限制

- **只运行一个应用实例**：智能体调度、工作流引擎和实时推送目前在进程内协调，多实例部署会重复调度、实时消息也只到达本实例的连接。多实例支持在计划中。
- 应用本身不存文件，容器可以随时重建；需要持久化的只有数据库卷。

## 上线检查清单

- [ ] `Auth__SigningKey` 是新生成的随机值，`POSTGRES_PASSWORD` 足够强
- [ ] 要在「集成」里保存令牌时，设置了 `Secrets__MasterKey`，并和数据库备份分开保存
- [ ] 通过 HTTPS 访问，应用端口只绑定本机（`APP_PORT=127.0.0.1:8080`）
- [ ] 数据库端口没有暴露到公网（示例 Compose 默认不映射）
- [ ] 建好自己的空间后设置 `Auth__AllowRegistration=false`
- [ ] 配置了模型密钥，并为智能体设置了月度预算
- [ ] 定期备份数据库，并验证过恢复
