#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

int wmain(int argc, wchar_t **argv) {
    wchar_t path[32768], mode[32];
    FILE *file = NULL;
    int i;
    BOOL target = FALSE, batch = FALSE, trust = FALSE, config = FALSE;
    for (i = 1; i < argc; ++i) {
        if (wcscmp(argv[i], L"root@192.168.1.200") == 0) target = TRUE;
        if (wcscmp(argv[i], L"BatchMode=yes") == 0) batch = TRUE;
        if (wcscmp(argv[i], L"StrictHostKeyChecking=accept-new") == 0) trust = TRUE;
        if (wcscmp(argv[i], L"-F") == 0 && i + 1 < argc && wcscmp(argv[i + 1], L"NUL") == 0) config = TRUE;
    }
    if (!target || !batch || !trust || !config || !wcsstr(argv[argc - 1], L"sleep 2; reboot")) return 98;
    if (GetEnvironmentVariableW(L"QJ_TEST_SSH_RECORD", path, _countof(path))) {
        if (_wfopen_s(&file, path, L"ab") == 0) { fputs("restart\n", file); fclose(file); }
    }
    GetEnvironmentVariableW(L"QJ_TEST_SSH_MODE", mode, _countof(mode));
    if (wcscmp(mode, L"hang") == 0) { Sleep(30000); return 1; }
    if (wcscmp(mode, L"denied") == 0) { fputs("Permission denied", stderr); return 255; }
    if (wcscmp(mode, L"hostkey") == 0) { fputs("Host key verification failed", stderr); return 255; }
    if (wcscmp(mode, L"noack") == 0) return 0;
    puts("QJ_REBOOT_ACCEPTED");
    return 0;
}
