#include <stdio.h>
#include <stdlib.h>

/* OpenSSH 的 SSH_ASKPASS 助手：只把环境变量里的口令原样写到标准输出。
   口令不经过命令行、不落盘；由调用方在子进程环境里临时提供。 */
int main(void) {
    const char *password = getenv("QJAR_SSH_PASSWORD");
    if (!password) return 1;
    fputs(password, stdout);
    fputc('\n', stdout);
    return fflush(stdout) == 0 ? 0 : 1;
}