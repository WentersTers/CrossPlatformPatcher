# Windows 10 golden image — stage-0 Windows host target, Win11 pattern.
#
# Build ON the KVM host (same discipline as windows-11):
#   packer init    harness/vms/packer/windows-10.pkr.hcl
#   packer build   -var-file=~/harness/vms/packer/secrets.pkrvars.hcl \
#                  -var image_name="..." -var iso_checksum="sha256:..." \
#                  harness/vms/packer/windows-10.pkr.hcl
#   (secrets.pkrvars.hcl lives on the HOST at ~/harness/... — same file the
#   Win11 build used; copy the repo template there, do not commit secrets.)
#
# Design (deltas from windows-11, all deliberate):
# - NO TPM anywhere: Win10 has no platform requirement, so the build stays
#   fully hermetic (no swtpm) and import adds no TPM device either.
# - IDE disk + e1000 NIC + i440fx + SeaBIOS, verbatim (inbox drivers).
# - SSH communicator + sftp + same shutdown/boot_wait (Win32-OpenSSH MOTW).
# - Answer file is Autounattend10 (clone of proven Win11 answer): same disk,
#   OOBE, SSH/UAC story; image NAME is a -var (per-ISO); LabConfig kept.
# - Runtime hardening baked in (Win11 census lesson): w02b-runtime installs
#   the VALIDATED VC++ redist (file-provisioned, md5 recorded below — never
#   downloaded). The mingw trio rides app/sidecar staging, not the golden.
# - Eval clock: install date goes in the ceremony at snapshot time; re-arm
#   by rebuild (Win11 discipline).
#
# ISO GATE (2026-09-15): the held windows-10-19041.1.iso (sha256 8a529c42…)
# is ZERO-PAYLOAD (install.esd reads zeros, boot.wim XML unparsable) and
# must NOT build. Good media: Win10 Enterprise LTSC 2021 x64 en-us
# (21H2 19044.1288, MSDN SW_DVD9 MLF_X22-84414), sha256-verified against
# archive.org metadata + rg-adguard + MS Q&A (c90a6df8…, size 4899461120),
# structural check passed (boot.wim 2 images, install.wim MSWIM magic,
# index 1 = Windows 10 Enterprise LTSC 2021). LTSC retail channel: no
# 90-day eval clock (runs unactivated, watermark cosmetic) — eval-clock
# discipline does not apply; note activation state at snapshot instead.

packer {
  required_plugins {
    qemu = {
      version = ">= 1.0.9"
      source  = "github.com/hashicorp/qemu"
    }
  }
}

variable "ssh_username" {
  type    = string
  default = "sage"
}

variable "ssh_password" {
  type        = string
  sensitive   = true
  description = "Build-user password: SSH + answer-file Administrator/sage (XML-safe hex). Prefer -var-file."
}

variable "password_hash" {
  type        = string
  sensitive   = true
  default     = ""
  description = "IGNORED for Windows (ubuntu autoinstall only). Accepted so the shared secrets file works."
}

variable "iso_path" {
  type    = string
  default = "/home/sage/iso/w10-ltsc2021-en.iso"
}

variable "iso_checksum" {
  type    = string
  default = "sha256:c90a6df8997bf49e56b9673982f3e80745058723a707aef8f22998ae6479597d"
  description = "Win10 Enterprise LTSC 2021 x64 en-us MSDN (verified 2026-09-15)."
}

variable "image_name" {
  type    = string
  default = "Windows 10 Enterprise LTSC 2021"
  description = "Must equal an /IMAGE/NAME in the ISO (wimlib-imagex info); setup fails loud otherwise."
}

variable "cpus" {
  type    = number
  default = 4
}

variable "memory" {
  type    = number
  default = 6144
}

variable "disk_size" {
  type    = string
  default = "80G"
}

variable "vm_name" {
  type    = string
  default = "windows-10-eval-golden"
}

variable "output_directory" {
  type    = string
  default = "output-windows-10-eval"
}

variable "headless" {
  type    = bool
  default = true
}

source "qemu" "windows-10-eval" {
  iso_url      = var.iso_path
  iso_checksum = var.iso_checksum

  output_directory = var.output_directory
  vm_name          = var.vm_name
  disk_size        = var.disk_size
  disk_interface   = "ide"
  format           = "qcow2"

  # e1000: inbox driver on Win10, so no driver ISO is needed for build-time
  # network (packer's user-mode net + SSH hostfwd ride this NIC).
  net_device = "e1000"

  # No vtpm: Win10 has no platform requirement (Win11 delta, deliberate).

  accelerator = "kvm"
  cpu_model   = "host"
  cpus        = var.cpus
  memory      = var.memory
  headless    = var.headless

  # Answer file on a second CD (setup reads \Autounattend.xml from any
  # removable media root). Fresh disk is unbootable so SeaBIOS falls through
  # to the install ISO with no keystrokes; boot_command stays empty.
  cd_content = {
    "Autounattend.xml" = templatefile("${path.root}/http/Autounattend10.pkrtpl.xml", {
      username   = var.ssh_username
      password   = var.ssh_password
      image_name = var.image_name
    })
  }
  cd_label = "AUTOINST"

  communicator   = "ssh"
  ssh_username   = var.ssh_username
  ssh_password   = var.ssh_password
  ssh_timeout    = "45m"
  # Win32-OpenSSH >= 9.1 scp returns non-zero (MOTW); SFTP is the
  # documented workaround.
  ssh_file_transfer_method = "sftp"

  shutdown_command = "shutdown /s /t 5 /f /c \"packer\""

  boot_wait = "45s"

  # pc (i440fx) machine: inbox IDE + e1000 + USB HID everywhere, so no
  # driver ISO is needed.
  qemuargs = [
    ["-vga", "std"],
    ["-device", "qemu-xhci"],
    ["-device", "usb-tablet"],
    ["-device", "usb-kbd"],
  ]
}

build {
  sources = ["source.qemu.windows-10-eval"]

  # Validated VC++ redist, staged on host from the Win11 census tree:
  # /home/sage/staging/win10-runtime/vc_redist.x64.exe
  # md5 486f81facf798678c3244c7cf35a557f (VC++ 14.44 x64).
  provisioner "file" {
    source      = "/home/sage/staging/win10-runtime/vc_redist.x64.exe"
    destination = "C:/Windows/Temp/packer-runtime/vc_redist.x64.exe"
  }

  provisioner "powershell" {
    # w02b-runtime is the Win10 hardening delta (VCredist install+assert);
    # the rest mirrors the proven Win11 order (configure, generalize,
    # assert last).
    scripts = [
      "${path.root}/scripts/w01-base.ps1",
      "${path.root}/scripts/w02-tooling.ps1",
      "${path.root}/scripts/w02b-runtime.ps1",
      "${path.root}/scripts/w04-cleanup.ps1",
      "${path.root}/scripts/w03-assert.ps1",
    ]
  }
}

# Post-build, in order: import to libvirt (virt-install --import, usb-tablet,
# 1920x1080, NO tpm), autologin lands on desktop, snapshot immediately
# (the rollback point), ledger install date + eval re-arm note in ceremony.
