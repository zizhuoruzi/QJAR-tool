#!/usr/bin/env bash

sync_makefile_driver() (
    local file=$1 driver=$2 temp
    case "$driver" in ETHERCAT_X86|VIRTUAL_SERVO) ;; *) return 2 ;; esac
    if [[ ! -f $file || -L $file ]]; then
        printf '无法读取或更新 Makefile：%s\n' "$file" >&2
        return 1
    fi
    temp=$(mktemp -- "$file.driver.XXXXXX") || return 1
    trap 'rm -f -- "$temp"' EXIT
    if ! LC_ALL=C awk -v driver="$driver" '
        {
            cr = sub(/\r$/, "") ? "\r" : ""
            if (cr != "") ending = cr
            if (match($0, /^ *((override|export)[ \t]+)*DRIVER[ \t]*[:?+]?=[ \t]*/)) {
                prefix = substr($0, 1, RLENGTH)
                value = substr($0, RLENGTH + 1)
                if (value ~ /\\[ \t]*$/) { unsupported = 1; exit 2 }
                comment = match(value, /#/) ? substr(value, RSTART) : ""
                sub(/\+=[ \t]*$/, "= ", prefix)
                if (prefix !~ /[ \t]$/) prefix = prefix " "
                print prefix driver comment cr
                found = 1
            } else print $0 cr
        }
        END { if (!found && !unsupported) print "DRIVER = " driver ending }
    ' "$file" > "$temp"; then
        printf '无法同步 Makefile 的 DRIVER：%s\n' "$file" >&2
        return 1
    fi
    if ! cmp -s -- "$file" "$temp"; then
        chmod --reference="$file" "$temp" && mv -T -- "$temp" "$file" || return 1
        printf 'Makefile 的 DRIVER 已同步为 %s。\n' "$driver"
    else
        printf 'Makefile 的 DRIVER 已确认是 %s。\n' "$driver"
    fi
)

run_desktop_ssh_sudo() {
    local helper=$HOME/.local/lib/qj-controller-build/qj-build-keyring.py
    local pid value
    local -a session_env
    if [[ -x $helper ]]; then
        for pid in $(pgrep -u "$EUID" -x gnome-session || true); do
            session_env=()
            while IFS= read -r -d '' value; do
                case "$value" in
                    DBUS_SESSION_BUS_ADDRESS=*|DISPLAY=*|GNOME_KEYRING_CONTROL=*|GNOME_KEYRING_PID=*|XDG_RUNTIME_DIR=*)
                        session_env+=("$value") ;;
                esac
            done 2>/dev/null < "/proc/$pid/environ" || continue
            if [[ -n ${session_env[0]:-} ]] &&
                env "${session_env[@]}" /usr/bin/timeout 4 /usr/bin/python "$helper" available >/dev/null 2>&1; then
                if env "${session_env[@]}" SUDO_ASKPASS="$helper" sudo -A -v; then
                    env "${session_env[@]}" SUDO_ASKPASS="$helper" sudo -A -- "$@" || return 1
                    return 0
                fi
                break
            fi
        done
    fi
    sudo -n -v >/dev/null 2>&1 || return 73
    sudo -n -- "$@" || return 1
}

build_controller() (
    set -euo pipefail

    local driver=$1
    shift
    local action=build
    local stop_before_build=0 ssh_start=0 ssh_password=''
    if [[ ${1:-} == --build-only || ${1:-} == --stop-and-build ]]; then
        if [[ $1 == --stop-and-build ]]; then stop_before_build=1; fi
        action=build_only
        shift
    elif [[ ${1:-} == --package-built ]]; then
        action=package_built
        shift
    elif [[ ${1:-} == --package-installed ]]; then
        action=package_installed
        shift
    elif [[ ${1:-} == --start-ssh ]]; then
        [[ $driver == VIRTUAL_SERVO ]] || return 2
        action=start
        ssh_start=1
        shift
    elif [[ $driver == VIRTUAL_SERVO ]]; then
        case "${1:-}" in
            --start) action=start; shift ;;
            --build-and-start) action=build_and_start; shift ;;
        esac
    fi
    local number=${1:-} exact_dir='' root desktop project dir name zip target_zip dest stage='' unpacked tree='' log module answer attempt stop_status=0 committed=0
    local old_virtual=0
    local -a matches=()
    if [[ $number == --directory && $# -eq 2 ]]; then
        exact_dir=$2
        if [[ ! $exact_dir =~ ^([0-9]+)[^/]*$ ]]; then
            printf '无效的项目目录。\n' >&2
            return 2
        fi
        number=${BASH_REMATCH[1]}
    elif [[ $# -gt 1 ]]; then
        printf '用法：%s [目录编号]\n' "${0##*/}" >&2
        return 2
    fi
    if [[ -z $number ]]; then
        read -r -p '请输入目录编号（例如 11）: ' number || return 2
    fi
    if [[ ! $number =~ ^[0-9]+$ ]]; then
        printf '目录编号必须是数字。\n' >&2
        return 2
    fi

    if [[ ! -d $HOME/expspace ]]; then
        printf '找不到项目目录：%s/expspace\n' "$HOME" >&2
        return 2
    fi
    root=$(cd -- "$HOME/expspace" && pwd -P)
    if [[ -n $exact_dir ]]; then
        project=$root/$exact_dir
        if [[ ! -d $project ]]; then
            printf '目标目录不存在：%s\n' "$project" >&2
            return 2
        fi
    else
        for dir in "$root"/"$number"*; do
            [[ -d $dir ]] || continue
            name=${dir##*/}
            if [[ $name =~ ^([0-9]+) && ${BASH_REMATCH[1]} == "$number" ]]; then
                matches+=("$dir")
            fi
        done
        if (( ${#matches[@]} != 1 )); then
            printf '编号 %s 应唯一对应一个目录，在 %s 下找到 %d 个。\n' "$number" "$root" "${#matches[@]}" >&2
            if (( ${#matches[@]} != 0 )); then printf '  %s\n' "${matches[@]}" >&2; fi
            return 2
        fi
        project=${matches[0]}
    fi
    dest=$project/cx-v3.0
    if [[ -L $project ]]; then
        printf '编号目录不能是符号链接：%s\n' "$project" >&2
        return 2
    fi
    if [[ -L $dest || ( -e $dest && ! -d $dest ) ]]; then
        printf '现有工程的路径类型不符：%s\n' "$dest" >&2
        return 2
    fi
    case "$driver" in
        ETHERCAT_X86) module=ethercat_x86 ;;
        VIRTUAL_SERVO) module=virtual_servo ;;
        *) printf '不支持的编译目标：%s\n' "$driver" >&2; return 2 ;;
    esac

    exec 9> "$project/.cx-v3.0-build.lock"
    if ! flock -n 9; then
        printf '这个目录已有编译或启动脚本在运行：%s\n' "$project" >&2
        return 1
    fi

    desktop=$(xdg-user-dir DESKTOP 2>/dev/null || true)
    if [[ -z $desktop || $desktop == "$HOME" ]]; then desktop=$HOME/Desktop; fi
    zip=${QJ_CONTROLLER_BUILD_ARCHIVE:-$desktop/cx-v3.0.zip}
    target_zip=$project/cx-v3.0.zip
    local receipt=${QJ_CONTROLLER_BUILD_RECEIPT:-}
    match_build_receipt() {
        local built_identity log_name
        {
            IFS= read -r built_identity && IFS= read -r log_name
        } < "$receipt" || return 1
        [[ $built_identity == "$(stat -c '%d:%i' -- "$dest")" &&
            $log_name =~ ^build-${driver}\.log\.[A-Za-z0-9]{6}$ ]] || return 1
        log=$project/$log_name
        [[ -f $log && ! -L $log ]]
    }
    package_controller() {
        local package=$dest/src/control_update.tar.gz
        sync_makefile_driver "$dest/src/Makefile" "$driver" | tee -a "$log" || return 1
        if ! (cd -- "$dest/src" && make tar SPACE=USER BUILD_KERNEL=xeno-x86 "DRIVER=$driver") 2>&1 | tee -a "$log"; then
            printf '打包失败。已编译的工程保留，详见 %s\n' "$log" >&2
            return 1
        fi
        if [[ ! -s $package ]]; then
            printf '没有找到打包产物：%s\n' "$package" >&2
            return 1
        fi
    }
    if [[ $action == package_built || $action == package_installed ]]; then
        if [[ ! -f $dest/bin/SPACE ]] || ! grep -qx "USER $driver" "$dest/bin/SPACE"; then
            printf '目标工程的已部署轴型与所选轴型不符，无法打包：%s\n' "$dest" >&2
            return 1
        fi
        if [[ $action == package_installed ]]; then
            if [[ ! -x $dest/bin/user_task || ! -f $dest/rtlib/$module.so || ! -r $dest/src/Makefile ]]; then
                printf '目标工程缺少所选轴型的编译产物，无法打包：%s\n' "$dest" >&2
                return 1
            fi
            local candidate
            log=''
            for candidate in "$project"/build-*.log.* "$project"/package-*.log.*; do
                [[ -f $candidate && ! -L $candidate ]] || continue
                if [[ -z $log || $candidate -nt $log ]]; then log=$candidate; fi
            done
            if [[ -z $log ]]; then log=$(mktemp "$project/package-${driver}.log.XXXXXX"); fi
        elif [[ -n $receipt ]]; then
            if ! match_build_receipt; then
                printf '目标工程已变更，无法为本次编译生成包：%s\n' "$dest" >&2
                return 1
            fi
        else
            if [[ -L $target_zip ]] || ! cmp -s -- "$zip" "$target_zip"; then
                printf '目标工程已变更，无法为本次编译生成包：%s\n' "$dest" >&2
                return 1
            fi
            log=$(mktemp "$project/package-${driver}.log.XXXXXX")
        fi
        if ! package_controller; then
            if [[ -n ${QJ_CONTROLLER_BUILD_DOWNLOAD:-} ]]; then rm -f -- "$dest/src/control_update.tar.gz"; fi
            return 1
        fi
        if [[ -n ${QJ_CONTROLLER_BUILD_DOWNLOAD:-} ]]; then
            if ! mv -T -- "$dest/src/control_update.tar.gz" "$QJ_CONTROLLER_BUILD_DOWNLOAD"; then
                rm -f -- "$dest/src/control_update.tar.gz"
                return 1
            fi
            printf '打包成功，更新包等待下载。\n'
            printf 'QJPKG:'
            sha256sum -- "$QJ_CONTROLLER_BUILD_DOWNLOAD" | cut -d ' ' -f1
        else
            printf '打包成功：%s/src/control_update.tar.gz\n' "$dest"
        fi
        return 0
    fi
    if [[ $action == start ]]; then
        if [[ ! -x $dest/bin/control || ! -x $dest/bin/user_task || ! -f $dest/rtlib/$module.so ||
            ! -f $dest/bin/SPACE ]] || ! grep -qx "USER $driver" "$dest/bin/SPACE"; then
            printf '没有可启动的虚拟控制器版本，请先选择“编译后启动”：%s\n' "$dest" >&2
            return 2
        fi
        if (( ssh_start )) && [[ -n $receipt ]] && ! match_build_receipt; then
            printf '目标工程在编译后已变更，请核对已部署版本再启动。\n' >&2
            return 1
        elif (( ssh_start )) && [[ -z $receipt && -n ${QJ_CONTROLLER_BUILD_ARCHIVE:-} ]] &&
            { [[ -L $target_zip ]] || ! cmp -s -- "$zip" "$target_zip"; }; then
            printf '目标工程在编译后已变更，请核对已部署版本再启动。\n' >&2
            return 1
        fi
    else
        if [[ ! -f $zip ]]; then
            printf '请把压缩包放到：%s\n' "$zip" >&2
            return 2
        fi
        if [[ -L $target_zip || ( -e $target_zip && ! -f $target_zip ) ]]; then
            printf '现有压缩包的路径类型不符：%s\n' "$target_zip" >&2
            return 2
        fi
    fi

    run_controller_sudo() {
        local keyring_helper=${BASH_SOURCE[0]%/*}/qj-build-keyring.py
        if /usr/bin/python "$keyring_helper" available 2>/dev/null; then
            SUDO_ASKPASS="$keyring_helper" sudo -A "$@"
        else
            sudo "$@"
        fi
    }

    run_ssh_controller_sudo() {
        if [[ ${QJ_CONTROLLER_SUDO_STDIN:-0} != 1 ]]; then
            run_desktop_ssh_sudo "$@"
        elif (( ssh_start )); then
            printf '%s\n' "$ssh_password" | sudo -S -p '' -- "$@"
        else
            sudo -S -p '' -- "$@"
        fi
    }

    controller_processes() (
        local pid stat name command own=0 other=0 task=0 other_task=0
        while read -r pid name; do
            if ! IFS= read -r stat 2>/dev/null < "/proc/$pid/stat"; then continue; fi
            stat=${stat##*) }
            [[ $stat != Z* && $stat != X* ]] || continue
            if ! IFS= read -r -d '' command 2>/dev/null < "/proc/$pid/cmdline"; then
                if [[ $name == rtapi_app && -d /proc/$pid ]]; then other=1; fi
                if [[ $name == user_task && -d /proc/$pid ]]; then other_task=1; fi
                continue
            fi
            if [[ $name == rtapi_app ]]; then
                if [[ $command == "$dest/bin/rtapi_app" ]]; then own=1; else other=1; fi
            elif [[ $command == "$dest/bin/user_task" ]]; then
                task=1
            else
                other_task=1
            fi
        done < <(
            for name in rtapi_app user_task; do
                for pid in $(pgrep -x "$name" || true); do
                    printf '%s %s\n' "$pid" "$name"
                done
            done
        )
        case "$1" in
            exclusive) (( own && !other && !other_task )) ;;
            any_rtapi) (( own || other )) ;;
            any_controller) (( own || other || task || other_task )) ;;
            ready) (( own && task && !other && !other_task )) ;;
            *) (( own || task )) ;;
        esac
    )
    target_running() {
        controller_processes target
    }
    start_virtual_controller() (
        local startup_log=${QJ_CONTROLLER_STARTUP_LOG:-} attempt ready=0
        local console=${QJ_CONTROLLER_CONSOLE:-} capture capture_pid
        local launch='nohup setsid ./control task </dev/null &' launch_status=0
        if controller_processes any_controller; then
            printf '发现正在运行的控制器进程，请先停止对应控制器，再启动这份虚拟版。\n' >&2
            return 1
        fi
        if [[ -n $console ]]; then
            mkfifo -- "$console/output" || return 1
            exec 6<> "$console/output" || return 1
            capture='
import os
import sys

folder = sys.argv[1]
path = os.path.join(folder, "console.log")
limit = 4194304
output = None
try:
    with open(os.path.join(folder, "output"), "rb", 0) as stream:
        output = open(path, "ab", 0)
        with open(os.path.join(folder, "pid"), "w") as pid_file:
            pid_file.write(str(os.getpid()) + "\n")
        while True:
            chunk = os.read(stream.fileno(), 4096)
            if not chunk:
                break
            if not os.path.isdir(folder):
                if output is not None:
                    output.close()
                    output = None
                continue
            try:
                if output is None:
                    output = open(path, "ab", 0)
                if output.tell() + len(chunk) > limit:
                    output.close()
                    output = None
                    os.rename(path, path + ".old")
                    output = open(path, "wb", 0)
                output.write(chunk)
            except IOError:
                if output is not None:
                    output.close()
                    output = None
            except OSError:
                pass
finally:
    if output is not None:
        output.close()
'
            nohup setsid /usr/bin/python -u -c "$capture" "$console" </dev/null >/dev/null 2>&1 6>&- 7>&- 8>&- 9>&- &
            capture_pid=$!
            for ((attempt=0; attempt<30; ++attempt)); do
                if [[ -s $console/pid ]]; then break; fi
                sleep 0.1
            done
            if [[ ! -s $console/pid ]]; then
                kill "$capture_pid" 2>/dev/null || true
                printf '无法打开虚拟控制器的实时日志。\n' >&2
                return 1
            fi
            launch='nohup setsid stdbuf -oL -eL ./control task </dev/null &'
        elif [[ -n $startup_log ]]; then
            launch='nohup setsid /bin/bash -c '"'"'./control task 2>&1 | { stdbuf -o0 head -c 4194304; cat >/dev/null; }'"'"' </dev/null &'
        else
            startup_log=$(mktemp "$project/start-VIRTUAL_SERVO.log.XXXXXX") || return 1
        fi
        printf '启动虚拟控制器：%s\n' "$dest"
        cd -- "$dest/bin" || return 1
        if (( ssh_start )); then
            if [[ -n $console ]]; then
                run_ssh_controller_sudo /bin/bash -c "$launch" >&6 2>&1 6>&- 7>&- 8>&- 9>&- || launch_status=$?
                exec 6>&-
                if (( launch_status == 73 )); then wait "$capture_pid" || true; fi
            else
                run_ssh_controller_sudo /bin/bash -c "$launch" \
                    7>&- 8>&- 9>&- >> "$startup_log" 2>&1 || launch_status=$?
            fi
            if (( launch_status == 73 )); then return 73; fi
        else
            printf '启动日志：%s\n' "$startup_log"
            run_controller_sudo /bin/bash -c "$launch" 9>&- >> "$startup_log" 2>&1 || launch_status=$?
        fi
        if (( launch_status != 0 )); then
            printf '未能启动虚拟控制器，详见 %s\n' "$startup_log" >&2
            return 1
        fi
        for ((attempt=0; attempt<30; ++attempt)); do
            if controller_processes ready; then
                if (( ready )); then break; fi
                ready=1
            else
                ready=0
            fi
            sleep 1
        done
        if (( attempt == 30 )); then
            printf '未确认虚拟控制器启动成功，详见 %s\n' "$startup_log" >&2
            return 1
        fi
        printf '虚拟控制器已启动。\n'
        if [[ -f $target_zip && ! -L $target_zip && ! $target_zip -ef $zip ]]; then
            if ! rm -f -- "$target_zip"; then
                printf '未能删除项目目录内的压缩包：%s\n' "$target_zip" >&2
                return 1
            fi
            printf '已删除项目目录内的压缩包：%s\n' "$target_zip"
        fi
    )
    stop_target_if_needed() {
        if ! target_running; then return 0; fi
        if [[ $action == build_only && $stop_before_build == 0 ]]; then
            printf '目标控制器正在运行，请先停止后再编译：%s\n' "$dest" >&2
            return 1
        fi
        if ! controller_processes exclusive || [[ ! -x $dest/bin/control ]]; then
            printf '无法确认实时服务只属于目标目录，未执行停止：%s\n' "$dest" >&2
            return 1
        fi

        if [[ -f $dest/bin/SPACE ]] && grep -qx 'USER VIRTUAL_SERVO' "$dest/bin/SPACE"; then
            old_virtual=1
        fi
        printf '停止目标目录的旧控制器：%s\n' "$dest"
        if (( stop_before_build || ssh_start )); then
            (cd -- "$dest/bin" && run_ssh_controller_sudo ./control stop) || stop_status=$?
            if (( stop_status == 73 )) && [[ ${QJ_CONTROLLER_SUDO_STDIN:-0} != 1 ]]; then return 73; fi
        else
            (cd -- "$dest/bin" && run_controller_sudo ./control stop) || stop_status=$?
        fi
        if (( stop_status != 0 && ! old_virtual )); then
            printf '停止失败，未开始编译，原工程保留。\n' >&2
            return 1
        fi
        for ((attempt=0; attempt<15; ++attempt)); do
            if ! target_running; then break; fi
            sleep 1
        done
        if target_running; then
            printf '停止命令已返回，但旧控制器仍在运行。\n' >&2
            return 1
        fi
        if (( stop_status != 0 )); then
            if controller_processes any_controller; then
                printf '停止命令报错，且仍有实时服务或控制器任务在运行。未开始编译。\n' >&2
                return 1
            fi
            printf '停止命令有报错，但已确认旧虚拟控制器和实时服务均已退出。\n'
        fi
        return 0
    }

    if (( ssh_start )); then
        exec 7> "$root/.qj-virtual-start.lock"
        if ! flock -n 7; then
            printf '已有虚拟控制器启动操作，请等待完成。\n' >&2
            return 1
        fi
        if [[ ${QJ_CONTROLLER_SUDO_STDIN:-0} == 1 ]]; then
            IFS= read -r ssh_password || return 1
        fi
        stop_target_if_needed || return $?
        local startup_status=0
        if [[ -n ${QJ_CONTROLLER_CONSOLE:-} ]]; then
            # 校验 sudo 失败重试时，前一次的 FIFO 已由记录进程读完。
            rm -f -- "$QJ_CONTROLLER_CONSOLE/output" "$QJ_CONTROLLER_CONSOLE/pid" "$QJ_CONTROLLER_CONSOLE/console.log"
        fi
        start_virtual_controller || startup_status=$?
        if (( startup_status != 0 )) && [[ -n ${QJ_CONTROLLER_CONSOLE:-} && -f $QJ_CONTROLLER_CONSOLE/console.log ]]; then
            tail -n 120 -- "$QJ_CONTROLLER_CONSOLE/console.log"
        fi
        if [[ -f ${QJ_CONTROLLER_STARTUP_LOG:-} ]]; then
            if [[ -n $receipt ]]; then
                tail -n 120 -- "$QJ_CONTROLLER_STARTUP_LOG" | tee -a "$log"
            else
                tail -n 120 -- "$QJ_CONTROLLER_STARTUP_LOG"
            fi
        fi
        return "$startup_status"
    fi

    if [[ $action == start ]]; then
        stop_target_if_needed || return $?
        start_virtual_controller
        return $?
    fi

    log=$(mktemp "$project/build-${driver}.log.XXXXXX")
    stage=$(mktemp -d "$project/.cx-v3.0-build.XXXXXX")
    cleanup() {
        local status=$?
        trap - EXIT
        if [[ -z $stage || $stage != "$project"/.cx-v3.0-build.* || -L $stage ]]; then
            return "$status"
        fi
        if (( ! committed )) && [[ -d $stage/replaced ]]; then
            if [[ -n $tree && ! -e $tree && -d $dest ]]; then
                mv -T -- "$dest" "$tree" || { printf '恢复失败，旧文件在 %s/replaced\n' "$stage" >&2; return 1; }
            fi
            if [[ ! -e $stage/cx-v3.0.zip && -f $target_zip ]]; then
                mv -T -- "$target_zip" "$stage/cx-v3.0.zip" || { printf '恢复失败，旧文件在 %s/replaced\n' "$stage" >&2; return 1; }
            fi
            if [[ -d $stage/replaced/cx-v3.0 ]]; then
                mv -T -- "$stage/replaced/cx-v3.0" "$dest" || { printf '恢复失败，旧文件在 %s/replaced\n' "$stage" >&2; return 1; }
            fi
            if [[ -f $stage/replaced/cx-v3.0.zip ]]; then
                mv -T -- "$stage/replaced/cx-v3.0.zip" "$target_zip" || { printf '恢复失败，旧文件在 %s/replaced\n' "$stage" >&2; return 1; }
            fi
        fi
        rm -rf -- "$stage" || { printf '未能清理临时目录：%s\n' "$stage" >&2; return 1; }
        return "$status"
    }
    trap cleanup EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM

    unpacked=$stage/unpacked
    mkdir -- "$unpacked"
    printf '压缩包：%s\n目标目录：%s\n编译目标：%s\n日志：%s\n' "$zip" "$project" "$driver" "$log"
    cp -- "$zip" "$stage/cx-v3.0.zip"
    if ! unzip -q "$stage/cx-v3.0.zip" -d "$unpacked" 2>&1 | tee -a "$log"; then
        printf '解压失败。原压缩包和工程未替换。\n' >&2
        return 1
    fi
    if [[ -f $unpacked/cx-v3.0/src/Makefile ]]; then
        tree=$unpacked/cx-v3.0
    else
        tree=$unpacked
    fi
    if [[ ! -f $tree/src/Makefile || ! -f $tree/bin/control || ! -d $tree/lib || ! -d $tree/rtlib ]]; then
        printf '压缩包结构不正确：需要 src/Makefile、bin/control、lib 和 rtlib。\n' >&2
        return 1
    fi

    sync_makefile_driver "$tree/src/Makefile" "$driver" | tee -a "$log" || return 1
    stop_target_if_needed || return $?
    if ! (cd -- "$tree/src" && make modules SPACE=USER BUILD_KERNEL=xeno-x86 "DRIVER=$driver") 2>&1 | tee -a "$log"; then
        printf '编译失败。原工程未替换，详见 %s\n' "$log" >&2
        return 1
    fi
    if [[ ! -x $tree/bin/user_task || ! -f $tree/rtlib/$module.so || ! -f $tree/bin/SPACE ]] ||
        ! grep -qx "USER $driver" "$tree/bin/SPACE"; then
        printf '编译产物或 bin/SPACE 与目标不匹配。原工程未替换。\n' >&2
        return 1
    fi
    chmod u+x -- "$tree/bin/control"
    if [[ $action == build_only && -f $tree/bin/system_update ]]; then
        chmod u+x -- "$tree/bin/system_update"
    fi
    if target_running; then
        printf '目标控制器在编译期间启动了。原工程未替换。\n' >&2
        return 1
    fi

    if [[ -n $receipt ]]; then
        local build_identity
        rm -f -- "$tree/src/control_update.tar.gz"
        build_identity=$(stat -c '%d:%i' -- "$tree")
        printf '%s\n%s\n' "$build_identity" "${log##*/}" > "$receipt"
    fi
    mkdir -- "$stage/replaced"
    if [[ -d $dest ]]; then mv -T -- "$dest" "$stage/replaced/cx-v3.0"; fi
    if [[ -f $target_zip ]]; then mv -T -- "$target_zip" "$stage/replaced/cx-v3.0.zip"; fi
    if [[ -z $receipt ]]; then mv -T -- "$stage/cx-v3.0.zip" "$target_zip"; fi
    mv -T -- "$tree" "$dest"
    committed=1
    cleanup
    stage=''
    printf '编译成功：%s\n' "$dest"
    if [[ -n $receipt ]]; then
        if find "$project" -maxdepth 1 -type f ! -name "${log##*/}" \
            \( -name 'build-ETHERCAT_X86.log.*' -o -name 'build-VIRTUAL_SERVO.log.*' \
               -o -name 'package-ETHERCAT_X86.log.*' -o -name 'package-VIRTUAL_SERVO.log.*' \
               -o -name 'start-VIRTUAL_SERVO.log.*' \) -delete; then
            printf '项目仅保留本次编译日志：%s\n' "$log"
        else
            printf '部分历史编译日志未能清理。\n' >&2
        fi
    else
        printf '压缩包已更新：%s\n' "$target_zip"
    fi

    if [[ $action == build_only ]]; then
        return 0
    elif [[ $driver == ETHERCAT_X86 ]]; then
        answer=''
        read -r -p '是否打包 EtherCAT 版本？[y/N] ' answer || true
        case "$answer" in
            y|Y|yes|YES|是)
                local package=$dest/src/control_update.tar.gz output temp stamp
                package_controller || return 1
                temp=$(mktemp "$desktop/.qj-control-update.XXXXXX")
                output=$desktop/control_update.tar.gz
                if [[ -e $output || -L $output ]]; then
                    stamp=$(date +%Y%m%d-%H%M%S)
                    output=$desktop/control_update-$number-$stamp-${temp##*.}.tar.gz
                fi
                if ! cp -- "$package" "$temp" || ! mv -nT -- "$temp" "$output" || [[ -e $temp ]]; then
                    rm -f -- "$temp"
                    printf '未能将包复制到桌面，原包保留在 %s\n' "$package" >&2
                    return 1
                fi
                printf '打包成功：%s\n' "$output"
                ;;
        esac
    elif [[ $action == build_and_start ]]; then
        start_virtual_controller
    else
        answer=''
        read -r -p '是否启动虚拟控制器？[y/N] ' answer || true
        case "$answer" in
            y|Y|yes|YES|是)
                start_virtual_controller
                ;;
        esac
    fi
)

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
    if [[ ( ${1:-} == --build-only || ${1:-} == --stop-and-build || ${1:-} == --package-built || ${1:-} == --package-installed ||
        ${1:-} == --start-ssh ) && $# -eq 4 ]]; then
        build_controller "$2" "$1" "$3" "$4"
    else
        build_controller ETHERCAT_X86 "$@"
    fi
fi
