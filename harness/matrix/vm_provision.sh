#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# D2 — vm_provision.sh : base images per distro, guest tools, snapshot creation.
#
# libvirt/qemu-kvm + cloud-init preferred (proven slirp hostfwd + cidata-labeled
# ext2 seed pattern from rig prior art). VirtualBox fallback is not implemented
# on this rig (no VBoxManage present) — the flag exists and refuses cleanly.
#
#   bash vm_provision.sh --leg L01 [--force] [--skip-app]
#
# Code-first installs (apt/dnf/pacman non-interactive); every package records
# which path was used (cloud-init | ssh-apt) in provision-record.json.
# Generation of the app bundle runs ONCE per base, then snapshot golden-<leg>;
# per-leg runs only ever revert to that snapshot.
# ---------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

LEG=""; FORCE=0; SKIP_APP=0
while [ $# -gt 0 ]; do
  case "$1" in
    --leg) LEG="$2"; shift 2 ;;
    --force) FORCE=1; shift ;;
    --skip-app) SKIP_APP=1; shift ;;
    --backend) log "backend=libvirt only on this rig (no VBoxManage); VirtualBox fallback refused"; shift 2 ;;
    *) die "unknown arg: $1" ;;
  esac
done
[ -n "$LEG" ] || die "--leg <LEG> required (e.g. --leg L01)"

OS_ID="$(leg_field "$LEG" 3)" || die "leg $LEG not in legs.conf"
OS_PRETTY="$(leg_field "$LEG" 4)"
RUNTIME_ID="$(leg_field "$LEG" 5)"
DOM="$(leg_domain "$LEG")"
PORT="$(leg_hostfwd_port "$LEG")"
SNAP="golden-$(echo "$LEG" | tr '[:upper:]' '[:lower:]')"
DISK="$POOL/$DOM.qcow2"
SEED="$POOL/$DOM-seed.img"
SERIAL_LOG="/var/log/libvirt/$DOM-serial.log"
STAMP="$(date -u +%FT%TZ)"
declare -a PKG_LOG=()

note_pkg() { PKG_LOG+=("$1|$2"); }   # name|install-path

# ---------------------------------------------------------------- base image
base_image_url() {
  case "$1" in
    ubuntu-2404) echo "https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img" ;;
    ubuntu-2204) echo "https://cloud-images.ubuntu.com/jammy/current/jammy-server-cloudimg-amd64.img" ;;
    debian-12)   echo "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-amd64.qcow2" ;;
    arch-latest) echo "https://geo.mirror.pkgbuild.com/images/latest/Arch-Linux-x86_64-cloudimg.qcow2" ;;
    fedora-41)   echo "" ;;  # resolved via directory listing below
    fedora-42)   echo "" ;;
    *) echo "" ;;
  esac
}

BASE_NAME="base-$OS_ID.img"
BASE_PATH="$POOL/$BASE_NAME"
log "== D2 provision $LEG ($OS_PRETTY / $RUNTIME_ID) dom=$DOM port=$PORT snap=$SNAP =="

if [ ! -f "$BASE_PATH" ]; then
  URL="$(base_image_url "$OS_ID")"
  if [ -z "$URL" ]; then
    case "$OS_ID" in
      fedora-41|fedora-42)
        REL="${OS_ID#fedora-}"
        URL="$(curl -fsSL "https://download.fedoraproject.org/pub/fedora/linux/releases/$REL/Cloud/x86_64/images/" \
              | grep -oE 'Fedora-Cloud-Base-Generic[^"]*\.qcow2' | head -1)"
        [ -n "$URL" ] && URL="https://download.fedoraproject.org/pub/fedora/linux/releases/$REL/Cloud/x86_64/images/$URL"
        ;;
    esac
  fi
  [ -n "$URL" ] || die "no base image URL for $OS_ID"
  log "downloading base image: $URL"
  curl -fL --retry 3 -o "$WORK/$BASE_NAME.partial" "$URL" || die "base download failed"
  sudo mv "$WORK/$BASE_NAME.partial" "$BASE_PATH"
  sudo qemu-img check "$BASE_PATH" >/dev/null || true
else
  log "base image present: $BASE_PATH"
fi

# ---------------------------------------------------------------- overlay disk
if [ -f "$DISK" ] && [ "$FORCE" = 1 ]; then
  sudo virsh destroy "$DOM" >/dev/null 2>&1 || true
  sudo rm -f "$DISK"
fi
if [ ! -f "$DISK" ]; then
  sudo qemu-img create -f qcow2 -F qcow2 -b "$BASE_PATH" -o backing_fmt=qcow2 \
       "$DISK" "${VM_DISK_G}G" || die "overlay create failed"
  log "overlay disk: $DISK (${VM_DISK_G}G thin over $BASE_NAME)"
else
  log "overlay disk present (use --force to rebuild)"
fi

# ---------------------------------------------------------------- cloud-init seed
mkdir -p "$WORK/seedmnt-$$"
USER_DATA="$WORK/user-data-$LEG"
cat > "$USER_DATA" <<EOF
#cloud-config
hostname: paimatrix-$(echo "$LEG" | tr '[:upper:]' '[:lower:]')
manage_etc_hosts: true
users:
  - name: $GUEST_USER
    sudo: "ALL=(ALL) NOPASSWD:ALL"
    shell: /bin/bash
    lock_passwd: false
    plain_text_passwd: sage
    ssh_authorized_keys:
      - $(ssh-keygen -y -f "$SSH_KEY" 2>/dev/null || echo "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIIKACRvYZsqEWcuU25V5PvkVkxvyvwIPSNHe22c+n0Yd vm-access")
ssh_pwauth: true
disable_root: false
package_update: true
package_upgrade: false
packages:
  - qemu-guest-agent
  - curl
  - wget
  - xvfb
  - x11-utils
  - x11-apps
  - unzip
  - cabextract
  - winbind
  - openbox
  - xdotool
  - imagemagick
  - scrot
  - python3-pil
  - fonts-dejavu-core
  - xterm
runcmd:
  - [ systemctl, enable, --now, qemu-guest-agent ]
  - [ sh, -c, "echo ready > /var/log/vm-ready" ]
  - [ sh, -c, "ssh-keygen -A" ]
final_message: "REV013-CLOUDINIT-DONE after \$UPTIME seconds"
EOF
printf 'instance-id: %s-%s\nlocal-hostname: paimatrix\n' "$DOM" "$STAMP" > "$WORK/meta-data-$LEG"

# NoCloud on a cdrom device requires an ISO9660 volume labeled 'cidata'
# (ext2-labeled disks are only visible when attached as a block disk —
# DataSourceNone observed live with the ext2 seed on noble).
sudo rm -f "$SEED"
if command -v cloud-localds >/dev/null 2>&1; then
  cloud-localds "$WORK/seed-$LEG.iso" "$USER_DATA" "$WORK/meta-data-$LEG" \
    || die "cloud-localds failed"
elif command -v genisoimage >/dev/null 2>&1; then
  genisoimage -output "$WORK/seed-$LEG.iso" -volid cidata -joliet -rock \
    -graft-points "user-data=$USER_DATA" "meta-data=$WORK/meta-data-$LEG" >/dev/null 2>&1 \
    || die "genisoimage failed"
elif command -v mkisofs >/dev/null 2>&1; then
  mkdir -p "$WORK/seedsrc-$LEG"
  cp "$USER_DATA" "$WORK/seedsrc-$LEG/user-data"
  cp "$WORK/meta-data-$LEG" "$WORK/seedsrc-$LEG/meta-data"
  mkisofs -output "$WORK/seed-$LEG.iso" -volid cidata -joliet -rock \
    "$WORK/seedsrc-$LEG" >/dev/null 2>&1 || die "mkisofs failed"
else
  sudo apt-get install -y -qq cloud-image-utils genisoimage >/dev/null 2>&1 || true
  if command -v cloud-localds >/dev/null 2>&1; then
    cloud-localds "$WORK/seed-$LEG.iso" "$USER_DATA" "$WORK/meta-data-$LEG" || die "cloud-localds failed"
  else
    die "no ISO tool for cidata seed (need cloud-localds or genisoimage)"
  fi
fi
sudo cp "$WORK/seed-$LEG.iso" "$SEED"
sudo chown libvirt-qemu:kvm "$SEED" 2>/dev/null || sudo chown qemu:qemu "$SEED" 2>/dev/null || true
sudo chmod 644 "$SEED"
log "seed ready: $SEED (iso9660 cidata, $(sudo du -h "$SEED" | cut -f1))"
for p in qemu-guest-agent curl wget xvfb x11-utils unzip cabextract winbind openbox xdotool imagemagick scrot python3-pil fonts-dejavu-core xterm; do
  note_pkg "$p" "cloud-init-packages"
done

# ---------------------------------------------------------------- domain (slirp hostfwd)
sudo virsh destroy "$DOM" >/dev/null 2>&1 || true
if ! sudo virsh dominfo "$DOM" >/dev/null 2>&1; then
  cat > "$WORK/$DOM.xml" <<XML
<domain type='kvm' xmlns:qemu='http://libvirt.org/schemas/domain/qemu/1.0'>
  <name>$DOM</name>
  <memory unit='MiB'>$VM_MEM_MIB</memory>
  <currentMemory unit='MiB'>$VM_MEM_MIB</currentMemory>
  <vcpu placement='static'>$VM_VCPUS</vcpu>
  <os><type arch='x86_64' machine='pc'>hvm</type><boot dev='hd'/></os>
  <features><acpi/><apic/></features>
  <cpu mode='host-model'/>
  <clock offset='utc'/>
  <on_poweroff>destroy</on_poweroff>
  <on_reboot>restart</on_reboot>
  <on_crash>destroy</on_crash>
  <devices>
    <emulator>/usr/bin/qemu-system-x86_64</emulator>
    <disk type='file' device='disk'>
      <driver name='qemu' type='qcow2' cache='writeback'/>
      <source file='$DISK'/>
      <target dev='vda' bus='virtio'/>
    </disk>
    <disk type='file' device='cdrom'>
      <driver name='qemu' type='raw'/>
      <source file='$SEED'/>
      <target dev='hdb' bus='ide'/>
      <readonly/>
    </disk>
    <serial type='file'><source path='$SERIAL_LOG'/><target type='isa-serial' port='0'/></serial>
    <console type='file'><source path='$SERIAL_LOG'/><target type='serial'/></console>
    <input type='tablet' bus='usb'/>
    <input type='mouse' bus='ps2'/>
    <input type='keyboard' bus='ps2'/>
    <channel type='unix'><target type='virtio' name='org.qemu.guest_agent.0'/></channel>
    <graphics type='vnc' port='-1' listen='127.0.0.1'/>
    <interface type='network'>
      <source network='default'/>
      <model type='virtio'/>
      <mac address='52:54:00:aa:bb:$(printf '%02x' $((10#${LEG#L})))'/>
    </interface>
  </devices>
</domain>
XML
  sudo virsh define "$WORK/$DOM.xml" >/dev/null || die "virsh define failed"
fi
sudo rm -f "$SERIAL_LOG"; sudo touch "$SERIAL_LOG"; sudo chmod 666 "$SERIAL_LOG"
sudo virsh start "$DOM" || die "virsh start failed"

# QGA channel sanity (idempotent wait)
for i in $(seq 1 30); do
  if qga "$DOM" '{"execute":"guest-ping"}' >/dev/null 2>&1; then log "QGA channel alive"; break; fi
  sleep 2
done

log "discovering guest IP on default NAT network"
GUEST_IP="$(discover_ip "$DOM")" || {
  log "no DHCP address; serial log tail:"
  sudo tail -30 "$SERIAL_LOG" | sed -e 's/\x1b\[[0-9;]*m//g'
  die "guest has no address"
}
echo "$GUEST_IP" > "$(leg_ip_file "$LEG")"
log "guest ip: $GUEST_IP"
wait_ssh "$GUEST_IP" 300 || {
  log "SSH never came up; serial log tail:"
  sudo tail -30 "$SERIAL_LOG" | sed -e 's/\x1b\[[0-9;]*m//g'
  die "guest unreachable"
}
gssh "$GUEST_IP" 'cloud-init status --wait >/dev/null 2>&1; cloud-init status | head -1' || true

# ---------------------------------------------------------------- runtime layer
WINE_VERSION="(not installed)"
MONO_MSI_NAME="(none)"
MONO_MSI_ASSERT="NOT-RUN"
GUEST_MSI_PATH=""

install_wine() {
  case "$OS_ID" in
    ubuntu-2404|ubuntu-2204)
      local codename=noble
      [ "$OS_ID" = "ubuntu-2204" ] && codename=jammy
      gssh "$GUEST_IP" "sudo dpkg --add-architecture i386 && \
        sudo mkdir -pm755 /etc/apt/keyrings && \
        sudo wget -qO /etc/apt/keyrings/winehq-archive.key https://dl.winehq.org/wine-builds/winehq.key && \
        sudo wget -qNP /etc/apt/sources.list.d/ https://dl.winehq.org/wine-builds/ubuntu/dists/$codename/winehq-$codename.sources && \
        sudo apt-get update -qq && \
        (sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --install-recommends winehq-staging || \
         sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --install-recommends winehq-devel || \
         sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --install-recommends winehq-stable)" \
        && note_pkg "wine" "ssh-apt-winehq-$codename" || return 1
      ;;
    debian-12)
      gssh "$GUEST_IP" "sudo dpkg --add-architecture i386 && \
        sudo mkdir -pm755 /etc/apt/keyrings && \
        sudo wget -qO /etc/apt/keyrings/winehq-archive.key https://dl.winehq.org/wine-builds/winehq.key && \
        sudo wget -qNP /etc/apt/sources.list.d/ https://dl.winehq.org/wine-builds/debian/dists/bookworm/winehq-bookworm.sources && \
        sudo apt-get update -qq && \
        (sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --install-recommends winehq-staging || \
         sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --install-recommends winehq-stable)" \
        && note_pkg "wine" "ssh-apt-winehq-bookworm" || return 1
      ;;
    arch-latest)
      gssh "$GUEST_IP" "sudo pacman -Sy --noconfirm wine wine-mono" \
        && note_pkg "wine" "ssh-pacman" || return 1
      ;;
    fedora-41|fedora-42)
      local rel="${OS_ID#fedora-}"
      gssh "$GUEST_IP" "sudo dnf install -y 'dnf-command(config-manager)' && \
        sudo dnf config-manager --add-repo https://dl.winehq.org/wine-builds/fedora/$rel/winehq-$rel.repo && \
        sudo dnf install -y winehq-staging" \
        && note_pkg "wine" "ssh-dnf-winehq" || return 1
      ;;
    *) log "no wine install recipe for $OS_ID"; return 1 ;;
  esac
}

case "$RUNTIME_ID" in
  wine11-mono|wine-distro-mono|wine-latest-mono)
    log "-- runtime: wine + wine-mono ($RUNTIME_ID)"
    install_wine || die "wine install failed"
    WINE_VERSION="$(gssh "$GUEST_IP" 'wine --version 2>/dev/null' | tail -1)"
    log "   wine version: $WINE_VERSION"

    MONO_MSI_NAME="wine-mono-${MONO_MSI_VERSION}-x86.msi"
    GUEST_MSI_PATH="/tmp/$MONO_MSI_NAME"
    gssh "$GUEST_IP" "wget -qO '$GUEST_MSI_PATH' 'https://dl.winehq.org/wine/wine-mono/${MONO_MSI_VERSION}/$MONO_MSI_NAME'" \
      || die "wine-mono download failed"
    # ---- do-not-regress #2: x86.msi exactly, never a _64 variant
    case "$MONO_MSI_NAME" in
      *"_64"*) MONO_MSI_ASSERT="FAIL" ;;
      wine-mono-*-x86.msi) MONO_MSI_ASSERT="PASS" ;;
      *) MONO_MSI_ASSERT="FAIL" ;;
    esac
    [ "$MONO_MSI_ASSERT" = "PASS" ] || die "wine-mono MSI filename violates do-not-regress #2: $MONO_MSI_NAME"
    note_pkg "wine-mono-$MONO_MSI_VERSION" "guest-wget-dl.winehq.org"
    log "   wine-mono MSI: $MONO_MSI_NAME assert=$MONO_MSI_ASSERT"
    ;;
  ge-proton11-7|proton-steam|winetricks-dotnet48|flatpak-wine)
    log "-- runtime '$RUNTIME_ID' provisioning is Phase 1/2 scope (stub recorded)"
    ;;
  *)
    die "unknown runtime_id $RUNTIME_ID"
    ;;
esac

# ---------------------------------------------------------------- app bundle (host)
BUNDLE_SHA="(none)"
if [ "$SKIP_APP" = 0 ]; then
  log "-- building patcher from $SRC_DIR (git HEAD, provenance-pinned)"
  [ -f "$SRC_DIR/CrossPlatformPatcher.csproj" ] || die "no source at $SRC_DIR (deploy step missing)"
  if [ ! -f "$MATRIX_ROOT/provenance.json" ]; then
    die "provenance.json missing (deploy step must capture git sha/dirty on the console box)"
  fi
  dotnet publish "$SRC_DIR/CrossPlatformPatcher.csproj" -c Release -r linux-x64 \
        --self-contained false -o "$WORK/patcher-build" > "$WORK/dotnet-publish.log" 2>&1 \
    || { tail -30 "$WORK/dotnet-publish.log"; die "dotnet publish failed"; }
  note_pkg "CrossPlatformPatcher" "dotnet-publish-from-src"
  PROV_FILE="$MATRIX_ROOT/provenance.json" python3 - <<'PY'
import json, datetime, os
p = os.environ["PROV_FILE"]
doc = json.load(open(p))
doc["build_ts"] = datetime.datetime.utcnow().isoformat() + "Z"
json.dump(doc, open(p, "w"), indent=2)
print("provenance build_ts:", doc["build_ts"])
PY

  log "-- regenerating app bundle from the authoritative full tree"
  # RULE 1/2 (restricted operating rules): complete product tree + real
  # program (target from the private input) — a partial tree or wrong target
  # produces startup crashes that must never be adjudicated as product verdicts.
  PATCH_TARGET="$(tree_target)" || PATCH_TARGET=""
  [ -n "$PATCH_TARGET" ] || die "patch target not configured — private input must carry a target| entry"
  verify_app_tree "$APP_INPUT" || die "RULE 1/2 violation: app-input incomplete or wrong target — read the restricted operating rules and re-stage from the authoritative install"
  rm -rf "$APP_BUNDLE"; mkdir -p "$APP_BUNDLE"
  cp -a "$APP_INPUT/." "$APP_BUNDLE/"
  [ -f "$APP_BUNDLE/$PATCH_TARGET" ] || die "RULE 2 violation: real program '$PATCH_TARGET' not in bundle"
  dotnet "$WORK/patcher-build/CrossPlatformPatcher.dll" \
      "$APP_BUNDLE/$PATCH_TARGET" \
      --out "$APP_BUNDLE/$PATCH_TARGET.patched.exe" \
      --migration-mode full > "$WORK/patch-run.log" 2>&1 \
    || { tail -30 "$WORK/patch-run.log"; die "patcher run failed"; }
  [ -f "$APP_BUNDLE/$PATCH_TARGET.patched.exe" ] || die "patched exe not produced"
  [ -f "$APP_BUNDLE/run.sh" ] || die "generated run.sh not produced in out dir"
  chmod +x "$APP_BUNDLE/run.sh"
  BUNDLE_SHA="$(sha256_of "$APP_BUNDLE/$PATCH_TARGET.patched.exe")"
  log "   bundle ready: $PATCH_TARGET.patched.exe sha256=$BUNDLE_SHA"
  log "   run.sh target: $(grep -m1 'EXE=' "$APP_BUNDLE/run.sh")"
  log "   run.sh WINEDLLOVERRIDES lines: $(grep -c 'WINEDLLOVERRIDES' "$APP_BUNDLE/run.sh" || true)"
else
  log "-- skipping app build (--skip-app); using existing $APP_BUNDLE"
fi

# ---------------------------------------------------------------- push bundle to guest
log "-- staging bundle into guest:$GUEST_GAME_DIR"
gssh "$GUEST_IP" "rm -rf '$GUEST_GAME_DIR'; mkdir -p '$GUEST_GAME_DIR'"
tar -C "$APP_BUNDLE" -czf - . | gssh "$GUEST_IP" "tar -C '$GUEST_GAME_DIR' -xzf -" || die "bundle transfer failed"
gssh "$GUEST_IP" "chmod +x '$GUEST_GAME_DIR/run.sh'; ls -la '$GUEST_GAME_DIR' | head -20"

# ---------------------------------------------------------------- guest prefix + mono
log "-- creating project-local prefix and installing wine-mono into it"
gssh "$GUEST_IP" "export WINEPREFIX='$GUEST_PREFIX' WINEARCH=win64; \
  wineboot -u >/dev/null 2>&1; wineserver -w" || die "prefix init failed"
if [ -n "$GUEST_MSI_PATH" ] && [ "$MONO_MSI_ASSERT" = "PASS" ]; then
  gssh "$GUEST_IP" "export WINEPREFIX='$GUEST_PREFIX'; \
    wine msiexec /i '$GUEST_MSI_PATH' /qn >/tmp/mono-install.log 2>&1; \
    wineserver -w; tail -5 /tmp/mono-install.log" || die "wine-mono msiexec failed"
  log "   wine-mono installed into $GUEST_PREFIX"
fi
gssh "$GUEST_IP" "find '$GUEST_PREFIX' -iname 'mscorlib.dll' 2>/dev/null | head -5"

# ---------------------------------------------------------------- snapshot
log "-- snapshot $SNAP"
# drop any stale snapshot first (snapshot-create-as fails silently otherwise
# and the existence poll would false-positive on the OLD snapshot)
if sudo virsh snapshot-list "$DOM" 2>/dev/null | grep -q "$SNAP"; then
  sudo virsh snapshot-delete "$DOM" "$SNAP" --metadata >/dev/null 2>&1 || \
  sudo virsh snapshot-delete "$DOM" "$SNAP" >/dev/null 2>&1 || true
fi
sudo virsh snapshot-create-as "$DOM" "$SNAP" "rev013-base-$LEG" \
     --diskspec vda,snapshot=internal >/dev/null 2>&1 || true
# seam: client wait may exceed 300s while the server finishes — verify by listing
FOUND=""
for i in $(seq 1 24); do
  if sudo virsh snapshot-list "$DOM" 2>/dev/null | grep -q "$SNAP"; then FOUND=1; break; fi
  sleep 5
done
[ -n "$FOUND" ] || die "snapshot $SNAP not visible after 120s"
log "   snapshot verified: $SNAP"

# ---------------------------------------------------------------- provision record
PKG_JSON="$(printf '%s\n' "${PKG_LOG[@]}" | python3 -c '
import json, sys
pkgs = []
for line in sys.stdin:
    line = line.strip()
    if not line: continue
    name, path = line.split("|", 1)
    pkgs.append({"package": name, "install_path": path})
print(json.dumps(pkgs))')"

python3 - <<PY
import json
rec = {
  "schema": "rev013-provision-v1",
  "leg": "$LEG",
  "os_pretty": "$OS_PRETTY",
  "os_id": "$OS_ID",
  "runtime_id": "$RUNTIME_ID",
  "domain": "$DOM",
  "ssh_port": $PORT,
  "snapshot": "$SNAP",
  "wine_version": "$WINE_VERSION",
  "mono_msi_filename": "$MONO_MSI_NAME",
  "mono_msi_assert": "$MONO_MSI_ASSERT",
  "bundle_sha256": "$BUNDLE_SHA",
  "packages": json.loads('''$PKG_JSON'''),
  "provenance": json.load(open("$MATRIX_ROOT/provenance.json")),
  "provisioned_ts": "$STAMP",
}
out = "$PROVISION_LOG"
try:
    old = json.load(open(out))
    if isinstance(old, list): old.append(rec)
    else: old = [old, rec]
except Exception:
    old = [rec]
json.dump(old, open(out, "w"), indent=2)
print("provision record appended:", out)
PY

log "== D2 done: $LEG -> dom=$DOM snap=$SNAP wine='$WINE_VERSION' mono=$MONO_MSI_NAME ($MONO_MSI_ASSERT) =="
