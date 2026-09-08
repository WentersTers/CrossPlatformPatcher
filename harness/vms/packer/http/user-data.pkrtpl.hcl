#cloud-config
autoinstall:
  version: 1
  locale: en_US
  keyboard: {layout: us}
  identity:
    hostname: ubuntu-golden
    username: ${username}
    # mkpasswd -m sha-512 output, passed as -var (never committed)
    password: "${password_hash}"
  ssh:
    install-server: true
    allow-pw: true
  # 22.04 package names: libasound2 (NOT libasound2t64 — that is 24.04+)
  packages:
    - openssh-server
    - qemu-guest-agent
  storage:
    layout: {name: direct}
  late-commands:
    # passwordless sudo for the provisioners (test golden on a tailnet)
    - echo '${username} ALL=(ALL) NOPASSWD:ALL' > /target/etc/sudoers.d/90-golden
    - chmod 0440 /target/etc/sudoers.d/90-golden
    - curtin in-target -- systemctl enable ssh
    - curtin in-target -- systemctl enable qemu-guest-agent
