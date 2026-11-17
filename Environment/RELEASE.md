# WSL 32 位控制器编译环境 v1

- GCC/G++ 4.6.3、make 3.81、glibc 2.15、Xenomai 2.6.4。
- 编译目标：同代际 xeno-x86 / 3.14.17-xenomai；实体轴 ETHERCAT_X86 与虚拟轴 VIRTUAL_SERVO。
- 环境包附带可移植的 prepare-rootfs.sh、build-controller.sh、包版本及文件校验清单。
- 仅环境内容，无控制器项目源码、编译产物、导出日志、现场配置或登录凭据。

公开下载，7z AES 加密并加密文件名；解压密码由发布者授权提供。本 Release 不提供密码。

文件：qj-wsl-env.7z，332,733,570 字节（约 317.3 MiB）。

SHA-256：

```text
e00dfcabc34243ee10072a0871a365b3b87ee47c44fede6808be6658703d3084
```

在独立 WSL 目录完成恢复测试：三个原环境 tar 的 SHA-256 通过；GCC 4.6.3、make 3.81、Xenomai 2.6.4 输出匹配；一个真实编译的 32 位 C++ 程序运行并返回预期值。测试未改动已有编译环境。

说明及使用命令见仓库 README。仅适用于 Windows x64 + WSL 2；不是 ARM/任意项目工具链，也不提供实时运行或控制器部署。
