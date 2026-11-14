#!/usr/bin/env python3
"""Virtual-mic feeder: silence keep-alive that never drops a clip.

The pipe-source can die when its FIFO goes idle between clips (seen on Debian
12: the module drops, the app's stream disappears and every later take looks
like a recognition miss). Writing silence keeps the source alive, but a naive
non-blocking writer fills the pipe buffer and then drops the clip bytes with
EAGAIN - which is exactly the "no wake" symptom. So silence is best-effort and
clip bytes are retried until the reader makes room.
"""
import errno
import os
import time
import wave

FIFO = "/tmp/vmic.fifo"
PLAY = "/tmp/vmic.play"
DONE = "/tmp/vmic.done"
SILENCE = b"\x00" * 3200  # 100 ms, 16 kHz mono s16le

fd = os.open(FIFO, os.O_RDWR | os.O_NONBLOCK)
f = os.fdopen(fd, "wb", 0)
open(DONE, "w").write("READY")


def write_all(data: bytes, deadline_s: float) -> bool:
    """Write every byte or give up; silence may be dropped, clips may not."""
    end = time.time() + deadline_s
    view = memoryview(data)
    while view:
        try:
            written = f.write(view)
            view = view[written:]
        except BlockingIOError:
            if time.time() > end:
                return False
            time.sleep(0.01)
        except OSError as exc:
            if exc.errno == errno.EPIPE:
                return False
            raise
    return True


while True:
    if os.path.exists(PLAY):
        try:
            path = open(PLAY).read().strip()
            os.remove(PLAY)
        except Exception:
            time.sleep(0.1)
            continue
        try:
            with wave.open(path, "rb") as w:
                frames = w.readframes(w.getnframes())
            ok = True
            for i in range(0, len(frames), 3200):
                ok = write_all(frames[i:i + 3200], 2.0) and ok
            write_all(b"\x00" * 16000, 1.0)
            open(DONE, "w").write("OK" if ok else "TRUNCATED")
        except Exception as exc:  # noqa: BLE001 - report and keep serving
            open(DONE, "w").write("ERR:" + str(exc)[:120])
    else:
        write_all(SILENCE, 0.05)
        time.sleep(0.1)
