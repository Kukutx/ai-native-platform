# 桌面端

Tauri 2 原生外壳：窗口里加载 Web 工作台的静态导出（`apps/web` 以 `NEXT_OUTPUT=export` 构建到 `apps/web/out`），业务全部走后端 API。没有自己的 TS 代码，不参与 `pnpm type-check` 和 `pnpm lint`。

## 开发与打包

```bash
pnpm dev:desktop                          # 启动 Web 开发服务器并打开桌面窗口
pnpm build:desktop                        # 静态导出 Web 并打包安装程序
pnpm --filter @ainative/desktop check     # cargo check，CI 里同样会跑
```

需要 Rust stable 工具链（最低 1.90，与 `Cargo.toml` 的 `rust-version` 一致）和各平台的 WebView 依赖，Linux 上是 `webkit2gtk-4.1` 等开发包，详见 Tauri 官方文档的“前置要求”。

## 后端地址在打包时确定

Web 的静态导出在构建时把 `NEXT_PUBLIC_API_URL` 写进代码（见 `apps/web/src/lib/config.ts`），桌面端运行时没有修改服务器地址的入口。为哪个服务器打包，就在打包时指定它：

```bash
NEXT_PUBLIC_API_URL=https://ai.example.com pnpm build:desktop
```

- 不指定时默认是 `http://localhost:5080`，只适合本机开发。
- 换服务器需要重新打包；同一个安装包不能连接不同的服务器。
- 后端默认的 `Cors:AllowedOrigins` 已包含 Tauri 的来源（`tauri://localhost`、`http://tauri.localhost`），一般不需要额外配置。

## 内容安全策略（CSP）

`src-tauri/tauri.conf.json` 是 JSON，写不了注释，取舍记在这里：

- `script-src 'self'`：脚本只能来自应用自身打包的文件，不加载任何外部脚本。
- `connect-src` 允许 `http:`、`https:`、`ws:`、`wss:` 的任意主机。后端地址在打包时才确定，内网部署常用 `http://192.168.1.10:5080` 这类明文地址，CSP 里无法预先列出；脚本已经限定为应用自身，放宽连接目标不会引入外部代码。公网部署请使用 HTTPS（实时通道随之走 `wss:`）。
- `ipc:` 和 `http://ipc.localhost` 是 Tauri 自身的进程间通信。

## 外部链接

主窗口在 Rust 里创建（配置里 `create: false`，见 `src-tauri/src/lib.rs`），并挂上导航和新窗口拦截：

- 应用自身的页面（`tauri://localhost`、`http(s)://tauri.localhost`，开发时的 devUrl）留在窗口里。
- 其他 `http(s):`、`mailto:`、`tel:` 链接，不论是普通跳转、`target="_blank"` 还是 `window.open`，都交给系统默认程序（浏览器、邮件、电话）打开，窗口里不会跳到外部网站。
- 其他协议（`file:` 等）直接拒绝。
- 网页不能直接调用打开命令：capabilities 里没有授予 opener 插件的权限，链接只在原生层判断后打开。
