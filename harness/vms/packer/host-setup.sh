#!/bin/bash
# host-setup: packer binary + build secrets, generated entirely on-host.
# The password and hash below NEVER traverse the operator console: they are
# created here, written straight to secrets.pkrvars.hcl (mode 600), and the
# build consumes them via -var-file. Run once per host.
set -eu
BIN_DIR="$HOME/.local/bin"
mkdir -p "$BIN_DIR"
export PATH="$BIN_DIR:$PATH"
if ! command -v packer >/dev/null 2>&1; then
  PKVER=$(python3 -c 'import json,urllib.request; print(json.load(urllib.request.urlopen("https://checkpoint-api.hashicorp.com/v1/check/packer", timeout=30))["current_version"])')
  echo "installing packer $PKVER"
  PKURL="https://releases.hashicorp.com/packer/${PKVER}/packer_${PKVER}_linux_amd64.zip"
  python3 -c 'import sys,urllib.request; urllib.request.urlretrieve(sys.argv[1], "/tmp/packer.zip")' "$PKURL"
  python3 -c 'import zipfile; zipfile.ZipFile("/tmp/packer.zip").extractall("'"$BIN_DIR"'")'
  chmod +x "$BIN_DIR/packer"
  rm -f /tmp/packer.zip
fi
packer version
SECRETS="$HOME/harness/vms/packer/secrets.pkrvars.hcl"
if [ ! -f "$SECRETS" ]; then
  # mksalt alphabet has no braces, so the $6$ hash is HCL-safe (no ${...}).
  PASS=$(python3 -c 'import secrets; print(secrets.token_hex(16))')
  HASH=$(PASS="$PASS" python3 -c 'import crypt,os; print(crypt.crypt(os.environ["PASS"], crypt.mksalt(crypt.METHOD_SHA512)))')
  printf 'ssh_username  = "sage"\nssh_password  = "%s"\npassword_hash = "%s"\n' "$PASS" "$HASH" > "$SECRETS"
  chmod 600 "$SECRETS"
  echo "secrets written (mode 600): $SECRETS"
else
  echo "secrets already present, untouched"
fi
