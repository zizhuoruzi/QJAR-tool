#!/bin/bash
set -euo pipefail

# 环境包可放在任意目录；第二个参数仅用于在独立目录验证恢复。
archive_dir=${1:-"$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"}
case "$archive_dir" in [a-zA-Z]:*) archive_dir=$(wslpath -u "$archive_dir");; esac
archive_dir=$(cd -- "$archive_dir" && pwd)
rootfs=${2:-/opt/qjar-build/ubuntu12-i686}
rootfs=$(realpath -m -- "$rootfs")
case "$rootfs" in /opt/*/*) ;; *) echo '恢复目标须为 /opt 下的独立子目录'; exit 2;; esac
[ "$(id -u)" = 0 ] || { echo '请用 wsl -u root 执行恢复'; exit 2; }
[ ! -e "$rootfs" ] && [ ! -L "$rootfs" ] || {
    echo "已有环境，拒绝覆盖：$rootfs；请先检查或选独立恢复目录"; exit 2;
}

for command in sha256sum tar setarch chroot file realpath; do
    command -v "$command" >/dev/null || { echo "WSL 发行版缺少命令：$command"; exit 2; }
done
[ -f "$archive_dir/bundle.sha256" ] || { echo '缺少 bundle.sha256，请重新下载完整环境包'; exit 2; }
[ -f "$archive_dir/archive.sha256" ] || { echo '缺少 archive.sha256'; exit 2; }
printf '校验环境包：%s\n' "$archive_dir"
(cd -- "$archive_dir" && sha256sum -c bundle.sha256 && sha256sum -c archive.sha256)

parent=$(dirname -- "$rootfs")
mkdir -p -- "$parent"
stage=$(mktemp -d "$parent/.qjar-restore.XXXXXX")
printf '独立恢复目录：%s；成功后移至：%s\n' "$stage" "$rootfs"
trap 'echo "恢复未完成，临时目录保留用于诊断：$stage" >&2' ERR
for archive in ubuntu12-i686-toolchain.tar.gz extra-libs.tar.gz system-headers.tar.gz; do
    printf '解压：%s\n' "$archive"
    tar -xzf "$archive_dir/$archive" -C "$stage"
done
[ -f "$stage/lib/i386-linux-gnu/libtinfo.so.5" ] || { echo '环境包缺少 libtinfo.so.5'; exit 1; }
mkdir -p "$stage"/{dev,proc,tmp,root,work,etc,usr/lib/i386-linux-gnu}
ln -sfn /lib/i386-linux-gnu/libtinfo.so.5 "$stage/usr/lib/i386-linux-gnu/libtinfo.so"
chmod 1777 "$stage/tmp"
mknod -m 666 "$stage/dev/null" c 1 3
mknod -m 666 "$stage/dev/zero" c 1 5
mknod -m 666 "$stage/dev/random" c 1 8
mknod -m 666 "$stage/dev/urandom" c 1 9
printf 'root:x:0:0:root:/root:/bin/bash\n' > "$stage/etc/passwd"
printf 'root:x:0:\n' > "$stage/etc/group"
printf 'passwd: files\ngroup: files\n' > "$stage/etc/nsswitch.conf"
cp "$archive_dir/manifest.json" "$stage/environment.json"

printf '检查 32 位 GCC/G++、make 与 Xenomai：\n'
setarch linux32 chroot "$stage" /usr/bin/env -i HOME=/root \
    PATH=/usr/bin:/bin:/usr/sbin:/sbin LC_ALL=C /bin/bash -c '
    set -eu
    uname -m
    gcc --version | head -n 1
    make --version | head -n 1
    /usr/xenomai/bin/xeno-config --version
    test -f /lib/modules/3.14.17-xenomai/build/Module.symvers
    printf "#include <iostream>\nint main(){std::cout << 42 << std::endl;}\n" > /tmp/qjar-environment-probe.cpp
    g++ /tmp/qjar-environment-probe.cpp -o /tmp/qjar-environment-probe
    test "$(/tmp/qjar-environment-probe)" = 42
    printf "32 位 C++ 程序执行通过\n"
'
file "$stage/tmp/qjar-environment-probe" | grep -q 'ELF 32-bit'
# 两个路径均为上文核对过的独立目录，已有环境绝不被此次恢复替换。
[ ! -e "$rootfs" ] && [ ! -L "$rootfs" ] || { echo "恢复期间目标已被创建：$rootfs"; exit 2; }
mv -T -- "$stage" "$rootfs"
trap - ERR
printf '恢复完成：%s\n' "$rootfs"
printf '下一步：install -m 755 %q /opt/qjar-build/build-controller.sh\n' "$archive_dir/build-controller.sh"
