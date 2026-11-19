#!/bin/bash
set -euo pipefail

project=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
package=0
source_arg=
for arg in "$@"; do
    case "$arg" in
        --package) package=1 ;;
        -*) echo "未知参数：$arg"; exit 2 ;;
        *)
            [ -z "$source_arg" ] || { echo '只接受一个工程目录参数'; exit 2; }
            source_arg=$arg
            ;;
    esac
done
source_arg=${source_arg:-"$project/01代码/cx-v3.0"}
case "$source_arg" in [a-zA-Z]:*) source_arg=$(wslpath -u "$source_arg");; esac
[ -d "$source_arg" ] || { echo '工程目录不存在；请传入含src和lib的控制器工程目录'; exit 2; }
source_dir=$(cd "$source_arg" && pwd)
[ -f "$source_dir/src/Makefile" ] && [ -d "$source_dir/lib" ] || {
    echo '工程需要src/Makefile和lib目录'; exit 2;
}
grep -Eq '^[[:space:]]*BUILD_KERNEL[[:space:]]*[:?]?=[[:space:]]*xeno-x86([[:space:]#]|$)' "$source_dir/src/Makefile" || {
    echo '此环境仅支持BUILD_KERNEL=xeno-x86的32位Xenomai控制器工程'; exit 2;
}

# 编译目标决定产物集合：实体轴编 atemsys.ko/ethercat_x86.so，虚拟轴只有 virtual_servo.so。
driver=$(sed -n 's/^[[:space:]]*DRIVER[[:space:]]*=[[:space:]]*\([A-Za-z0-9_]*\).*/\1/p' "$source_dir/src/Makefile" | head -n 1)
[ -n "$driver" ] || { echo '无法从src/Makefile读取DRIVER'; exit 2; }
case "$driver" in
    ETHERCAT_X86)  driver_artifacts='rtlib/atemsys.ko rtlib/ethercat_x86.so' ;;
    VIRTUAL_SERVO) driver_artifacts='rtlib/virtual_servo.so' ;;
    *)             driver_artifacts='' ;;
esac
artifacts="bin/user_task rtlib/motmod.so lib/libbasic_compiler.so $driver_artifacts"

rootfs=/opt/qjar-build/ubuntu12-i686
[ "$(id -u)" = 0 ] || { echo '请用wsl -u root运行此构建脚本'; exit 1; }
[ -x "$rootfs/usr/bin/gcc-4.6" ] || { echo '缺少隔离的32位Xenomai编译环境'; exit 1; }
work=$(mktemp -d "$rootfs/work/controller.XXXXXX")
guest_work="/work/$(basename "$work")"
out="$source_dir/build-wsl/$(basename "$work")"
mkdir -p "$out" "$work/bin" "$work/rtlib"
printf 'Build output: %s\n' "$out"
printf 'Driver: %s\n' "$driver"

# 每次使用当前源码和原依赖库，避免沿用共享结构体变化前的对象或混用构建产物。
tar -C "$source_dir" --exclude=.svn --exclude=.tmp_versions --exclude='*.o' \
    --exclude='*.ko' --exclude='*.cmd' --exclude='*.mod' --exclude='*.mod.c' --exclude=Module.symvers \
    --exclude=modules.order -cf - src lib | tar -C "$work" -xf -
# make tar 要求工程根目录的 README；不随源码复制会得到一个缺文件的包。
if [ -f "$source_dir/README" ]; then
    cp -p "$source_dir/README" "$work/README"
elif [ "$package" = 1 ]; then
    echo '打包需要工程根目录的README（make tar 的输入）'; exit 2
fi
if [ -f "$source_dir/tests/check_tool_compiler.c" ]; then
    cp "$source_dir/tests/check_tool_compiler.c" "$work/"
fi
(cd "$source_dir" && find src -type f ! -name '*.mod.c' \( -name '*.c' -o -name '*.h' -o -name 'Makefile*' \) \
    -print0 | sort -z | xargs -0 sha256sum) > "$out/source.sha256"
cat > "$work/compiler-check.mk" <<'MAKE'
compiler-check:
	$(GCC) -DULAPI -D$(KBUILD_ON_KERNEL) -D$(DRIVER) $(GLOBALOPTIONS) $(ALL_INCLUDE_PATH) ../check_tool_compiler.c -L$(LIB_PATH) -Wl,--export-dynamic -lbasic_compiler -lfile_oper -o ../check_tool_compiler
MAKE

set +e
set -o pipefail
# 编译过程直接输出（同时留下 build.log）：调用方和本工具都能看到实时进度。
setarch linux32 chroot "$rootfs" /usr/bin/env -i HOME=/root \
    PATH=/usr/bin:/bin:/usr/sbin:/sbin LC_ALL=C /bin/bash -c '
    set -eu
    printf "编译：make modules\n"
    cd "$1/src"
    make modules KERNEL_BUILD_PATH=/lib/modules/3.14.17-xenomai/build \
        RT_KERNEL_MODULE_SYMVERS=/lib/modules/3.14.17-xenomai/build/Module.symvers
    if [ -f ../check_tool_compiler.c ]; then
        make -s -f Makefile -f ../compiler-check.mk compiler-check
    fi
    cd ..
    if [ -x ./check_tool_compiler ]; then
        LD_LIBRARY_PATH="$PWD/lib:/usr/xenomai/lib" ./check_tool_compiler
    else
        printf "未提供check_tool_compiler.c，仅执行完整构建\n"
    fi
    printf "检查产物：%s\n" "$2"
    for artifact in $2; do
        [ -f "$artifact" ] || { printf "缺少构建产物：%s\n" "$artifact"; exit 1; }
    done
    file $2
    if [ "$3" = 1 ]; then
        printf "打包：make tar（控制更新包）\n"
        ( cd src && make tar )
        tar -tzf src/control_update.tar.gz > /dev/null
        printf "打包完成：control_update.tar.gz %s\n" "$(du -h src/control_update.tar.gz | cut -f1)"
    fi
' -- "$guest_work" "$artifacts" "$package" 2>&1 | tee "$out/build.log"
result=$?
set -e
printf '%s\n' "$result" > "$out/build.exit"
[ "$result" = 0 ] || exit "$result"
(cd "$work" && sha256sum -c "$out/source.sha256") > "$out/source-check.log"
cp -a "$work/bin" "$work/rtlib" "$work/lib" "$out/"
mkdir -p "$out/src"
cp -p "$work/src/Makefile" "$out/src/"
if [ -f "$work/src/version_info" ]; then
    cp -p "$work/src/version_info" "$out/src/"
else
    printf '未生成 src/version_info，跳过复制\n'
fi
package_json=null
if [ "$package" = 1 ] && [ -f "$work/src/control_update.tar.gz" ]; then
    cp -p "$work/src/control_update.tar.gz" "$out/"
    package_bytes=$(stat -c %s "$out/control_update.tar.gz")
    package_sha=$(sha256sum "$out/control_update.tar.gz" | cut -d' ' -f1)
    package_json="{\"file\":\"control_update.tar.gz\",\"bytes\":$package_bytes,\"sha256\":\"$package_sha\"}"
fi
version=$(sed -n '2p' "$work/src/version_info" 2>/dev/null | tr -d '"' || true)
printf '%s\n' "$guest_work" > "$out/guest-workdir.txt"
cat > "$out/result.json" <<JSON
{
  "date": "$(date +%F)",
  "distribution": "${WSL_DISTRO_NAME:-unknown}",
  "rootfs": "$rootfs",
  "workdir": "$guest_work",
  "driver": "$driver",
  "version": "$version",
  "build_exit": 0,
  "package": $package_json,
  "target_kernel": "3.14.17-xenomai"
}
JSON

if [[ "$work" != "$rootfs"/work/controller.?????? || "$(realpath -e -- "$work")" != "$work" ]]; then
    printf '无法确认本次 WSL 工作目录，已跳过清理：%s\n' "$work" >&2
    exit 1
fi
if ! rm -rf --one-file-system -- "$work"; then
    printf 'Windows 产物已导出，但本次 WSL 工作目录清理失败：%s\n' "$work" >&2
    exit 1
fi
printf '已清理本次 WSL 工作目录：%s\n' "$work"
printf 'WSL build passed: %s\n' "$out"
