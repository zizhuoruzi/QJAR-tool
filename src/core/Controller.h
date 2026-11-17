#ifndef REGISTRATION_CONTROLLER_H
#define REGISTRATION_CONTROLLER_H

#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>

typedef enum { NOTICE_LOG, NOTICE_CONNECTED, NOTICE_STATE, NOTICE_PROGRESS } NoticeType;
typedef struct {
    NoticeType type;
    wchar_t text[256];
    char serial[256];
    int flag, days, progress;
} Notice;

typedef enum { RESULT_OK, RESULT_FAILED, RESULT_CANCELED, RESULT_PENDING } Outcome;
typedef struct {
    Outcome outcome;
    wchar_t message[256];
    char serial[256];
    int flag, days;
    BOOL has_state, reachable, submitted;
} Result;

typedef struct {
    char ip[16];
    unsigned short port;
    BOOL activate;
    HANDLE cancel;
    DWORD timeout_ms, verify_ms, poll_ms;
    void (*notify)(void *context, const Notice *notice);
    void *context;
} Request;

void controller_run(const Request *request, Result *result);

#endif
