#include "Controller.h"
#include <ws2tcpip.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>
#include <errno.h>
#include <limits.h>

#define HEADER_SIZE 28u
#define MAX_PACKET 2000028u

typedef struct {
    const Request *request;
    Result *result;
    SOCKET socket;
    wchar_t error[256];
    BOOL canceled;
} Client;

static uint32_t get_u32_le(const unsigned char *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
        ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static void put_u32_le(unsigned char *p, uint32_t value) {
    p[0] = (unsigned char)value;
    p[1] = (unsigned char)(value >> 8);
    p[2] = (unsigned char)(value >> 16);
    p[3] = (unsigned char)(value >> 24);
}

static uint32_t checksum_packet(const unsigned char *p, uint32_t length) {
    uint32_t sum = 0, i;
    for (i = 0; i < 4; ++i) sum += p[i];
    for (i = 8; i < length; ++i) sum += p[i];
    return sum;
}

static void notify(Client *c, NoticeType type, const wchar_t *text, int progress) {
    Notice n = {0};
    n.type = type;
    n.progress = progress;
    n.flag = c->result->flag;
    n.days = c->result->days;
    strcpy_s(n.serial, sizeof(n.serial), c->result->serial);
    if (text) wcscpy_s(n.text, _countof(n.text), text);
    if (c->request->notify) c->request->notify(c->request->context, &n);
}

static int fail(Client *c, const wchar_t *message) {
    wcscpy_s(c->error, _countof(c->error), message);
    return -1;
}

static BOOL canceled(Client *c) {
    if (c->request->cancel && WaitForSingleObject(c->request->cancel, 0) == WAIT_OBJECT_0) {
        c->canceled = TRUE;
        return TRUE;
    }
    return FALSE;
}

static int wait_socket(Client *c, BOOL write, ULONGLONG deadline) {
    while (!canceled(c)) {
        fd_set ready, errors;
        struct timeval timeout;
        ULONGLONG now = GetTickCount64();
        DWORD slice;
        int count;
        if (now >= deadline) return fail(c, L"通信超时。");
        slice = (DWORD)((deadline - now) < 50 ? deadline - now : 50);
        FD_ZERO(&ready); FD_SET(c->socket, &ready);
        FD_ZERO(&errors); FD_SET(c->socket, &errors);
        timeout.tv_sec = 0;
        timeout.tv_usec = (long)slice * 1000;
        count = select(0, write ? NULL : &ready, write ? &ready : NULL, &errors, &timeout);
        if (count == SOCKET_ERROR || FD_ISSET(c->socket, &errors))
            return fail(c, L"网络连接发生错误。");
        if (count > 0) return 0;
    }
    return -1;
}

static int transfer(Client *c, unsigned char *buffer, int length, BOOL write,
    BOOL activation, ULONGLONG deadline) {
    int offset = 0;
    while (offset < length) {
        int count, error;
        if (wait_socket(c, write, deadline) < 0 || canceled(c)) return -1;
        count = write ? send(c->socket, (const char *)buffer + offset, length - offset, 0)
            : recv(c->socket, (char *)buffer + offset, length - offset, 0);
        if (count == SOCKET_ERROR) {
            error = WSAGetLastError();
            if (error == WSAEWOULDBLOCK) continue;
            return fail(c, L"网络收发失败。");
        }
        if (count == 0) return fail(c, L"控制器已断开连接。");
        /* 发送过注册报文后，断连或取消都不能证明设备没有执行写入。 */
        if (write && activation) c->result->submitted = TRUE;
        offset += count;
    }
    return 0;
}

static int connect_controller(Client *c) {
    struct sockaddr_in address = {0};
    u_long nonblocking = 1;
    int error, length = sizeof(error), rc;
    ULONGLONG deadline = GetTickCount64() + c->request->timeout_ms;
    address.sin_family = AF_INET;
    address.sin_port = htons(c->request->port);
    if (!c->request->port || InetPtonA(AF_INET, c->request->ip, &address.sin_addr) != 1)
        return fail(c, L"IP 地址或端口无效。");
    c->socket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (c->socket == INVALID_SOCKET) return fail(c, L"无法创建网络连接。");
    if (ioctlsocket(c->socket, FIONBIO, &nonblocking) != 0)
        return fail(c, L"无法初始化网络连接。");
    rc = connect(c->socket, (struct sockaddr *)&address, sizeof(address));
    if (rc == SOCKET_ERROR) {
        error = WSAGetLastError();
        if (error != WSAEWOULDBLOCK && error != WSAEINPROGRESS)
            return fail(c, L"无法连接控制器，请检查地址和网络。");
        if (wait_socket(c, TRUE, deadline) < 0) return -1;
        if (getsockopt(c->socket, SOL_SOCKET, SO_ERROR, (char *)&error, &length) != 0 || error)
            return fail(c, L"无法连接控制器，请检查地址和网络。");
    }
    if (canceled(c)) return -1;
    c->result->reachable = TRUE;
    notify(c, NOTICE_CONNECTED, L"连接成功。", 15);
    return 0;
}

static int invoke(Client *c, const char *command, const char *data,
    char *output, size_t capacity, ULONGLONG limit) {
    size_t cl = strlen(command) + 1, dl = data ? strlen(data) + 1 : 1;
    uint32_t length = HEADER_SIZE + (uint32_t)cl + (uint32_t)dl;
    unsigned char prefix[4], *packet = (unsigned char *)calloc(1, length);
    ULONGLONG deadline = GetTickCount64() + c->request->timeout_ms;
    uint32_t response_cl, response_dl;
    int rc = -1;
    if (limit && deadline > limit) deadline = limit;
    if (!packet) return fail(c, L"内存不足。");
    put_u32_le(packet, length);
    put_u32_le(packet + 20, (uint32_t)cl);
    put_u32_le(packet + 24, (uint32_t)dl);
    memcpy(packet + HEADER_SIZE, command, cl);
    if (data) memcpy(packet + HEADER_SIZE + cl, data, dl);
    put_u32_le(packet + 4, checksum_packet(packet, length));
    rc = transfer(c, packet, (int)length, TRUE, strcmp(command, "actlicense") == 0, deadline);
    SecureZeroMemory(packet, length);
    free(packet);
    if (rc < 0 || transfer(c, prefix, 4, FALSE, FALSE, deadline) < 0) return -1;
    length = get_u32_le(prefix);
    if (length < HEADER_SIZE + 2 || length > MAX_PACKET)
        return fail(c, L"控制器返回的报文长度无效。");
    packet = (unsigned char *)malloc(length);
    if (!packet) return fail(c, L"内存不足。");
    memcpy(packet, prefix, 4);
    rc = -1;
    if (transfer(c, packet + 4, (int)length - 4, FALSE, FALSE, deadline) < 0) goto done;
    if (get_u32_le(packet + 4) != checksum_packet(packet, length)) {
        fail(c, L"控制器报文校验失败。"); goto done;
    }
    response_cl = get_u32_le(packet + 20);
    response_dl = get_u32_le(packet + 24);
    /* 逐项减法检查，避免设备提供的长度相加后溢出。 */
    if (response_cl < 1 || response_dl < 1 || response_cl > length - HEADER_SIZE ||
        response_dl != length - HEADER_SIZE - response_cl || response_dl > capacity ||
        packet[HEADER_SIZE + response_cl - 1] != 0 || packet[length - 1] != 0 ||
        memchr(packet + HEADER_SIZE, 0, response_cl - 1) ||
        memchr(packet + HEADER_SIZE + response_cl, 0, response_dl - 1)) {
        fail(c, L"控制器报文内容长度无效。"); goto done;
    }
    if (strcmp((char *)packet + HEADER_SIZE, command) != 0) {
        fail(c, L"控制器返回了错误或不匹配的命令。"); goto done;
    }
    memcpy(output, packet + HEADER_SIZE + response_cl, response_dl);
    while (response_dl > 1 && isspace((unsigned char)output[response_dl - 2]))
        output[--response_dl - 1] = 0;
    rc = 0;
done:
    SecureZeroMemory(packet, length);
    free(packet);
    return rc;
}

static int parse_state(Client *c, const char *text) {
    char *end;
    long flag, days;
    errno = 0;
    flag = strtol(text, &end, 10);
    if (end == text || *end != ',' || errno == ERANGE || flag < INT_MIN || flag > INT_MAX)
        return fail(c, L"授权状态格式无效。");
    text = end + 1;
    errno = 0;
    days = strtol(text, &end, 10);
    while (isspace((unsigned char)*end)) ++end;
    if (end == text || *end || errno == ERANGE || days < INT_MIN || days > INT_MAX)
        return fail(c, L"授权剩余天数格式无效。");
    c->result->flag = (int)flag;
    c->result->days = (int)days;
    c->result->has_state = TRUE;
    return 0;
}

static BOOL printable(const char *text) {
    const unsigned char *p = (const unsigned char *)text;
    for (; *p; ++p) if (*p < 32 || *p > 126) return FALSE;
    return TRUE;
}

void controller_run(const Request *request, Result *result) {
    Client c = {0};
    WSADATA wsa;
    char buffer[1024] = {0}, key[1024] = {0};
    ULONGLONG deadline;
    wchar_t line[256];
    BOOL initialized = FALSE;
    memset(result, 0, sizeof(*result));
    result->outcome = RESULT_FAILED;
    c.request = request; c.result = result; c.socket = INVALID_SOCKET;
    if (!request->timeout_ms || !request->verify_ms || !request->poll_ms) {
        fail(&c, L"通信时间参数无效。"); goto done;
    }
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        fail(&c, L"Windows 网络初始化失败。"); goto done;
    }
    initialized = TRUE;
    notify(&c, NOTICE_PROGRESS, L"正在连接控制器", 5);
    if (connect_controller(&c) < 0) goto done;
    notify(&c, NOTICE_PROGRESS, L"正在查询设备", 25);
    if (invoke(&c, "getlicstate", NULL, buffer, sizeof(buffer), 0) < 0 ||
        parse_state(&c, buffer) < 0) goto done;
    if (invoke(&c, "getsn", NULL, result->serial, sizeof(result->serial), 0) < 0) goto done;
    if (strlen(result->serial) < 20 || !printable(result->serial) || strcmp(result->serial, "error") == 0) {
        fail(&c, L"设备序列号无效。"); goto done;
    }
    swprintf_s(line, _countof(line), L"序列号：%hs", result->serial);
    notify(&c, NOTICE_LOG, line, 0);
    notify(&c, NOTICE_STATE, NULL, 40);
    if (!request->activate || result->flag > 0) {
        wcscpy_s(result->message, _countof(result->message), result->flag > 0 ? L"授权有效" : L"设备尚未注册");
        swprintf_s(line, _countof(line), L"查询完成，%s。", result->message);
        notify(&c, NOTICE_LOG, line, 0);
        result->outcome = RESULT_OK;
        goto done;
    }
    notify(&c, NOTICE_PROGRESS, L"正在获取授权信息", 50);
    notify(&c, NOTICE_LOG, L"正在获取授权信息。", 0);
    if (invoke(&c, "getlic", NULL, key, sizeof(key), 0) < 0) goto done;
    if (strlen(key) < 17 || !printable(key) || strcmp(key, "error") == 0) {
        fail(&c, L"获取授权信息失败。"); goto done;
    }
    notify(&c, NOTICE_PROGRESS, L"正在提交注册", 65);
    if (invoke(&c, "actlicense", key, buffer, sizeof(buffer), 0) < 0) goto done;
    SecureZeroMemory(key, sizeof(key));
    if (strcmp(buffer, "ok") != 0) {
        result->submitted = FALSE;
        fail(&c, L"控制器拒绝注册。"); goto done;
    }
    notify(&c, NOTICE_LOG, L"注册请求已提交，正在验证授权状态。", 0);
    notify(&c, NOTICE_PROGRESS, L"等待授权生效", 80);
    deadline = GetTickCount64() + request->verify_ms;
    while (GetTickCount64() < deadline && !canceled(&c)) {
        ULONGLONG now;
        DWORD wait;
        if (invoke(&c, "getlicstate", NULL, buffer, sizeof(buffer), deadline) < 0 ||
            parse_state(&c, buffer) < 0) goto done;
        if (result->flag > 0) {
            notify(&c, NOTICE_STATE, NULL, 100);
            wcscpy_s(result->message, _countof(result->message), L"注册成功");
            notify(&c, NOTICE_LOG, L"注册成功。", 0);
            result->outcome = RESULT_OK;
            goto done;
        }
        now = GetTickCount64();
        wait = now >= deadline ? 0 : (DWORD)(deadline - now);
        if (wait > request->poll_ms) wait = request->poll_ms;
        if (request->cancel) WaitForSingleObject(request->cancel, wait);
        else Sleep(wait);
    }
    if (!canceled(&c)) fail(&c, L"验证超时。");
done:
    SecureZeroMemory(key, sizeof(key));
    SecureZeroMemory(buffer, sizeof(buffer));
    if (c.socket != INVALID_SOCKET) closesocket(c.socket);
    if (initialized) WSACleanup();
    if (result->outcome != RESULT_OK) {
        if (c.canceled) {
            result->outcome = result->submitted ? RESULT_PENDING : RESULT_CANCELED;
            wcscpy_s(result->message, _countof(result->message), result->submitted ? L"注册结果待确认" : L"操作已取消");
        } else {
            result->outcome = result->submitted ? RESULT_PENDING : RESULT_FAILED;
            wcscpy_s(result->message, _countof(result->message), result->submitted ? L"注册结果待确认" : c.error);
            if (c.error[0] && wcscmp(c.error, result->message) != 0) notify(&c, NOTICE_LOG, c.error, 0);
        }
        notify(&c, NOTICE_LOG, result->message, 0);
    }
}

/* 命令调试页用的单次收发：不涉及授权流程，只把 command/data 发出去取回 data 字段。
   命令和数据按字节传入，编码由界面层决定（UTF-8 / GBK），这里只负责协议帧。
   返回 0 成功、-1 失败（error 为中文原因）、-2 已取消。 */
int qj_exchange(const char *ip, unsigned short port, unsigned int timeout_ms,
    const char *command, unsigned int command_len,
    const char *data, unsigned int data_len,
    char *output, unsigned int capacity, unsigned int *output_length,
    HANDLE cancel, wchar_t *error, unsigned int error_capacity) {
    Client c = {0};
    Request request = {0};
    Result result = {0};
    WSADATA wsa;
    BOOL initialized = FALSE;
    char *cmd = NULL, *body = NULL;
    int rc = -1;
    if (error && error_capacity) error[0] = 0;
    if (!ip || !command || !command_len || !output || capacity < 2) {
        if (error && error_capacity) wcscpy_s(error, error_capacity, L"命令或输出缓冲区无效。");
        return -1;
    }
    cmd = (char *)malloc((size_t)command_len + 1);
    if (!cmd) {
        if (error && error_capacity) wcscpy_s(error, error_capacity, L"内存不足。");
        return -1;
    }
    memcpy(cmd, command, command_len);
    cmd[command_len] = 0;
    if (data && data_len) {
        body = (char *)malloc((size_t)data_len + 1);
        if (!body) {
            SecureZeroMemory(cmd, (size_t)command_len + 1);
            free(cmd);
            if (error && error_capacity) wcscpy_s(error, error_capacity, L"内存不足。");
            return -1;
        }
        memcpy(body, data, data_len);
        body[data_len] = 0;
    }
    if (!timeout_ms) timeout_ms = 5000;
    strncpy_s(request.ip, sizeof(request.ip), ip, _TRUNCATE);
    request.port = port;
    request.timeout_ms = timeout_ms;
    request.cancel = cancel;
    c.request = &request;
    c.result = &result;
    c.socket = INVALID_SOCKET;
    output[0] = 0;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        fail(&c, L"Windows 网络初始化失败。");
        goto done;
    }
    initialized = TRUE;
    if (connect_controller(&c) < 0) goto done;
    if (invoke(&c, cmd, body, output, capacity, 0) < 0) goto done;
    if (output_length) *output_length = (unsigned int)strlen(output);
    rc = 0;
done:
    if (c.socket != INVALID_SOCKET) closesocket(c.socket);
    if (initialized) WSACleanup();
    if (cmd) { SecureZeroMemory(cmd, (size_t)command_len + 1); free(cmd); }
    if (body) { SecureZeroMemory(body, (size_t)data_len + 1); free(body); }
    if (rc < 0 && error && error_capacity) {
        if (c.canceled) rc = -2;
        wcsncpy_s(error, error_capacity, c.error[0] ? c.error : L"操作已取消。", _TRUNCATE);
    }
    return rc;
}
