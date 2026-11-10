"""Build the portable Linux x64 archive from a clean publish directory."""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
from xml.etree import ElementTree


ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "src/LocalAccountManager.Linux/LocalAccountManager.Linux.csproj"
RUNTIME = "linux-x64"
EXECUTABLE = "RobloxAccountManager"
PLAYWRIGHT_NODE = ".playwright/node/linux-x64/node"
LAUNCHER = """#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
export RAM_PORTABLE_DIR="$root"
exec "$root/app/RobloxAccountManager" "$@"
"""
NUGET_NOTICES = {
    "Microsoft.NETCore.App.Runtime.linux-x64": ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"),
    "QRCoder": ("LICENSE.txt",),
    "SkiaSharp": ("LICENSE.txt",),
    "SkiaSharp.NativeAssets.Linux": ("LICENSE.txt", "THIRD-PARTY-NOTICES.txt"),
    "HarfBuzzSharp": ("LICENSE.txt",),
    "HarfBuzzSharp.NativeAssets.Linux": ("LICENSE.txt", "THIRD-PARTY-NOTICES.txt"),
}
SOURCE_NOTICES = ("Avalonia", "Avalonia-DataGrid", "MicroCom", "Tmds.DBus")


def add_file(archive, source, destination, mode=0o644):
    info = archive.gettarinfo(source, destination)
    if not info.isfile():
        raise RuntimeError(f"Build input must be a regular file: {source}")
    info.mode = mode
    info.uid = info.gid = 0
    info.uname = info.gname = "root"
    with source.open("rb") as contents:
        archive.addfile(info, contents)


def add_runtime_notices(archive, publish, prefix, packages):
    deps = json.loads((publish / f"{EXECUTABLE}.deps.json").read_text(encoding="utf-8"))
    versions = dict(name.removeprefix("runtimepack.").split("/", 1) for name in deps["libraries"])
    for name, notices in NUGET_NOTICES.items():
        for filename in notices:
            source = packages / name.lower() / versions[name].lower() / filename
            add_file(archive, source, f"{prefix}/Docs/ThirdPartyNotices/{name}-{filename}")
    for name in SOURCE_NOTICES:
        filename = f"{name}-LICENSE.txt"
        add_file(archive, ROOT / "third-party" / filename, f"{prefix}/Docs/ThirdPartyNotices/{filename}")


def package(publish, work, archive_path, packages):
    for executable in (EXECUTABLE, PLAYWRIGHT_NODE):
        with (publish / executable).open("rb") as binary:
            if binary.read(5) != b"\x7fELF\x02":
                raise RuntimeError(f"Expected a Linux 64-bit ELF file: {executable}")
    launcher = work / EXECUTABLE
    launcher.write_text(LAUNCHER, encoding="ascii", newline="\n")
    prefix = EXECUTABLE
    with tarfile.open(archive_path, "w:gz", compresslevel=6) as archive:
        add_file(archive, launcher, f"{prefix}/{EXECUTABLE}", 0o755)
        add_file(archive, ROOT / "scripts/Linux-Readme.txt", f"{prefix}/README.txt")
        for name in ("LICENSE", "NOTICE.md"):
            add_file(archive, ROOT / name, f"{prefix}/Docs/{name}")
        add_runtime_notices(archive, publish, prefix, packages)
        for source in sorted(publish.rglob("*")):
            if source.is_dir():
                continue
            relative = source.relative_to(publish).as_posix()
            if source.suffix.lower() in {".pdb", ".ps1"}:
                continue
            if any(part.lower() in {"data", "browserdata", ".updates"} for part in source.relative_to(publish).parts):
                raise RuntimeError("Private state was found in the publish directory.")
            executable = relative in {EXECUTABLE, PLAYWRIGHT_NODE, "createdump"}
            add_file(archive, source, f"{prefix}/app/{relative}", 0o755 if executable else 0o644)


def main():
    version = ElementTree.parse(PROJECT).findtext("./PropertyGroup/Version")
    bundled_dotnet = ROOT / ".tools/dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")
    dotnet = str(bundled_dotnet) if bundled_dotnet.is_file() else "dotnet"
    env = os.environ.copy()
    env.setdefault("NUGET_PACKAGES", str(ROOT / ".tools/packages"))
    env.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    packages = Path(env["NUGET_PACKAGES"]).resolve()
    env["NUGET_PACKAGES"] = str(packages)
    release = ROOT / "releases"
    release.mkdir(exist_ok=True)
    name = f"{EXECUTABLE}-{version}-{RUNTIME}.tar.gz"
    with tempfile.TemporaryDirectory(prefix="ram-linux-", dir=release) as scratch:
        work = Path(scratch)
        publish = work / "publish"
        subprocess.run([dotnet, "restore", str(PROJECT), f"-p:RuntimeIdentifier={RUNTIME}",
                        "-p:SelfContained=true", "--locked-mode", "--configfile", str(ROOT / "NuGet.Config")],
                       check=True, cwd=ROOT, env=env)
        subprocess.run([dotnet, "publish", str(PROJECT), "-c", "Release", "-r", RUNTIME, "--self-contained",
                        "--no-restore", "-o", str(publish)], check=True, cwd=ROOT, env=env)
        archive = work / name
        package(publish, work, archive, packages)
        with archive.open("rb") as data:
            digest = hashlib.file_digest(data, "sha256").hexdigest()
        checksum = work / f"{name}.sha256"
        checksum.write_text(f"{digest}  {name}\n", encoding="ascii", newline="\n")
        archive.replace(release / name)
        checksum.replace(release / checksum.name)
    print(release / name)
    print(release / checksum.name)


if __name__ == "__main__":
    main()
