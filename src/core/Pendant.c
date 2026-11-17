#include "Pendant.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

static void report(const Request *request, NoticeType type, const wchar_t *text, int progress) {
    Notice n = {0};
    n.type = type; n.progress = progress;
    wcscpy_s(n.text, _countof(n.text), text);
    if (request->notify) request->notify(request->context, &n);
}

/* registered 为 TRUE 时是注册成功后的自动重启，必须先确认注册已生效；
   FALSE 是界面上的手动“重启示教器”，只要求调用方给了 activate。 */
void pendant_restart(const wchar_t *ssh, const Request *request, Result *result, BOOL registered) {
    wchar_t path[MAX_PATH], command[2048];
    const wchar_t *message = L"示教器 SSH 连接或重启失败";
    SECURITY_ATTRIBUTES security = {sizeof(security), NULL, TRUE};
    STARTUPINFOW startup = {0};
    PROCESS_INFORMATION process = {0};
    HANDLE reader = NULL, writer = NULL, input = INVALID_HANDLE_VALUE;
    char output[4096] = {0}, chunk[512];
    DWORD count, available, exit_code = 1;
    size_t used = 0;
    ULONGLONG deadline;
    BOOL started = FALSE, exited = FALSE;
    if (!request->activate) return;
    if (registered && (result->outcome != RESULT_OK || !result->submitted ||
        !result->has_state || result->flag <= 0)) return;
    if (request->cancel && WaitForSingleObject(request->cancel, 0) == WAIT_OBJECT_0) {
        message = L"示教器重启已取消"; goto done;
    }
    if (!ssh) {
        UINT length = GetSystemDirectoryW(path, _countof(path));
        if (!length || length >= _countof(path) - 17) goto done;
        wcscat_s(path, _countof(path), L"\\OpenSSH\\ssh.exe");
        ssh = path;
    }
    if (GetFileAttributesW(ssh) == INVALID_FILE_ATTRIBUTES) {
        message = L"注册成功，未找到 OpenSSH，示教器未重启"; goto done;
    }
    /* 先回传接收标记再延迟重启，避免正常断线被当作 SSH 失败。 */
    if (_snwprintf_s(command, _countof(command), _TRUNCATE,
        L"\"%s\" -F NUL -T -o BatchMode=yes -o PreferredAuthentications=none,password "
        L"-o NumberOfPasswordPrompts=0 -o StrictHostKeyChecking=accept-new "
        L"-o ConnectTimeout=5 -o ConnectionAttempts=1 -o ServerAliveInterval=2 "
        L"-o ServerAliveCountMax=2 root@192.168.1.200 "
        L"\"command -v reboot >/dev/null || exit 127; "
        L"(sleep 2; reboot) </dev/null >/dev/null 2>&1 & echo QJ_REBOOT_ACCEPTED\"", ssh) < 0) goto done;
    if (!CreatePipe(&reader, &writer, &security, 0) ||
        !SetHandleInformation(reader, HANDLE_FLAG_INHERIT, 0)) goto done;
    input = CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
        &security, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (input == INVALID_HANDLE_VALUE) goto done;
    startup.cb = sizeof(startup); startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = input; startup.hStdOutput = startup.hStdError = writer;
    report(request, NOTICE_PROGRESS, L"正在重启示教器", 95);
    report(request, NOTICE_LOG, L"正在连接示教器 192.168.1.200。", 0);
    if (!CreateProcessW(ssh, command, NULL, NULL, TRUE, CREATE_NO_WINDOW,
        NULL, NULL, &startup, &process)) goto done;
    started = TRUE;
    CloseHandle(writer); writer = NULL;
    CloseHandle(input); input = INVALID_HANDLE_VALUE;
    deadline = GetTickCount64() + 15000;
    for (;;) {
        while (PeekNamedPipe(reader, NULL, 0, NULL, &available, NULL) && available) {
            DWORD take = available < sizeof(chunk) ? available : sizeof(chunk);
            size_t copy;
            if (!ReadFile(reader, chunk, take, &count, NULL) || !count) break;
            copy = count < sizeof(output) - 1 - used ? count : sizeof(output) - 1 - used;
            memcpy(output + used, chunk, copy); used += copy; output[used] = 0;
        }
        if (exited) break;
        if ((request->cancel && WaitForSingleObject(request->cancel, 0) == WAIT_OBJECT_0) ||
            GetTickCount64() >= deadline) {
            message = L"示教器重启结果待确认";
            TerminateProcess(process.hProcess, 1);
            WaitForSingleObject(process.hProcess, 1000);
            goto done;
        }
        exited = WaitForSingleObject(process.hProcess, 50) == WAIT_OBJECT_0;
    }
    if (GetExitCodeProcess(process.hProcess, &exit_code) && exit_code == 0 &&
        strstr(output, "QJ_REBOOT_ACCEPTED"))
        message = L"示教器重启命令已发送";
    else if (strstr(output, "Permission denied"))
        message = L"示教器 SSH 登录失败";
    else if (strstr(output, "Host key verification failed") || strstr(output, "REMOTE HOST IDENTIFICATION HAS CHANGED"))
        message = L"示教器 SSH 主机密钥校验失败";
done:
    if (reader) CloseHandle(reader);
    if (writer) CloseHandle(writer);
    if (input != INVALID_HANDLE_VALUE) CloseHandle(input);
    if (started) { CloseHandle(process.hThread); CloseHandle(process.hProcess); }
    SecureZeroMemory(output, sizeof(output));
    wcscpy_s(result->message, _countof(result->message), message);
    report(request, NOTICE_LOG, message, 0);
    report(request, NOTICE_PROGRESS, message, 100);
}
