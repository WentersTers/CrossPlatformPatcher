"""Deterministic zip builder for release bundles.

Every member gets a fixed timestamp (SOURCE_DATE_EPOCH, default 1757980800 =
2026-09-16Z), sorted entry order, and fixed unix permissions, so the same
inputs always produce byte-identical zips. The pipeline proves it by
building each bundle twice and comparing hashes.

Usage:
  python3 bundle-zip.py <output.zip> <epoch> <file> [<file> ...]
Filenames inside the zip are basenames. Fails loudly on missing inputs.
"""
import os
import sys
import time
import zipfile


def build(output, epoch, files):
    stamp = time.gmtime(epoch)[:6]
    infos = []
    for path in files:
        if not os.path.isfile(path):
            raise SystemExit("bundle member missing: " + path)
        with open(path, "rb") as fh:
            data = fh.read()
        info = zipfile.ZipInfo(os.path.basename(path), date_time=stamp)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.create_system = 3  # unix, so external_attr permissions stick
        info.external_attr = (0o644 << 16)
        infos.append((info, data))
    infos.sort(key=lambda pair: pair[0].filename)
    tmp = output + ".tmp"
    with zipfile.ZipFile(tmp, "w") as zf:
        for info, data in infos:
            zf.writestr(info, data)
    os.replace(tmp, output)


if __name__ == "__main__":
    if len(sys.argv) < 4:
        raise SystemExit("usage: bundle-zip.py <output.zip> <epoch> <file> ...")
    build(sys.argv[1], int(sys.argv[2]), sys.argv[3:])
    print("bundled: " + sys.argv[1])
