# ITAsset4 客户端更新包 v1.2.18

## 本次变更
将 Tray 的启动机制从「Service 用 `CreateProcessAsUser` + 计划任务拉起」改为「在公共 Startup 文件夹创建快捷方式，由 Windows 在用户登录时自动拉起」。

**原因**：`CreateProcessAsUser` 在 RDP / 跨会话桌面 DACL 场景下子进程 user32 初始化失败（0x8007045A 秒退）；计划任务兜底方案复杂且依赖交互用户令牌解析，易出错。Startup 快捷方式天然在正确用户会话、拥有交互桌面访问权，彻底绕开跨会话令牌问题，也无需 Service 常驻监听 WTS 事件。

## 代码改动
- **`ITAsset4.Service/SessionManager.cs`**（整体重写）
  - 删除：`LaunchProcessInSession`(`CreateProcessAsUser`)、`LaunchViaScheduledTask`/`ResolveInteractiveUser`/`RunHidden`/`TrayTaskName`/`IsTrayRunningAnywhere`、`OnTrayNeeded` 事件、`StartWatchThread`/`WatchLoop`(WTS 监听线程) 及全部 `advapi32`/`userenv`/`CreateProcessAsUser` 相关 P/Invoke 与结构体。
  - 新增 `EnsureStartupShortcut()`：`IShellLink`+`IPersistFile` 自包含 COM 互操作，在 `Environment.SpecialFolder.CommonStartup`（=`C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup`）创建指向 `ITAsset4.Tray.exe` 的 `ITAsset4 Tray.lnk`；已存在则跳过（`_shortcutConfirmed` 防日志刷屏）。
  - 保留 `GetActiveUserSessionId()`（WTS：`WTSEnumerateSessions`/`WTSQueryUserToken`/`WTSFreeMemory`/`CloseHandle`）——`TaskExecutor.cs:635` 连接 Tray 管道仍需它。
- **`ITAsset4.Service/AgentWorker.cs`**：删除 `OnTrayNeeded` 订阅、`_trayCheckSignal` 字段及主循环 `WhenAny` 分支；`TickAsync` 内 `CheckAndLaunchTray()` → `EnsureStartupShortcut()`；清理相关注释。
- **`ITAsset4.Common/ClientVersion.cs`**：`Current` → `1.2.18`。

## 构建与验证
- `dotnet build ITAsset4.sln -c Release`（net48 / x64，dotnet 9 SDK + VS2022）：**0 error**（仅 `TaskExecutor` 2 个无关的 CS1998 警告）。
- `dotnet test ITAsset4.Logic.Tests`：**49/49 通过**。
- `make_update_pkg.py` 扁平打包三处 bin 输出（25 文件，排除 .pdb）。

## 产物
| 文件 | 说明 |
|---|---|
| `update.zip` | 更新包（932,755 字节，SHA256 `07e52e7a0aed8fd85f72d6dd82907b86010e3a80420514914afc3ecb930b1117`） |
| `version.json` | 服务端 `/api/client/update` 元数据（ClientUpdateInfo 结构） |

## 部署提示（尚未执行）
- `version.json` 的 `url` 当前为占位 `https://<SERVER>/downloads/client/itasset4-update-1.2.18.zip`，部署前需替换为真实下载地址。
- 部署需将 `update.zip` + `version.json` 放到服务端下载目录（`/api/client/update` 读取 `version.json`），会触发全员客户端自更新——**建议确认后再发**。
