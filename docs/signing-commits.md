# Commit signing and git over SSH

For everyone who works with repositories container-forest deploys from. It covers setup on Linux, macOS and Windows, for GitHub and Gitea.

## Why

A stack with *Deploy only signed commits* turned on deploys only a commit signed with the key of a person allowed to deploy that stack ([ADR-0011](adr/0011-signed-commits.md), [Content endorsement](architecture.md#content-endorsement-commit-signatures)). Without signatures, push access to the branch effectively equals root on the host and access to the environment's secrets, including for a compromised git server. An unsigned commit can only be deployed by hand, by approving its SHA in the panel.

Recommendations:

- **A separate signing key per device.** One device, one key. Lose a laptop, and you delete only its key.
- **Don't use the signing key to log in over SSH**, or login keys to sign. More in [Why a separate signing key](#why-a-separate-signing-key).
- **Use git over SSH rather than HTTPS.** Why: [Git over SSH, not HTTPS](#git-over-ssh-not-https).

You need git 2.34 or later (`git --version`), OpenSSH 8.2 or later (`ssh -V`), and 8.9 or later for a hardware key.

## 1. Generate a signing key

In the commands below, `<device>` is the device's name, e.g. `laptop`; it goes into the key's comment and helps you recognise the key in lists.

### Linux and macOS

```bash
ssh-keygen -t ed25519 -C "<your name>@<device> (commit signing)" -f ~/.ssh/id_commit_signing
```

Set a passphrase when `ssh-keygen` asks: it protects the private key if the file is stolen.

### Windows (PowerShell)

Windows 10 and 11 already have OpenSSH (`C:\Windows\System32\OpenSSH`). If `ssh -V` shows a version older than 8.2, update OpenSSH (for example, `winget install Microsoft.OpenSSH.Preview`).

```powershell
ssh-keygen -t ed25519 -C "<your name>@<device> (commit signing)" -f $HOME\.ssh\id_commit_signing
```

### A hardware key (YubiKey, other FIDO2): recommended

The private part stays on the device: it can't be copied, and every signature needs a touch.

```bash
ssh-keygen -t ed25519-sk -O resident -O verify-required -C "<your name>@<device> (commit signing)" -f ~/.ssh/id_commit_signing_sk
```

- **Linux:** you need the `libfido2` library (Debian and Ubuntu: `sudo apt install libfido2-1`).
- **macOS:** Apple's system OpenSSH is built without FIDO support. Install OpenSSH from Homebrew (`brew install openssh`) and use it (`/opt/homebrew/bin/ssh-keygen`).
- **Windows:** OpenSSH 8.9 and later works with FIDO through Windows Hello; in PowerShell replace `~/.ssh/…` with `$HOME\.ssh\…`.

From here on, for a hardware key write `id_commit_signing_sk` wherever the guide says `id_commit_signing`.

## 2. Add the key to the agent

So you aren't asked for the passphrase on every commit.

### Linux

```bash
ssh-add ~/.ssh/id_commit_signing
```

GNOME and KDE start an agent themselves. Standalone Wayland compositors (Hyprland, Sway, niri), X11 window managers (i3, bspwm) and sessions without graphics don't, and then `ssh-add` answers `Could not open a connection to your authentication agent`: `SSH_AUTH_SOCK` isn't set because there's no agent. There are three options.

**The agent as a systemd user service** (recommended): it works in every terminal and program of the session while you're logged in. Create `~/.config/systemd/user/ssh-agent.service`:

```ini
[Unit]
Description=SSH key agent

[Service]
Type=simple
Environment=SSH_AUTH_SOCK=%t/ssh-agent.socket
ExecStart=/usr/bin/ssh-agent -D -a $SSH_AUTH_SOCK

[Install]
WantedBy=default.target
```

Enable the service:

```bash
systemctl --user enable --now ssh-agent
```

Tell your shell where the agent is: add the line below to `~/.bashrc` or `~/.zshrc` and open a new terminal.

```bash
export SSH_AUTH_SOCK="$XDG_RUNTIME_DIR/ssh-agent.socket"
```

So that programs not started from a terminal (IDEs, git clients) see the agent too, set the same variable for the whole session. A compositor-independent way is the file `~/.config/environment.d/ssh-agent.conf`:

```
SSH_AUTH_SOCK=${XDG_RUNTIME_DIR}/ssh-agent.socket
```

The systemd user manager reads it, so user services and sessions started through systemd get the variable. It appears after you log in again. If your compositor starts differently, set `SSH_AUTH_SOCK` in its own configuration.

**Hyprland.** Pick one of these depending on how it's started:

- **with `uwsm`** (as the Hyprland wiki recommends): a line in `~/.config/uwsm/env`; the wiki recommends keeping session variables there rather than in Hyprland's config:

  ```
  export SSH_AUTH_SOCK="$XDG_RUNTIME_DIR/ssh-agent.socket"
  ```

- **without `uwsm`, a Lua config** (`~/.config/hypr/hyprland.lua`, in newer versions): `hl.env`; refer to other variables with `os.getenv`, not `$`:

  ```lua
  hl.env("SSH_AUTH_SOCK", os.getenv("XDG_RUNTIME_DIR") .. "/ssh-agent.socket")
  ```

- **without `uwsm`, the older config** (`~/.config/hypr/hyprland.conf`):

  ```
  env = SSH_AUTH_SOCK,$XDG_RUNTIME_DIR/ssh-agent.socket
  ```

The syntax follows the [Hyprland wiki](https://wiki.hypr.land/Configuring/Advanced-and-Cool/Environment-variables/). The variable takes effect after restarting the session.

Some distributions already ship such a service in the `openssh` package (`systemctl --user cat ssh-agent` shows it). Then you don't need your own file, but check which socket path it sets and use exactly that in `SSH_AUTH_SOCK`.

With these lines in `~/.ssh/config` the key goes into the agent by itself on first use, and you won't need `ssh-add`:

```
Host *
  AddKeysToAgent yes
```

**An agent for one terminal only:** `eval "$(ssh-agent -s)"`, then `ssh-add`. Other terminals and IDEs don't see this agent, and it's gone when the terminal closes.

**No agent:** configure nothing. Git will then ask for the key's passphrase on every commit. With a hardware key (`ed25519-sk`) the agent is optional: signing needs a touch anyway, and the key file usually has no passphrase.

### macOS

```bash
ssh-add --apple-use-keychain ~/.ssh/id_commit_signing
```

To take the passphrase from the Keychain after a reboot, add to `~/.ssh/config`:

```
Host *
  UseKeychain yes
  AddKeysToAgent yes
```

### Windows

Enable the agent service once, in PowerShell **as administrator**:

```powershell
Get-Service ssh-agent | Set-Service -StartupType Automatic
```

```powershell
Start-Service ssh-agent
```

Then in an ordinary PowerShell:

```powershell
ssh-add $HOME\.ssh\id_commit_signing
```

**Don't forward the agent to hosts you don't trust** (`ForwardAgent yes`, `ssh -A`): while the session is open, root on such a host can sign commits with your key. If you must forward the agent, add the signing key with confirmation of every use: `ssh-add -c` (Linux, macOS).

## 3. Configure git

The same on every system:

```bash
git config --global gpg.format ssh
```

```bash
git config --global user.signingkey ~/.ssh/id_commit_signing.pub
```

The path to the private key (`~/.ssh/id_commit_signing`) works too. The public one is more versatile: git passes it to `ssh-keygen`, which takes the private key from the agent if it's there, or else from the file next to it without `.pub`. A key with no file on disk, as in 1Password or KeePassXC, can only be given as the public key.

```bash
git config --global commit.gpgsign true
```

```bash
git config --global tag.gpgsign true
```

`user.email` must match a verified address of your GitHub and Gitea accounts, or they won't show *Verified*:

```bash
git config --global user.email
```

### Windows: making git and the agent work together

Git for Windows has its own OpenSSH, which doesn't see the Windows `ssh-agent` service. Point git at the system OpenSSH:

```powershell
git config --global gpg.ssh.program "C:/Windows/System32/OpenSSH/ssh-keygen.exe"
```

```powershell
git config --global core.sshCommand "C:/Windows/System32/OpenSSH/ssh.exe"
```

In PowerShell, give the key's path explicitly: `git config --global user.signingkey "$HOME/.ssh/id_commit_signing.pub"`.

### Checking signatures locally (optional)

For `git log --show-signature` to say *Good signature*, git has to know which keys to trust:

```bash
echo "$(git config user.email) namespaces=\"git\" $(cat ~/.ssh/id_commit_signing.pub)" >> ~/.ssh/allowed_signers
```

```bash
git config --global gpg.ssh.allowedSignersFile ~/.ssh/allowed_signers
```

In PowerShell the first step is `Add-Content $HOME\.ssh\allowed_signers "$(git config user.email) namespaces=`"git`" $(Get-Content $HOME\.ssh\id_commit_signing.pub)"`. container-forest doesn't need this file: the aggregator verifies signatures with the keys from the panel.

## 4. Add the key to container-forest

Copy the public key: `cat ~/.ssh/id_commit_signing.pub` (Windows: `Get-Content $HOME\.ssh\id_commit_signing.pub`).

Panel → *Account* → *Commit signing keys* → *Add key*: a name (the device), the public key, environments.

- **Environments.** A key endorses a stack's deployment only if you can deploy that stack yourself. Limiting it to some environments narrows it further: limit a key on a device you trust less (a work computer, a shared machine) to environments of the same level of trust.
- **You can't add your own key with an API token or the CLI**, only from a panel session.
- **A service user's key** (for example `ci`) is added by an administrator: `forest user signing-key add ci runner ./ci.pub -e <environment>`.

## 5. Add the key to GitHub and Gitea

These services show *Verified* on commits as a convenience for reviews. For container-forest it means nothing: the aggregator verifies signatures itself.

### GitHub

Settings → **SSH and GPG keys** → **New SSH key**, **Key type** **Signing Key**, and the contents of the `.pub` in **Key**.

An Authentication key doesn't verify commits, and a Signing key doesn't allow SSH login. That's exactly the separation we want.

### Gitea

Settings → **SSH / GPG Keys** → the **Manage SSH Keys** block → **Add Key**: a name and the contents of the `.pub`. After saving, press **Verify** next to the key: Gitea shows a token to sign with this key.

Linux and macOS:

```bash
echo -n '<token>' | ssh-keygen -Y sign -n gitea -f ~/.ssh/id_commit_signing
```

Windows (PowerShell; the token is written to a file without a newline, and the signature appears next to it in `token.txt.sig`):

```powershell
Set-Content -NoNewline -Path token.txt -Value '<token>'
```

```powershell
ssh-keygen -Y sign -n gitea -f $HOME\.ssh\id_commit_signing token.txt
```

Paste the signature from `-----BEGIN SSH SIGNATURE-----` to `-----END SSH SIGNATURE-----` into the form. If there's no **Verify** button, the instance's administrator turned verification off, and the key is considered verified by email.

⚠️ Gitea doesn't separate login and signing keys: an added signing key can also push over SSH with your permissions. That's fine for your own key, but keep it in mind for a service user's key.

## 6. Check

```bash
git commit --allow-empty -m "test: signed commit"
```

```bash
git log --show-signature -1
```

After a push, GitHub and Gitea show *Verified* next to the commit. For a stack that requires signatures, the deployment history shows *Signed by <name>*, and the deployment screen shows the key's fingerprint.

## Working with it

- **Ordinary commits** are signed automatically (`commit.gpgsign true`), as are `git commit --amend` and `git rebase`: they create new commits and sign them.
- **Merge into a branch a stack deploys from** locally: `git merge -S`, or a fast-forward to a commit signed by an author allowed to deploy. Merging with the button in Gitea's or GitHub's web interface signs with the platform's key, and such a commit deploys only after its SHA is approved in the panel (deployment screen → *Approve and deploy*) or with `forest stack deploy <stack> --approve <sha>`.
- **The branch head's signature endorses the whole state of the repository**, including unsigned commits under it. Don't sign a commit on top of other people's changes you haven't reviewed.
- **A force push** to a stack's branch stops deploy on push and the CI hook: the new commit must descend from the deployed one. Such a commit deploys only by hand.

## A new device, a lost device

- **A new device:** a new key (steps 1–6); don't copy the old ones.
- **Lost or compromised:** delete its signing key in the panel (*Account* → *Commit signing keys*), on GitHub and in Gitea, along with that device's SSH login keys. A key deleted from the panel stops endorsing deployments at once, including for commits already pushed.

## Git over SSH, not HTTPS

Use `git@…:…` URLs with Gitea and GitHub rather than `https://…`. Login keys are your own, separate from the signing key.

### Why

- **No passwords or tokens on disk.** HTTPS needs an access token (GitHub no longer accepts passwords), and git stores it in a credential helper, often in plain text in `~/.git-credentials`. Such a token usually has access to all your repositories, not just one, and leaks with the file. Over SSH the server checks the request's signature, and the private key never leaves the device.
- **Hardware keys.** A login key can live on a FIDO key too (`ed25519-sk`): without a touch nobody can push in your name, even with access to your laptop. There's no equivalent for an HTTPS token.
- **A key per device, revoked separately.** Lose a laptop and you delete one key; your other devices keep working. A shared token would have to be reissued and set up again everywhere.
- **No expiry or rotation.** HTTPS tokens expire, and pushing suddenly stops working. An SSH key works until you delete it.
- **Phishing protection.** An SSH key sends nothing to the server, so a page pretending to be Gitea can't steal anything. A token can be pasted into the wrong place.
- **Server verification.** SSH remembers the host key (`known_hosts`) and warns if it changes: an impersonated Gitea is noticed at once.
- **One agent.** The same `ssh-agent` provides keys for both git and signing, so you enter the passphrase once per session.

### How to switch

1. A login key (separate from the signing key), one per service or per device:

```bash
ssh-keygen -t ed25519 -C "<your name>@<device> (gitea)" -f ~/.ssh/id_ed25519_gitea
```

2. Add the `.pub`: GitHub → Settings → SSH and GPG keys → **New SSH key**, type **Authentication Key**; Gitea → Settings → SSH / GPG Keys → **Add Key**.

3. Say which key goes to which server in `~/.ssh/config` (Windows: `$HOME\.ssh\config`). Ask the administrator for Gitea's SSH port: it often isn't 22.

```
Host gitea.example.com
  User git
  Port 22
  IdentityFile ~/.ssh/id_ed25519_gitea
  IdentitiesOnly yes

Host github.com
  User git
  IdentityFile ~/.ssh/id_ed25519_github
  IdentitiesOnly yes
```

`IdentitiesOnly yes` stops SSH from offering the server every key in the agent, the signing key included.

4. Check, and switch existing clones:

```bash
ssh -T git@gitea.example.com
```

```bash
git remote set-url origin git@gitea.example.com:<organisation>/<repository>.git
```

On the first connection SSH shows the host key's fingerprint. Compare it with the one from Gitea's administrator or [GitHub's documentation](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/githubs-ssh-key-fingerprints), and only then answer `yes`.

If your network blocks port 22, GitHub accepts SSH on 443 too:

```
Host github.com
  HostName ssh.github.com
  Port 443
  User git
```

### Why a separate signing key

Technically a login key can sign too: the signature formats differ, and one signature can't stand in for the other. A separate key is needed for other reasons:

- **You use your login key all the time**, often with a forwarded agent. The signing key endorses what reaches production hosts, and is best kept away from places it isn't needed.
- **They're deleted and restricted independently:** in the panel by environment, on servers by access.
- **GitHub** separates the roles anyway: a key has to be added separately as Authentication and as Signing.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `error: Load key "…": invalid format` or `incorrect passphrase` when committing | `user.signingkey` points to a file that doesn't exist, or the key isn't in the agent and the passphrase is wrong; check the path and `ssh-add -l` |
| `error: unsupported value for gpg.format: ssh` | git older than 2.34 |
| `No principal matched` in `git log --show-signature` | no `allowed_signers` (step 3); it doesn't affect deployments |
| *Unverified* on GitHub or in Gitea | the commit's `user.email` doesn't match a verified address of the account, or the key isn't added as Signing (GitHub) or verified through Verify (Gitea) |
| `Could not open a connection to your authentication agent` (Linux) | there's no agent, or the shell doesn't know `SSH_AUTH_SOCK`; see step 2, Linux |
| Windows: git asks for the key's passphrase every time even though the agent runs | git uses its own OpenSSH; run the two `git config` commands from "Windows: making git and the agent work together" |
| Deployment: "commit … is signed with key …, which no user has added" | the key isn't added in the container-forest panel (step 4) |
| Deployment: "… is limited to other environments" | the key is limited to other environments; change it under *Commit signing keys* |
| Deployment: "… may not deploy the stack" | you have no Editor role on the stack or the environment; a signature endorses only what you can deploy anyway |
| Deployment: "… does not descend from the deployed commit" | the branch was rewritten or the stack's source changed; deploy by hand from the panel |
