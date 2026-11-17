#ifndef REGISTRATION_PENDANT_H
#define REGISTRATION_PENDANT_H

#include "Controller.h"

/* ssh 为可信 SSH 客户端的绝对路径；NULL 使用 Windows OpenSSH。
   registered 为 TRUE 时仅在注册确实生效后才重启，FALSE 时按手动重启处理。 */
void pendant_restart(const wchar_t *ssh, const Request *request, Result *result, BOOL registered);

#endif