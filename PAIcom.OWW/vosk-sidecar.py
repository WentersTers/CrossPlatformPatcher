#!/usr/bin/env python3
"""Vosk sidecar: native x64 speech recognition over loopback HTTP.

Lets the app process use 64-bit libvosk without loading it in-process.
Stdlib only. Fail-closed by design: any error returns null/empty, never raises
to the caller; the app treats that as Vosk-unavailable (existing path).
Cross-platform: Linux loads libvosk.so, Windows loads libvosk.dll
(same C API, same protocol, same twin expectations).

Endpoints (all JSON):
  GET  /health -> {"ok": true, "model": bool}
  POST /init   {"samplerate": 16000, "grammar": [...]} -> {"ok": true}
  POST /accept {"pcm_b64": "<16-bit mono PCM>"} -> {"ok": true}
  GET  /partial -> {"partial": "..."}
  GET  /final   -> {"text": "..."} (resets utterance)

Usage: vosk-sidecar.py <model-dir> <libvosk> [port]
  (libvosk defaults: libvosk.dll beside the script on Windows,
  libvosk.so beside the script or /usr/lib on Linux)
Env overrides: PAICOM_VOSK_MODEL_PATH, PAICOM_VOSK_LIB, PAICOM_VOSK_SIDECAR_PORT.
"""
import base64
import ctypes
import json
import os
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(os.environ.get("PAICOM_VOSK_SIDECAR_PORT", "18080"))


class VoskNative:
    def __init__(self, libpath, modeldir):
        self.lib = ctypes.CDLL(libpath)
        self.lib.vosk_set_log_level(-1)
        self.lib.vosk_model_new.argtypes = [ctypes.c_char_p]
        self.lib.vosk_model_new.restype = ctypes.c_void_p
        self.lib.vosk_recognizer_new.argtypes = [ctypes.c_void_p, ctypes.c_float]
        self.lib.vosk_recognizer_new.restype = ctypes.c_void_p
        self.lib.vosk_recognizer_new_grm.argtypes = [ctypes.c_void_p, ctypes.c_float, ctypes.c_char_p]
        self.lib.vosk_recognizer_new_grm.restype = ctypes.c_void_p
        self.lib.vosk_recognizer_accept_waveform.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]
        self.lib.vosk_recognizer_accept_waveform.restype = ctypes.c_int
        self.lib.vosk_recognizer_result.argtypes = [ctypes.c_void_p]
        self.lib.vosk_recognizer_result.restype = ctypes.c_char_p
        self.lib.vosk_recognizer_final_result.argtypes = [ctypes.c_void_p]
        self.lib.vosk_recognizer_final_result.restype = ctypes.c_char_p
        self.lib.vosk_recognizer_partial_result.argtypes = [ctypes.c_void_p]
        self.lib.vosk_recognizer_partial_result.restype = ctypes.c_char_p
        self.lib.vosk_recognizer_free.argtypes = [ctypes.c_void_p]
        self.lib.vosk_model_free.argtypes = [ctypes.c_void_p]
        self.model = self.lib.vosk_model_new(modeldir.encode())
        if not self.model:
            raise RuntimeError("vosk_model_new failed for " + modeldir)
        self.rec = None
        self.lock = threading.Lock()
        self._rate = 16000.0
        self._grammar = None

    def new_utterance(self, rate, grammar):
        with self.lock:
            self._rate = float(rate or 16000)
            self._grammar = grammar
            if self.rec:
                self.lib.vosk_recognizer_free(self.rec)
                self.rec = None
            if grammar:
                self.rec = self.lib.vosk_recognizer_new_grm(
                    self.model, self._rate, json.dumps(grammar).encode())
            else:
                self.rec = self.lib.vosk_recognizer_new(self.model, self._rate)
            if not self.rec:
                raise RuntimeError("recognizer_new failed")
            return True

    def accept(self, pcm):
        with self.lock:
            if not self.rec:
                # Transparent renew: utterances end at /final; late audio
                # starts a fresh one instead of dropping silently.
                try:
                    if self._grammar:
                        self.rec = self.lib.vosk_recognizer_new_grm(
                            self.model, self._rate, json.dumps(self._grammar).encode())
                    else:
                        self.rec = self.lib.vosk_recognizer_new(self.model, self._rate)
                except Exception:
                    self.rec = None
                if not self.rec:
                    return False
            self.lib.vosk_recognizer_accept_waveform(self.rec, pcm, len(pcm))
            return True

    def _text_of(self, raw, field="text"):
        try:
            doc = json.loads((raw or b"").decode("utf-8", errors="replace"))
        except Exception:
            return ""
        if isinstance(doc, dict):
            if field in doc and isinstance(doc[field], str):
                return doc[field]
            if field == "text" and isinstance(doc.get("partial"), str):
                return doc["partial"]
        return ""

    def partial(self):
        with self.lock:
            if not self.rec:
                return ""
            return self._text_of(self.lib.vosk_recognizer_partial_result(self.rec), "partial")

    def final(self):
        with self.lock:
            if not self.rec:
                return ""
            text = self._text_of(self.lib.vosk_recognizer_final_result(self.rec), "text")
            self.lib.vosk_recognizer_free(self.rec)
            self.rec = None
            return text


ENGINE = None


class Handler(BaseHTTPRequestHandler):
    server_version = "vosk-sidecar/1"

    def log_message(self, *a):
        pass

    def _send(self, obj, code=200):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _body(self):
        try:
            n = int(self.headers.get("Content-Length") or 0)
        except Exception:
            n = 0
        return self.rfile.read(max(0, n)) if n > 0 else b""

    def do_GET(self):
        try:
            if self.path == "/health":
                self._send({"ok": True, "model": ENGINE is not None})
            elif self.path == "/partial":
                self._send({"partial": ENGINE.partial() if ENGINE else ""})
            elif self.path == "/final":
                self._send({"text": ENGINE.final() if ENGINE else ""})
            else:
                self._send({"error": "unknown"}, 404)
        except Exception as e:
            self._send({"error": str(e)[:200]}, 500)

    def do_POST(self):
        try:
            doc = json.loads((self._body() or b"{}").decode("utf-8", errors="replace"))
            if self.path == "/init":
                ENGINE.new_utterance(doc.get("samplerate", 16000), doc.get("grammar"))
                self._send({"ok": True})
            elif self.path == "/accept":
                pcm = base64.b64decode(doc.get("pcm_b64", "") or b"")
                self._send({"ok": bool(ENGINE.accept(pcm)) if ENGINE else False})
            else:
                self._send({"error": "unknown"}, 404)
        except Exception as e:
            self._send({"error": str(e)[:200]}, 500)


def resolve_model_argv():
    for i, a in enumerate(sys.argv[1:], 1):
        if a in ("-h", "--help"):
            print(__doc__)
            raise SystemExit(0)
    pos = [a for a in sys.argv[1:] if not a.startswith("-")]
    model = os.environ.get("PAICOM_VOSK_MODEL_PATH", "")
    lib = os.environ.get("PAICOM_VOSK_LIB", "")
    port = PORT
    if len(pos) > 0:
        model = pos[0]
    if len(pos) > 1 and not pos[1].isdigit():
        lib = pos[1]
    if len(pos) > 2 or (len(pos) > 1 and pos[1].isdigit()):
        port = int(pos[-1])
    if not model:
        model = os.path.join(os.path.dirname(os.path.abspath(__file__)), "models")
    # model dir may point at a parent containing the model subdir
    if os.path.isdir(model) and not os.path.exists(os.path.join(model, "am", "final.mdl")):
        for root, dirs, files in os.walk(model):
            if "final.mdl" in files and os.path.basename(root) == "am":
                model = os.path.dirname(root)
                break
    if not lib:
        here = os.path.dirname(os.path.abspath(__file__))
        cands = [os.path.join(here, "libvosk.dll")] if os.name == "nt" else []
        cands += [os.path.join(here, "libvosk.so"),
                  "/usr/lib/x86_64-linux-gnu/libvosk.so"]
        for cand in cands:
            if os.path.exists(cand):
                lib = cand
                break
    return model, lib, port


def main():
    global ENGINE
    model, lib, port = resolve_model_argv()
    if not lib or not os.path.exists(lib):
        print("vosk-sidecar: libvosk not found (want libvosk.dll on Windows, libvosk.so on Linux)", flush=True)
        raise SystemExit(2)
    ENGINE = VoskNative(lib, model)
    ENGINE.new_utterance(16000, None)
    srv = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    print("vosk-sidecar: model=%s lib=%s port=%d" % (model, lib, port), flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()
