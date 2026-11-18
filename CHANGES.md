# 修改说明（AntigravityQuotaTray v1.0.0）

本仓库基于原有 Windows 任务栏圆环与桌面悬浮窗框架改造为 **Google Antigravity** 额度显示工具，UI 形态、绘制参数与交互逻辑保持一致。

## 1. 额度来源：本机 Google Antigravity 服务 + 离线快照缓存

文件：`src/SessionQuotaReader.cs`、`src/Models.cs`、`src/AppSettings.cs`

- 通过 Win32 API（`CreateToolhelp32Snapshot`、`NtQueryInformationProcess`、`GetExtendedTcpTable`）在当前用户会话下无感发现运行中的 `language_server_windows_x64.exe` / `language_server_windows_arm.exe` 进程，读取启动参数中的 `--csrf_token` 与本地监听端口（全程无子进程、无弹窗）。
- 仅向本机回环地址 `127.0.0.1` 请求 `RetrieveUserQuotaSummary`（旧版本自动回退 `GetUserStatus`），解析 Google Antigravity 的两大额度池：
  1. **Gemini 自有模型池**（`5h` 窗口 + `weekly` 周窗口）
  2. **第三方模型池**（`5h` 窗口 + `weekly` 周窗口）
- 当窗口处于 `100%` 满额未消耗状态时，自动忽略服务端返回的滑动占位 `resetTime`，标记为“满额未计时”。
- **离线缓存持久化**：每次读取成功后将圆环综合均值及 4 个窗口的数据原子保存至 `%LOCALAPPDATA%\AntigravityMeter\settings.ini`。当 Google Antigravity 主程序未启动时，程序保持每 60 秒轻量检查一次，界面继续显示最后一次缓存的圆环数值与 4 行详情，并在主程序启动后 1 分钟内自动恢复实时更新。

## 2. 圆环综合均值与 4 行悬停提示

文件：`src/Models.cs`、`src/QuotaDisplayState.cs`、`src/TrayApplicationContext.cs`

- **圆环与中心数字**：折算为 `0–100` 综合百分比，取 `(Gemini 5h剩余% + 第三方 5h剩余%) / 2` 四舍五入（若套餐仅含周额度则自动按两池周额度求均值）。
- **4 行悬停提示**：任务栏圆环、桌面悬浮窗与右下角系统托盘图标悬停时分 4 行显示 `Gemini 5h`、`Gemini 周`、`第三方 5h`、`第三方 周` 的剩余百分比与重置倒计时；针对 Windows `NotifyIcon.Text` 的 127 字符限制内置自适应紧凑排版，确保离线提示头与 4 行额度均不被截断。

## 3. 独立命名与手动 Release 工作流

文件：`AntigravityQuotaTray.csproj`、`src/Program.cs`、`src/RuntimeLog.cs`、`.github/workflows/manual-release.yml`、`build.ps1`

- 单实例锁、唤醒消息窗口、配置与日志目录（`%LOCALAPPDATA%\AntigravityMeter`）全部独立，可与其他额度工具同时运行互不干扰。
- 手动触发的 GitHub Actions 工作流输出 `AntigravityQuotaTray-vX.Y.Z-win-x64-self-contained.exe` 与 `AntigravityQuotaTray-vX.Y.Z-win-x64-framework-dependent.exe`。
