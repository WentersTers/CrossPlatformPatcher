# Ubuntu 22.04 X11 golden image — stage-0 VM host target (runbook Phase 3).
#
# Build ON the host (local qemu build, no remote complexity):
#   packer init harness/vms/packer/ubuntu-22.04.pkr.hcl
#   packer validate -var password_hash='$(mkpasswd -m sha-512)' \
#                   -var ssh_password='...' harness/vms/packer/ubuntu-22.04.pkr.hcl
#   packer build <same -var flags> harness/vms/packer/ubuntu-22.04.pkr.hcl
#
# First-draft template: expect Session-1 iteration (budget half a day).
# Post-build, in order: guest-ping QGA from the host, SSH up, xrandr 1920x1080,
# autologin lands on desktop, tablet in domain XML — then snapshot immediately
# (the rollback point). Resolution/final assertions are Session-1 checks;
# 03-assert.sh fails the BUILD on what is assertable at build time.
#
# 22.04 specifics baked in: live-server ISO + subiquity autoinstall, then
# ubuntu-desktop-minimal on top (full ubuntu-desktop is a one-line swap in
# scripts/02-desktop.sh); GDM autologin + forced X11; libasound2 (NOT the
# 24.04+ libasound2t64).

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
  description = "Build-user password (SSH + shutdown). Prefer -var-file."
}

variable "password_hash" {
  type        = string
  sensitive   = true
  description = "mkpasswd -m sha-512 output for autoinstall identity."
}

variable "iso_url" {
  type    = string
  default = "https://releases.ubuntu.com/22.04/ubuntu-22.04.5-live-server-amd64.iso"
}

variable "iso_checksum" {
  type    = string
  default = "none"
  description = "Fill 'sha256:<hash>' from https://releases.ubuntu.com/22.04/SHA256SUMS before first build."
}

variable "cpus" {
  type    = number
  default = 4
}

variable "memory" {
  type    = number
  default = 4096
}

variable "disk_size" {
  type    = string
  default = "60G"
}

variable "vm_name" {
  type    = string
  default = "ubuntu-22.04-x11-golden"
}

variable "output_directory" {
  type    = string
  default = "output-ubuntu-22.04-x11"
}

variable "headless" {
  type    = bool
  default = true
}

source "qemu" "ubuntu-2204-x11" {
  iso_url      = var.iso_url
  iso_checksum = var.iso_checksum

  output_directory = var.output_directory
  vm_name          = var.vm_name
  disk_size        = var.disk_size
  disk_interface   = "virtio"
  format           = "qcow2"

  accelerator = "kvm"
  cpu_model   = "host"
  cpus        = var.cpus
  memory      = var.memory
  headless    = var.headless

  http_content = {
    "/user-data" = templatefile("${path.root}/http/user-data.pkrtpl.hcl", {
      username      = var.ssh_username
      password_hash = var.password_hash
    })
    "/meta-data" = file("${path.root}/http/meta-data")
  }

  ssh_username = var.ssh_username
  ssh_password = var.ssh_password
  ssh_timeout  = "30m"

  shutdown_command = "echo '${var.ssh_password}' | sudo -S shutdown -P now"

  boot_wait = "10s"
  boot_command = [
    "c<wait>",
    "linux /casper/vmlinuz autoinstall ds='nocloud-net;s=http://{{ .HTTPIP }}:{{ .HTTPPort }}/' ---<enter><wait>",
    "initrd /casper/initrd<enter><wait>",
    "boot<enter>"
  ]

  qemuargs = [
    # virtio video; resolution is enforced guest-side (GDM + xrandr, Session 1)
    ["-vga", "virtio"],
    # USB controller first: default pc machine has no usb-bus, and usb-tablet
    # fails with "No 'usb-bus' bus found" (first-build finding, QEMU 6.2).
    ["-device", "qemu-xhci"],
    # USB tablet absolute coords (click() requires this; §8 / H-R §2)
    ["-device", "usb-tablet"],
    # QGA channel (watchdog qga_ping + guest-exec depend on this)
    ["-chardev", "socket,path=${var.output_directory}/qga.sock,server=on,wait=off,id=qga0"],
    ["-device", "virtio-serial-pci"],
    ["-device", "virtserialport,chardev=qga0,name=org.qemu.guest_agent.0"],
  ]
}

build {
  sources = ["source.qemu.ubuntu-2204-x11"]

  provisioner "shell" {
    environment_vars = ["GOLDEN_USER=${var.ssh_username}"]
    execute_command  = "sudo -E sh '{{ .Path }}'"
    # Order matters: configure (01/02/05/06), generalize (04), then assert
    # (03) last so it checks the final image state, including noblank read-back.
    scripts = [
      "${path.root}/scripts/01-base.sh",
      "${path.root}/scripts/02-desktop.sh",
      "${path.root}/scripts/05-noblank.sh",
      "${path.root}/scripts/06-visual-pin.sh",
      "${path.root}/scripts/04-cleanup.sh",
      "${path.root}/scripts/03-assert.sh",
    ]
  }
}
