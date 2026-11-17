#ifndef QJCORE_H
#define QJCORE_H

/*
 * QJCore.dll 的对外接口。C# 外壳通过 P/Invoke 调用，全部为 cdecl。
 *
 * 结构体布局（x64，与 Controller.h 一致）：
 *   Notice { int type; wchar_t text[256]; char serial[256]; int flag, days, progress; }  // 784 字节
 *   Result { int outcome; wchar_t message[256]; char serial[256]; int flag, days;
 *            int has_state, reachable, submitted; }                                    // 792 字节
 *   Request { char ip[16]; unsigned short port; int activate; HANDLE cancel;
 *             unsigned int timeout_ms, verify_ms, poll_ms;
 *             void (*notify)(void*, const Notice*); void* context; }                   // 64 字节
 *
 * Notice.type: 0 日志、1 已连接、2 状态、3 进度。
 * Result.outcome: 0 成功、1 失败、2 已取消、3 待确认。
 */

#include "Controller.h"

/* 查询 / 注册流程；activate 为 FALSE 时只查询。 */
void controller_run(const Request *request, Result *result);

/* 通过 SSH 重启示教器；registered 为 TRUE 表示注册成功后自动触发。 */
void pendant_restart(const wchar_t *ssh, const Request *request, Result *result, BOOL registered);

/* 单次命令收发：命令与数据按字节传入（编码由调用方决定），0 成功、-1 失败、-2 已取消。 */
int qj_exchange(const char *ip, unsigned short port, unsigned int timeout_ms,
    const char *command, unsigned int command_len,
    const char *data, unsigned int data_len,
    char *output, unsigned int capacity, unsigned int *output_length,
    HANDLE cancel, wchar_t *error, unsigned int error_capacity);

#endif