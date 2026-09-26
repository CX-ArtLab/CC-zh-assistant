<div align="center">
  <img src="src/Assets/assistant-icon.png" width="128" height="128" alt="Claude Code 桌面版中文助手 图标" />
  <h1>Claude Code 桌面版中文助手</h1>
  <p>面向 Windows 的 Claude Desktop / Claude Code 极简外挂式界面汉化伴侣</p>
</div>

**Claude Code 桌面版中文助手**（CC 中文助手 / `CCZhAssistant.exe`）是一个专为 Claude Desktop 及 Claude Code 桌面环境打造的非官方外挂式汉化伴侣程序。它采用与官方程序隔离的安全部署方式，不修改 `Claude.exe` 主程序与 `app.asar` 核心数字签名，不破坏 Cowork 沙箱及工作区功能，支持一键应用全中文界面，并随时可一键无损还原官方原版。

---

## 普通用户使用

1. 从 [Releases](https://github.com/CX-ArtLab/CC-zh-assistant/releases/latest) 下载最新版本的 `CCZhAssistant-windows.zip`。
2. 将 ZIP 解压到任意文件夹。无需安装开发环境，也不需要将 EXE 放入 Claude 安装目录。
3. 运行解压后的 `CCZhAssistant.exe`。
4. 点击主卡片中的 **“立即应用汉化”** 胶囊按钮即可一键完成汉化。
5. 汉化生效后，点击下方的 **“重启 Claude”** 按钮即可一键重启客户端刷新界面。
6. 助手支持“自动适配”、“开机启动”和“后台静默守护”，可随 Windows 启动并驻留系统托盘。

> **便携版程序**：本程序为单文件绿色免安装应用，移动整个解压文件夹即可移动助手，卸载时在助手内点击恢复原版后直接删除该文件夹即可。

<div align="center">
  <img src="docs/ui-preview.png" width="480" alt="Claude Code 中文助手 界面预览" />
</div>

---

## 当前功能特性

- **非破坏性外挂伴侣**：不改写 `Claude.exe` 与 `app.asar` 的核心代码及签名，保障 Cowork 沙箱及工作区完全可用。
- **一键应用与一键还原**：自动备份原始配置，随时可一键完美还原至官方原版英文状态。
- **海量词典预置**：内置打包 32,000+ 条官方界面与交互词条（涵盖前端、会话、设置、侧边栏、快捷键与模型选项等）。
- **极简居中卡片美学**：采用现代化无边框沉浸式视窗，结合高精度 GDI+ 矢量抗锯齿绘制与 DPI 自适应防挤压方案（100%、125%、150%、200% 显示缩放均清晰锐利）。
- **智能版本检测与自动适配**：实时监测 Claude Desktop 进程状态与版本升级，检测到新版本后自动增量适配。
- **待适配词条扫描器**：自动比对官方英文词库与现有汉化，未翻译的新增词条自动输出到 `%LOCALAPPDATA%\Claude Code 中文助手\待适配词条.json`，方便社区贡献与后续补充。
- **独立词典云端更新**：支持热更新远程词典包，无需频繁重新下载助手客户端。
- **便捷一键重启**：界面内置“重启 Claude”快捷按钮，优雅关闭旧进程并秒级重新拉起，无需手动任务管理器清理。

---

## 兼容性与运行环境

- **操作系统**：Windows 10 / Windows 11（64 位系统）
- **目标软件**：已安装官方 Claude Desktop（MSIX 应用商店版或普通免打包版均完美支持）
- **权限建议**：若 Claude 安装在受保护的系统目录，首次应用如提示权限受限，右键选择“以管理员身份运行”即可。

---

## 本地编译构建

在 Windows PowerShell 中运行：

```powershell
.\build.ps1
```

生成的单文件便携程序位于 `dist/CCZhAssistant.exe`，并会自动打包生成 `dist/CCZhAssistant-windows.zip`。项目使用 Windows 自带的 .NET Framework C# 编译器（`csc.exe`），无需安装 Visual Studio、.NET SDK、Node.js 或 Python 等额外开发环境。

---

## 词典包说明

- 翻译词典源文件位于 [`translation/`](translation/)：
  - `translation-pack.json`：打包的完整词典集合（包含前端、桌面、交互与补充词条）。
  - `manifest.json`：词典版本控制与云端更新元数据。
- Windows 发布版会直接把构建时的完整词典内嵌打包进 EXE，完全离线即可使用。
- 开启“自动检查最新汉化包”后，程序会自动检测并优先加载 `%LOCALAPPDATA%\Claude Code 中文助手\translation-pack.json` 中的更新词典。

---

## 隐私与免责声明

1. **隐私安全**：助手仅处理 Claude 官方应用程序的系统界面元素与语言配置，绝不读取、修改或上传您的对话记录、提示词、代码片段或项目私有文件。扫描出的待适配词条仅保存在本机。
2. **非官方声明**：本项目不是 Anthropic 官方产品，与 Anthropic PBC 不存在任何隶属、赞助或授权关系。“Claude”、“Claude Code”及相关商标和品牌资源均归其各自权利人所有。
3. **开源许可**：本项目基于 MIT 许可证开源，仅供学习、交流与界面便利使用。
