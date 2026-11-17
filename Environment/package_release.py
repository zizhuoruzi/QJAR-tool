"""Build the encrypted release from an explicit environment-only allowlist."""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ARCHIVES = (
    "ubuntu12-i686-toolchain.tar.gz",
    "extra-libs.tar.gz",
    "system-headers.tar.gz",
)
SOURCE_FILES = (*ARCHIVES, "archive.sha256", "manifest.json")
SCRIPT_FILES = ("prepare-rootfs.sh", "README.md")


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--build-script", type=Path, required=True)
    parser.add_argument("--seven-zip", type=Path, default=Path(r"C:\Program Files\7-Zip\7z.exe"))
    args = parser.parse_args()
    password = os.environ.get("QJ_WSL_ENV_PASSWORD")
    if not password:
        raise SystemExit("Set QJ_WSL_ENV_PASSWORD in this process; do not save the password in source files.")
    source = args.source.resolve(strict=True)
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    archive = out / "qj-wsl-env.7z"
    if archive.exists():
        raise SystemExit(f"Refusing to replace an existing archive: {archive}")
    stage = Path(tempfile.mkdtemp(prefix="stage-", dir=out))
    here = Path(__file__).resolve().parent
    for name in SOURCE_FILES:
        shutil.copy2(source / name, stage / name)
    for name in SCRIPT_FILES:
        shutil.copy2(here / name, stage / name)
    shutil.copy2(args.build_script.resolve(strict=True), stage / "build-controller.sh")

    expected = {}
    for line in (stage / "archive.sha256").read_text(encoding="utf-8-sig").splitlines():
        digest, name = line.split(maxsplit=1)
        expected[name.lstrip(" *")] = digest.lower()
    if set(expected) != set(ARCHIVES):
        raise SystemExit("archive.sha256 must describe only the three environment archives.")
    for name in ARCHIVES:
        if sha256(stage / name) != expected[name]:
            raise SystemExit(f"Environment source checksum mismatch: {name}")

    names = sorted(path.name for path in stage.iterdir())
    allowed = set(SOURCE_FILES) | set(SCRIPT_FILES) | {"build-controller.sh"}
    if set(names) != allowed:
        raise SystemExit("Unexpected files in the release staging directory.")
    manifest = "".join(f"{sha256(stage / name)}  {name}\n" for name in names)
    (stage / "bundle.sha256").write_text(manifest, encoding="utf-8", newline="\n")
    print(f"Validated environment-only stage: {stage}", flush=True)
    print("Files: " + ", ".join([*names, "bundle.sha256"]), flush=True)
    # Inherit stdio: the compressor reports progress; its password is never echoed here.
    subprocess.run([
        str(args.seven_zip), "a", "-t7z", "-mx=5", "-mhe=on", "-y", "-bsp1",
        "-p" + password, str(archive), ".\\*",
    ], cwd=stage, check=True)
    subprocess.run([str(args.seven_zip), "t", "-p" + password, str(archive)], check=True)
    checksum = sha256(archive)
    (out / "SHA256SUMS.txt").write_text(f"{checksum}  {archive.name}\n", encoding="utf-8", newline="\n")
    (out / "stage-path.txt").write_text(str(stage), encoding="utf-8")
    print(f"Release asset: {archive}; bytes={archive.stat().st_size}; SHA256={checksum}")


if __name__ == "__main__":
    main()
