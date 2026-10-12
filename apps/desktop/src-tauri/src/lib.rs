use tauri::webview::NewWindowResponse;
use tauri::{AppHandle, Manager, Url, WebviewWindowBuilder};
use tauri_plugin_opener::OpenerExt;

/// 主窗口的标签，与 tauri.conf.json 里的窗口配置一致。
const MAIN_WINDOW: &str = "main";

/// 桌面端只做一层原生外壳：界面就是 Web 工作台的静态导出，业务全部走后端 API。
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        // 再次启动时聚焦已有窗口，而不是开第二个实例。
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            if let Some(window) = app.get_webview_window(MAIN_WINDOW) {
                let _ = window.unminimize();
                let _ = window.set_focus();
            }
        }))
        .plugin(tauri_plugin_window_state::Builder::default().build())
        // 外部链接统一由主窗口的导航拦截处理，不注入插件自带的点击脚本：
        // 那个脚本会把按住 Ctrl 点击的应用内链接（Windows 上是 http://tauri.localhost/...）也交给系统浏览器。
        .plugin(
            tauri_plugin_opener::Builder::new()
                .open_js_links_on_click(false)
                .build(),
        )
        .setup(|app| create_main_window(app.handle()))
        .run(tauri::generate_context!())
        .expect("启动桌面端失败");
}

/// 主窗口在配置里设为不自动创建（`create: false`），这里按同一份配置创建，以便挂上导航拦截：
/// 应用以外的链接（包括 `target="_blank"`、`window.open` 和 `mailto:`、`tel:`）交给系统默认程序打开，
/// 窗口里始终只显示工作台。
fn create_main_window(app: &AppHandle) -> Result<(), Box<dyn std::error::Error>> {
    let config = app
        .config()
        .app
        .windows
        .iter()
        .find(|window| window.label == MAIN_WINDOW)
        .cloned()
        .ok_or("tauri.conf.json 里缺少主窗口配置")?;
    // 开发时页面来自 devUrl（Web 开发服务器）；打包后的应用不信任这个地址。
    let dev_url = if tauri::is_dev() {
        app.config().build.dev_url.clone()
    } else {
        None
    };

    let navigation_app = app.clone();
    let navigation_dev_url = dev_url.clone();
    let new_window_app = app.clone();
    WebviewWindowBuilder::from_config(app, &config)?
        .on_navigation(move |url| {
            if is_app_url(url, navigation_dev_url.as_ref()) {
                return true;
            }

            open_external(&navigation_app, url);
            false
        })
        .on_new_window(move |url, _features| {
            if !is_app_url(&url, dev_url.as_ref()) {
                open_external(&new_window_app, &url);
            }

            // 工作台只有一个窗口：应用内的页面（例如按住 Ctrl 点击的链接）也不另开窗口。
            NewWindowResponse::Deny
        })
        .build()?;
    Ok(())
}

/// 是否是应用自身的页面：打包后由自定义协议提供（macOS、Linux 为 `tauri://localhost`，
/// Windows 为 `http(s)://tauri.localhost`），开发时是 devUrl。
/// `about:blank`、`blob:`、`data:` 是窗口内部的内容，同样放行。
fn is_app_url(url: &Url, dev_url: Option<&Url>) -> bool {
    match url.scheme() {
        "tauri" | "about" | "blob" | "data" => true,
        "http" | "https" if url.host_str() == Some("tauri.localhost") => true,
        _ => dev_url.is_some_and(|dev_url| dev_url.origin() == url.origin()),
    }
}

/// 只把常见的外部链接交给系统默认程序；其他协议（`file:`、`javascript:` 等）既不打开，也不在窗口里加载。
fn open_external(app: &AppHandle, url: &Url) {
    if !matches!(url.scheme(), "http" | "https" | "mailto" | "tel") {
        return;
    }

    if let Err(error) = app.opener().open_url(url.as_str(), None::<&str>) {
        eprintln!("用系统默认程序打开 {url} 失败：{error}");
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn url(value: &str) -> Url {
        Url::parse(value).unwrap()
    }

    /// 打包后的页面留在窗口里。
    #[test]
    fn bundled_pages_stay_in_window() {
        assert!(is_app_url(&url("tauri://localhost/tasks/view?id=1"), None));
        assert!(is_app_url(&url("http://tauri.localhost/"), None));
        assert!(is_app_url(&url("https://tauri.localhost/chat"), None));
        assert!(is_app_url(&url("about:blank"), None));
    }

    /// 外部地址交给系统默认程序。
    #[test]
    fn external_urls_leave_window() {
        assert!(!is_app_url(&url("https://example.com/"), None));
        assert!(!is_app_url(&url("http://192.168.1.10:5080/"), None));
        assert!(!is_app_url(&url("mailto:someone@example.com"), None));
        assert!(!is_app_url(&url("tel:10086"), None));
        assert!(!is_app_url(
            &url("https://tauri.localhost.example.com/"),
            None
        ));
    }

    /// 只在开发时信任开发服务器。
    #[test]
    fn dev_server_trusted_only_in_dev() {
        let dev_url = url("http://localhost:3000");
        assert!(is_app_url(
            &url("http://localhost:3000/tasks"),
            Some(&dev_url)
        ));
        assert!(!is_app_url(
            &url("http://localhost:5080/api"),
            Some(&dev_url)
        ));
        assert!(!is_app_url(&url("http://localhost:3000/tasks"), None));
    }
}
