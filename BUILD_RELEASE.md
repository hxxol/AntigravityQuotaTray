# 手动构建与 Release

1. 将项目根目录内容上传到仓库，包含 `.github/workflows/manual-release.yml`。
2. 打开仓库的 Actions → Manual Windows build and release → Run workflow。
3. 输入三段版本号，例如 `1.0.0`。
4. 勾选“Publish a GitHub Release…”会在全部验证成功后发布；取消勾选则只生成 Actions 附件。

工作流仅有 `workflow_dispatch`，没有 push、pull_request、定时或自动升级触发。

执行顺序：

- 使用 Windows runner 和 .NET 10 SDK
- 编译并运行 Windows 回归、双池解析、离线缓存与绘制测试
- 分别发布自包含和依赖框架的 Windows x64 单 EXE
- 检查每个发布目录恰好一个 `AntigravityQuotaTray.exe`
- 运行 `--smoke-test`（云端无交互桌面时仅作警告提示）
- 生成 `SHA256SUMS.txt`，上传 Actions 附件
- 若选择发布，创建对应版本的 Release，并将标签指向本次验证过的 commit

发布文件：

- `AntigravityQuotaTray-vX.Y.Z-win-x64-self-contained.exe`
- `AntigravityQuotaTray-vX.Y.Z-win-x64-framework-dependent.exe`
- `SHA256SUMS.txt`

已有同名 Release 会停止创建，避免覆盖已发布的文件。需要重发时应使用新的版本号。
