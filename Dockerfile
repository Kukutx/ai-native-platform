# syntax=docker/dockerfile:1

# 生产镜像：一个容器同时提供 API、实时通道（SignalR）、MCP 服务和 Web 工作台。
# Web 工作台以静态导出放进 wwwroot，与接口同源，前端用相对地址访问后端。
#   构建：docker build -t ainative-platform .
#   部署：见 deploy/docker-compose.yml 与 docs/deployment.md
# 构建阶段在构建机的原生架构上运行，产物与架构无关，可以直接 docker buildx build --platform linux/amd64,linux/arm64。

ARG NODE_VERSION=24
ARG DOTNET_VERSION=10.0

# ---------- Web 工作台：Next.js 静态导出 ----------
FROM --platform=$BUILDPLATFORM node:${NODE_VERSION}-bookworm-slim AS web
WORKDIR /src
ENV CI=1 \
    NEXT_TELEMETRY_DISABLED=1 \
    COREPACK_ENABLE_DOWNLOAD_PROMPT=0
RUN corepack enable

# 先只拷依赖清单，源码改动时复用依赖层。
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml .npmrc ./
COPY apps/web/package.json apps/web/
COPY apps/desktop/package.json apps/desktop/
COPY apps/mobile/package.json apps/mobile/
COPY packages/api-client/package.json packages/api-client/
RUN --mount=type=cache,id=ainative-pnpm-store,target=/pnpm/store \
    pnpm install --frozen-lockfile --store-dir /pnpm/store --filter "@ainative/web..."

COPY tools/precompress.mjs tools/
COPY packages/api-client packages/api-client
COPY apps/web apps/web
# 接口地址留空：页面和接口同源。
ENV NEXT_PUBLIC_API_URL="" \
    NEXT_OUTPUT=export
RUN pnpm --filter @ainative/web exec next build \
    && node tools/precompress.mjs apps/web/out

# ---------- 后端：发布 ASP.NET Core ----------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS api
WORKDIR /src
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY global.json ./
COPY backend/Directory.Build.props backend/Directory.Packages.props backend/NuGet.Config backend/
COPY backend/AiNative.Domain/AiNative.Domain.csproj backend/AiNative.Domain/
COPY backend/AiNative.Contracts/AiNative.Contracts.csproj backend/AiNative.Contracts/
COPY backend/AiNative.Application/AiNative.Application.csproj backend/AiNative.Application/
COPY backend/AiNative.Infrastructure/AiNative.Infrastructure.csproj backend/AiNative.Infrastructure/
COPY backend/AiNative.Api/AiNative.Api.csproj backend/AiNative.Api/
RUN --mount=type=cache,id=ainative-nuget,target=/root/.nuget/packages \
    dotnet restore backend/AiNative.Api/AiNative.Api.csproj

COPY backend/AiNative.Domain backend/AiNative.Domain
COPY backend/AiNative.Contracts backend/AiNative.Contracts
COPY backend/AiNative.Application backend/AiNative.Application
COPY backend/AiNative.Infrastructure backend/AiNative.Infrastructure
COPY backend/AiNative.Api backend/AiNative.Api
# 不加 --no-restore：NuGet 包在缓存挂载里，不随镜像层导出。CI 从远端缓存命中上面的还原层时挂载是空的，
# 这里会按已还原的清单补下载缺的包；本地构建包都在，还原只是检查一遍。
RUN --mount=type=cache,id=ainative-nuget,target=/root/.nuget/packages \
    dotnet publish backend/AiNative.Api/AiNative.Api.csproj -c Release -o /app -p:UseAppHost=false

# ---------- 运行时 ----------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app
# 镜像自带 ICU 和时区数据（租户时区换算需要）。默认监听 8080，以非 root 用户运行。
ENV ASPNETCORE_HTTP_PORTS=8080
COPY --from=api /app ./
COPY --from=web /src/apps/web/out ./wwwroot
USER $APP_UID
EXPOSE 8080
# 只探活进程（/health/live）；数据库是否可用看 /health/ready。镜像里没有 curl，用 bash 的 /dev/tcp 发请求。
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/live HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "AiNative.Api.dll"]
