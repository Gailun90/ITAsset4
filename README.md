# ITAsset4 — Windows 客户端 Agent

**ITAsset4** 是一个 .NET Framework 4.8 Windows 客户端，以 Windows Service + 系统托盘双进程架构运行，为 itasset-api 后端提供资产上报、远程桌面和软件部署执行能力。

## 功能概览

### 📊 资产采集上报
- 硬件信息采集（CPU、内存、磁盘、主板序列号、Machine GUID）
- 已安装软件清单采集
- 当前登录用户检测
- 定时自动上报（可配置上报时间）
- 使用 HMAC 签名保障通信安全

### 🖱️ 远程桌面
- GDI 实时桌面截图（Session 0 感知）
- 截图通过 Named Pipe → Windows Service → WebSocket 链路传输到浏览器
- 鼠标移动、点击、滚轮输入透传
- 键盘输入透传
- 支持多 Session 的 CreateProcessAsUser 安全隔离

### 📦 软件部署执行
- 接收服务端部署任务
- 静默安装（`/S /silent /quiet` 等参数）
- 安装后可选重启
- 安装日志回传到服务端
- 支持软件卸载

### 🔄 自动更新
- 服务端发布新版本后，客户端自动检测并下载更新包
- 更新包校验与自动安装

## 架构说明

```
┌─────────────────────────────────────┐
│  ITAsset4.Service（Windows Service） │  ← Session 0，网络通信、任务执行
│  ├── AgentWorker     核心循环        │
│  ├── WsClient        WebSocket 连接  │
│  ├── TaskExecutor    部署任务执行     │
│  ├── SessionManager  用户 Session    │
│  └── TcpScreenClient 接收截图数据    │
├─────────────────────────────────────┤
│  ITAsset4.Tray（用户 Session 托盘）  │  ← 用户桌面，截图与输入
│  ├── PipeServer      Named Pipe 服务 │
│  ├── TcpScreenServer 截图推送        │
│  └── TcpInputServer  鼠标键盘接收    │
└─────────────────────────────────────┘
           ↕ TCP 15900 / 15901
    WebSocket → itasset-api → 浏览器
```

## 系统要求

| 环境 | 要求 |
|---|---|
| OS | Windows 10 / 11 / Server 2016+ |
| .NET | Framework 4.8 |
| 权限 | 需要以 SYSTEM 运行 Service |

## 配置文件

配置文件位于 `%ProgramData%\ITAsset4\config.ini`，首次运行自动创建：

```ini
[server]
; 服务器地址，部署时请修改为实际地址
url = http://your-server:8000
; 初始注册 Token，需与服务端 AGENT_INITIAL_TOKEN 一致（可选）
; initial_token = your-token-here

[schedule]
report_time    = 08:00
poll_time      = 09:00
```

## 构建方法

```powershell
# 使用 Visual Studio 2022 或 MSBuild
cd ITAsset4
dotnet build ITAsset4.sln -c Release
```

输出目录：
- `ITAsset4.Service\bin\Release\` — 服务程序
- `ITAsset4.Tray\bin\Release\` — 托盘程序

## 部署方法

1. 将 Release 输出拷贝到目标机器
2. 以管理员权限安装 Service：
   ```cmd
   sc create ITAsset4 binPath= "C:\ITAsset4\ITAsset4.Service.exe" start= auto
   sc start ITAsset4
   ```
3. 将 `ITAsset4.Tray.exe` 加入用户登录启动项

## 相关项目

- [itasset-api](https://github.com/Gailun90/itasset-api) — FastAPI 后端服务
- [admanager](https://github.com/Gailun90/admanager) — GLPI 插件（管理界面）

## License

MIT
