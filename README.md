# AntigravityQuotaTray

面向 Windows x64 的 **Google Antigravity** 额度显示工具，保留原任务栏动态额度大圆环、WPF 桌面悬浮窗、系统托盘图标与全部交互/设置逻辑。

- **圆环综合百分比（0–100）**：显示 Google Antigravity 自有 **Gemini 模型池**与**第三方模型池**的 5 小时窗口剩余均值 `(Gemini 5h + 第三方 5h) / 2`（仅周额度的套餐自动按两池周额度求均值）。
- **四行悬停提示**：鼠标悬停在任务栏圆环、悬浮窗或右下角系统托盘图标上时，分 4 行实时显示：
  1. `Gemini 5h` 剩余百分比与重置倒计时
  2. `Gemini 周` 剩余百分比与重置倒计时
  3. `第三方 5h` 剩余百分比与重置倒计时
  4. `第三方 周` 剩余百分比与重置倒计时
- **纯本地回环读取 + 离线缓存**：
  - 当 Google Antigravity 运行时，程序通过本机回环接口（`127.0.0.1` 的 `language_server_windows_*.exe` `RetrieveUserQuotaSummary`）每分钟读取最新额度，无需额外登录 Google OAuth，不向 Google 远端发送任何伪装请求。
  - 当 Google Antigravity 未启动时，自动展示本地保存的最后一次额度快照（含圆环均值与全部 4 行详情），并在悬停提示顶部注明缓存时间；主程序启动后 1 分钟内自动恢复实时刷新。

---

## 使用

点击任务栏圆环打开菜单，顺序固定为：

1. 悬浮窗（勾选开关）
2. 设置
3. 退出

左右键都可打开这个菜单。任务栏圆环不能拖动。悬浮窗可按住左键拖动，不提供右键菜单、边框或缩放手柄。

设置分为任务栏与悬浮窗两部分：

- 两者的圆环颜色、数字颜色互不影响，仅提供自定义颜色。
- 任务栏保留原有大小公式、字体与字号，只开放线宽和两种颜色。
- 悬浮窗口只可选择 128、256、512 **实际像素**的正方形画布；不同 DPI 下仍保持指定窗口像素大小。
- 悬浮窗可调整数字大小、字重、圆环线宽和圆环缩放。线宽与字号以 256 像素画布为基准。
- 圆环没有背景底圈；缺口居中朝下；100 显示为 `100`，不附加百分号。

显示设置、悬浮开关、最后拖动位置以及最近一次额度缓存自动保存至 `%LOCALAPPDATA%\AntigravityMeter\settings.ini`。新安装默认关闭悬浮窗。

再次运行同版 EXE 会打开现有进程的设置窗口，也可使用 `--settings`。`--recover` 会打开设置，并开启悬浮窗、移到主屏中央；不会重置其他设置。程序不会移动、注入或修改 Explorer。

## 两种单文件发布

目标框架为 `net10.0-windows`，Windows x64。

- `self-contained`：包括 .NET 10 Windows Desktop Runtime，文件较大；无需另外安装运行时。
- `framework-dependent`：较小的单 EXE，电脑需已安装 **.NET 10 Windows Desktop Runtime x64**。只有普通 .NET Runtime 不够。

两个版本都只分发一个 `AntigravityQuotaTray.exe`。不使用裁剪，也不自动修改注册表开机启动项。

## 本地构建

Windows 上安装 .NET 10 SDK，然后在项目根目录运行 PowerShell：

```powershell
./build.ps1
```

Linux/macOS 可以运行纯逻辑回归测试（不包含 Windows 绘制与交互）：

```sh
dotnet run --project tests/AntigravityQuotaTray.CoreTests.csproj -c Release
```

Windows 完整夹具测试：

```powershell
dotnet run --project tests/AntigravityQuotaTray.Tests.csproj -c Release -- --fixtures-only
```

## 手动 GitHub 发布

工作流只支持 `workflow_dispatch`，提交和推送不会自动触发。具体步骤见 [BUILD_RELEASE.md](BUILD_RELEASE.md)。所有自动测试和两种 EXE 构建成功后，才允许创建 Release。工作流不会覆盖已存在的版本。
