# QJAR 工具

把原来的「注册工具」（C + Win32）和「示教器命令调试工具」（C# WinForms）合并成一个桌面程序：
左侧竖排导航依次是「编译」「注册」「命令调试」「升级」，共用两套连接预设和管理员会话。

界面按 [Astryx](https://github.com/meta/astryx) 的设计 token 重做，但在原生 WinForms 里自绘实现，不引入 WebView。

## 功能

- **注册页**：查询状态、注册并确认授权，成功后自动重启所选的实体或本机虚拟示教器，本机虚拟示教器还可打开和关闭。
- **命令调试页**：发送任意命令（UTF-8 / GBK 可选，超时 1–600000 ms），收发记录可导出。
- **编译页**：选择本机 WSL 或 Ubuntu 远程编译。远程支持项目管理、编译、仅打包、校验下载和虚拟控制器日志。
- **升级页**：选择或拖入升级包，确认目标后上传并校验。控制器使用左侧当前地址，示教器固定为 `192.168.1.200`。
- **连接预设**：「实体控制器」和「Ubuntu 虚拟机」可分别修改和保存。远程编译读取「Ubuntu 虚拟机」的 IP。
- **管理员登录**：会话由 Windows 按当前用户保护，点「退出管理」时清除。

## 结构

```
src/core/   C 业务核心 → QJCore.dll（controller_run / pendant_restart / qj_exchange）
src/ui/     C# 外壳（窗口、主题、自绘控件、页面、P/Invoke 封装）
Tests/      模拟控制器、通信冒烟、界面回归、Makefile 读写检查、编译页集成检查、截图工具
Docs/       UI 规格、发布说明、使用说明、Astryx token 原始导出
build.ps1   编译 → 测试 → 打包
```

线协议（28 字节头 + 校验和 + 命令/数据字段）只有一份实现，位于 `src/core/Controller.c`；命令调试页也走同一个 `qj_exchange`。

## 构建

需要 Windows 10/11 x64、Visual Studio Build Tools（MSVC x64 + Roslyn csc）。

```powershell
.\build.ps1                 # 编译 QJCore.dll + QJAR工具.exe
.\build.ps1 -Tests          # 额外编译并运行通信冒烟与界面回归
.\build.ps1 -Tests -Package # 再打一个 dist\QJAR工具-v1.1.16.zip
```

编译页依赖本机的 WSL 2 与 `/opt/qjar-build` 环境。环境包可从[公开 Release](https://github.com/zizhuoruzi/qj-wsl-env/releases/tag/wsl-env-v1)下载，解压密码由发布者授权提供。`QJAR工具.exe --guide` 打开环境准备指引；提示词单一来源为 [wsl-agent-prompt.txt](<Assets/wsl-agent-prompt.txt>)，构建时同步生成 [agent-wsl-setup.md](<Docs/agent-wsl-setup.md>)。选择工程目录会记住上次位置，并在下三级内查找唯一工程，多个候选不会自动选中。

运行时不需要安装 .NET（使用系统自带的 .NET Framework 4.x）；示教器自动重启使用系统 OpenSSH 客户端。

## 测试

- `Smoke.exe`：对进程内模拟控制器验证查询、注册（含轮询等待）、裸命令收发。
- `UiCheck.exe`：覆盖登录、日志跟随、设备查询、注册后自动重启、命令收发和 Makefile 读写。设备操作使用模拟控制器和 SSH 替身。
- `BuildCheck.exe`：只测 `ProjectMakefile` 的解析、校验与原子写回（版本号、DRIVER、行尾、编码）。
- `BuildRun.exe <控制器工程目录>`：手动集成检查，用真实窗口走一次 WSL 编译 + 打包（需要本机 WSL 环境，不在自动回归里）。

## 说明

- 该项目面向内部设备维护，口令与设备地址按现场环境硬编码，公开仓库里的值仅作示例。
- 标题字体为源流明体裁剪版（`Assets/QJToolDisplay.otf`），许可见 `Assets/Fonts/OFL.txt`。
- 图标来自用户提供的猴子造型素材。
