# Windows 11 Enterprise Eval golden image — stage-0 Windows host target.
#
# Build ON the KVM host (local qemu build, same discipline as ubuntu-22.04):
#   packer init    harness/vms/packer/windows-11.pkr.hcl
#   packer validate -var-file=~/harness/vms/packer/secrets.pkrvars.hcl \
#                   harness/vms/packer/windows-11.pkr.hcl
#   packer build    -var-file=~/harness/vms/packer/secrets.pkrvars.hcl \
#                   harness/vms/packer/windows-11.pkr.hcl
#
# Design (fidelity notes, all deliberate):
# - Build firmware is BIOS/MBR with LabConfig bypasses (TPM/RAM/CPU/storage).
#   The FINAL libvirt domain adds an emulator TPM at import (libvirt manages
#   swtpm lifecycle); the build itself stays hermetic (no swtpm daemon dance).
#   Cost: SecureBoot-dependent features absent — irrelevant to the harness.
# - Build disk is IDE, build NIC is e1000 (inbox drivers, zero extra ISOs).
#   Packer's user-mode net stays virtio-net (unused in guest); the e1000 rides
#   the same netdev so hostfwd SSH works through a driven NIC.
# - Communicator is SSH: OpenSSH.Server lands via FirstLogonCommands (needs
#   guest egress at build time — host has it). Remote-UAC filtering is lifted
#   via LocalAccountTokenFilterPolicy so provisioners write HKLM/ProgramFiles.
# - Password reuse: ssh_password from secrets.pkrvars.hcl doubles as the
#   answer-file password (password_hash is IGNORED for Windows). Constraint:
#   the password must be XML-safe — host-setup generates hex, which is.
#   The answer file travels on a build-time extra CD (host-only), same secrecy
#   class as the ubuntu user-data HTTP serve.
# - No sysprep (ubuntu precedent: cleanup, not generalization; snapshot
#   lineage is the pattern). Eval 90-day grace starts at install — the install
#   date goes in the ceremony at snapshot time.
# - Post-build, in order: import to libvirt (virt-install --import, emulator
#   TPM, usb-tablet, 1920x1080), autologin lands on desktop, snapshot
#   immediately (the rollback point).
#
# 25H2 specifics baked in: CLIENTENTERPRISEEVAL single-image (Enterprise
# Evaluation), OOBE fully skipped (local accounts only, no MS account),
# Update set to notify-only, sleep/blank/lock off, consumer-feature downloads
# off. Python 3.12 ships in the golden (the sidecar's runtime); the Vosk model
# lands at sidecar stage, not here.

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
  default = "/home/sage/iso/26200.6584.250915-1905.25h2_ge_release_svc_refresh_CLIENTENTERPRISEEVAL_OEMRET_x64FRE_en-us.iso"
}

variable "iso_checksum" {
  type    = string
  default = "sha256:a61adeab895ef5a4db436e0a7011c92a2ff17bb0357f58b13bbc4062e535e7b9"
  description = "25H2 Enterprise Eval x64 en-us, operator download 2026-09-14."
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
  default = "windows-11-eval-golden"
}

variable "output_directory" {
  type    = string
  default = "output-windows-11-eval"
}

variable "headless" {
  type    = bool
  default = true
}

source "qemu" "windows-11-eval" {
  iso_url      = var.iso_path
  iso_checksum = var.iso_checksum

  output_directory = var.output_directory
  vm_name          = var.vm_name
  disk_size        = var.disk_size
  disk_interface   = "ide"
  format           = "qcow2"

  # e1000: inbox driver on 25H2, so no driver ISO is needed for build-time
  # network (packer's user-mode net + SSH hostfwd ride this NIC).
  net_device = "e1000"

  # Emulated TPM 2.0 at BUILD time (packer manages swtpm): the golden grows
  # up with the TPM present, so import adds no device delta. LabConfig
  # bypasses still carry the BIOS/MBR install; SecureBoot stays absent
  # (fidelity note in header).
  vtpm = true

  accelerator = "kvm"
  cpu_model   = "host"
  cpus        = var.cpus
  memory      = var.memory
  headless    = var.headless

  # Answer file baked into a second CD (setup reads \Autounattend.xml from
  # any removable media root). Credentials render from the shared secrets
  # file at validate/build time. Fresh disk is unbootable so SeaBIOS falls
  # through to the install ISO with no keystrokes (Enter during setup is
  # a hazard, so boot_command stays empty).
  cd_content = {
    "Autounattend.xml" = templatefile("${path.root}/http/Autounattend.pkrtpl.xml", {
      username = var.ssh_username
      password = var.ssh_password
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
  # driver ISO is needed. (q35 has no IDE controller and QEMU 6.2 rejects
  # -drive if=sata without an AHCI device — first-build finding.)
  # Packer's own -machine default (with accel=kvm) is left intact.
  qemuargs = [
    ["-vga", "std"],
    ["-device", "qemu-xhci"],
    ["-device", "usb-tablet"],
    ["-device", "usb-kbd"],
  ]
}

build {
  sources = ["source.qemu.windows-11-eval"]

  provisioner "powershell" {
    # Order mirrors ubuntu: configure (w01/w02), generalize (w04),
    # then assert (w03) last so it checks the final image state.
    scripts = [
      "${path.root}/scripts/w01-base.ps1",
      "${path.root}/scripts/w02-tooling.ps1",
      "${path.root}/scripts/w04-cleanup.ps1",
      "${path.root}/scripts/w03-assert.ps1",
    ]
  }
}
