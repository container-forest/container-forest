# Підпис комітів і git по SSH

Інструкція для всіх, хто працює з репозиторіями, з яких деплоїть container-forest. Тут описано налаштування на Linux, macOS і Windows для Gitea й GitHub.

## Навіщо

Стек, у якого ввімкнено «Деплоїти лише підписані коміти», деплоїть лише коміт, підписаний ключем людини з правом деплою цього стеку ([ADR-0011](adr/0011-signed-commits.md), [«Засвідчення вмісту»](architecture.md#засвідчення-вмісту-підписи-комітів)). Без підпису право push у гілку фактично дорівнює root на хості й доступу до секретів середовища, у тому числі для зламаного git-сервера. Непідписаний коміт можна задеплоїти лише вручну, підтвердивши його SHA в панелі.

Домовленість команди:

- **Окремий ключ підпису на кожен пристрій.** Один пристрій — один ключ. Загубили ноутбук — видаляєте лише його ключ.
- **Ключ підпису не використовується для входу по SSH**, а ключі для входу — для підпису. Детальніше — у розділі [«Чому окремий ключ»](#чому-окремий-ключ-підпису).
- **Git працює по SSH, а не HTTPS.** Чому — у розділі [«Git по SSH, а не HTTPS»](#git-по-ssh-а-не-https).

Потрібно: git 2.34 або новіший (`git --version`), OpenSSH 8.2 або новіший (`ssh -V`), для апаратного ключа — 8.9 або новіший.

## 1. Згенерувати ключ підпису

У командах нижче `<пристрій>` — назва пристрою, наприклад `laptop`; вона потрапить у коментар ключа й допоможе впізнати його в списках.

### Linux і macOS

```bash
ssh-keygen -t ed25519 -C "<ваше ім'я>@<пристрій> (commit signing)" -f ~/.ssh/id_commit_signing
```

Задайте пароль, коли `ssh-keygen` попросить: він захищає приватний ключ, якщо файл вкрадуть.

### Windows (PowerShell)

У Windows 10 і 11 OpenSSH уже є (`C:\Windows\System32\OpenSSH`). Якщо `ssh -V` показує версію, старішу за 8.2, оновіть OpenSSH (наприклад, `winget install Microsoft.OpenSSH.Preview`).

```powershell
ssh-keygen -t ed25519 -C "<ваше ім'я>@<пристрій> (commit signing)" -f $HOME\.ssh\id_commit_signing
```

### Апаратний ключ (YubiKey, інший FIDO2) — рекомендовано

Приватна частина лишається на пристрої: її неможливо скопіювати, а кожен підпис вимагає дотику.

```bash
ssh-keygen -t ed25519-sk -O resident -O verify-required -C "<ваше ім'я>@<пристрій> (commit signing)" -f ~/.ssh/id_commit_signing_sk
```

- **Linux:** потрібна бібліотека `libfido2` (Debian і Ubuntu: `sudo apt install libfido2-1`).
- **macOS:** системний OpenSSH від Apple зібраний без підтримки FIDO. Поставте OpenSSH із Homebrew (`brew install openssh`) і користуйтеся ним (`/opt/homebrew/bin/ssh-keygen`).
- **Windows:** OpenSSH 8.9 і новіший працює з FIDO через Windows Hello; у PowerShell замініть `~/.ssh/…` на `$HOME\.ssh\…`.

Далі в інструкції для апаратного ключа скрізь замість `id_commit_signing` пишіть `id_commit_signing_sk`.

## 2. Додати ключ в агент

Щоб пароль не питався на кожен коміт.

### Linux

```bash
ssh-add ~/.ssh/id_commit_signing
```

GNOME і KDE запускають агент самі. Окремі композитори Wayland (Hyprland, Sway, niri), віконні менеджери X11 (i3, bspwm) і сесії без графіки — ні, і тоді `ssh-add` відповідає `Could not open a connection to your authentication agent`: змінна `SSH_AUTH_SOCK` не задана, бо агента немає. Є три варіанти.

**Агент як служба systemd користувача** (рекомендовано): працює в усіх терміналах і програмах сесії, доки ви залогінені. Створіть `~/.config/systemd/user/ssh-agent.service`:

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

Увімкніть службу:

```bash
systemctl --user enable --now ssh-agent
```

Скажіть shell, де агент: додайте в `~/.bashrc` або `~/.zshrc` рядок нижче й відкрийте новий термінал.

```bash
export SSH_AUTH_SOCK="$XDG_RUNTIME_DIR/ssh-agent.socket"
```

Щоб агент бачили й програми, запущені не з терміналу (IDE, git-клієнти), задайте ту саму змінну для всієї сесії. Спосіб, що не залежить від композитора, — файл `~/.config/environment.d/ssh-agent.conf`:

```
SSH_AUTH_SOCK=${XDG_RUNTIME_DIR}/ssh-agent.socket
```

Його читає менеджер користувача systemd, тож змінну отримують служби користувача й сесії, запущені через systemd. Змінна з'явиться після повторного входу. Якщо композитор запускається інакше, задайте `SSH_AUTH_SOCK` у його власній конфігурації.

**Hyprland.** Обирайте один із варіантів залежно від того, як його запущено:

- **через `uwsm`** (так радить wiki Hyprland) — рядок у `~/.config/uwsm/env`; змінні сесії wiki радить тримати саме там, а не в конфігурації Hyprland:

  ```
  export SSH_AUTH_SOCK="$XDG_RUNTIME_DIR/ssh-agent.socket"
  ```

- **без `uwsm`, конфігурація на Lua** (`~/.config/hypr/hyprland.lua`, у нових версіях) — `hl.env`; на чужі змінні посилаються через `os.getenv`, а не `$`:

  ```lua
  hl.env("SSH_AUTH_SOCK", os.getenv("XDG_RUNTIME_DIR") .. "/ssh-agent.socket")
  ```

- **без `uwsm`, старіша конфігурація** (`~/.config/hypr/hyprland.conf`):

  ```
  env = SSH_AUTH_SOCK,$XDG_RUNTIME_DIR/ssh-agent.socket
  ```

Синтаксис — за [wiki Hyprland](https://wiki.hypr.land/Configuring/Advanced-and-Cool/Environment-variables/). Змінна діє після перезапуску сесії.

Деякі дистрибутиви вже мають таку службу в пакеті `openssh` (`systemctl --user cat ssh-agent` її покаже). Тоді власний файл не потрібен, але перевірте, який шлях сокета вона задає, і вкажіть у `SSH_AUTH_SOCK` саме його.

Якщо додати в `~/.ssh/config` рядки нижче, ключ потрапить в агент сам під час першого використання, і `ssh-add` викликати не доведеться:

```
Host *
  AddKeysToAgent yes
```

**Агент лише для одного терміналу:** `eval "$(ssh-agent -s)"`, потім `ssh-add`. Інші термінали й IDE цього агента не бачать, і після закриття терміналу він зникає.

**Без агента:** нічого не налаштовувати. Тоді git запитає пароль ключа на кожен коміт. З апаратним ключем (`ed25519-sk`) агент не обов'язковий: підпис і так потребує дотику, а пароль на сам файл ключа зазвичай не задають.

### macOS

```bash
ssh-add --apple-use-keychain ~/.ssh/id_commit_signing
```

Щоб пароль брався з Keychain після перезавантаження, додайте в `~/.ssh/config`:

```
Host *
  UseKeychain yes
  AddKeysToAgent yes
```

### Windows

Службу агента треба один раз увімкнути в PowerShell **від адміністратора**:

```powershell
Get-Service ssh-agent | Set-Service -StartupType Automatic
```

```powershell
Start-Service ssh-agent
```

Потім у звичайному PowerShell:

```powershell
ssh-add $HOME\.ssh\id_commit_signing
```

**Не пересилайте агент на хости, яким не довіряєте** (`ForwardAgent yes`, `ssh -A`): поки сесія відкрита, root на такому хості може підписувати коміти вашим ключем. Якщо пересилати агент усе ж треба, додавайте ключ підпису з підтвердженням кожного використання: `ssh-add -c` (Linux, macOS).

## 3. Налаштувати git

Однаково для всіх систем:

```bash
git config --global gpg.format ssh
```

```bash
git config --global user.signingkey ~/.ssh/id_commit_signing.pub
```

Шлях до приватного ключа (`~/.ssh/id_commit_signing`) теж працює. Публічний універсальніший: git передає його `ssh-keygen`, а той бере приватний ключ з агента, якщо він там є, інакше — з файлу поруч без `.pub`. Ключ без файлу на диску, як-от у 1Password чи KeePassXC, можна вказати лише публічним.

```bash
git config --global commit.gpgsign true
```

```bash
git config --global tag.gpgsign true
```

`user.email` має збігатися з підтвердженою адресою вашого облікового запису на GitHub і в Gitea, інакше вони не покажуть «Verified»:

```bash
git config --global user.email
```

### Windows: щоб git і агент працювали в парі

Git for Windows має власний OpenSSH, який не бачить служби `ssh-agent` Windows. Вкажіть git системний OpenSSH:

```powershell
git config --global gpg.ssh.program "C:/Windows/System32/OpenSSH/ssh-keygen.exe"
```

```powershell
git config --global core.sshCommand "C:/Windows/System32/OpenSSH/ssh.exe"
```

У PowerShell шлях до ключа задавайте явно: `git config --global user.signingkey "$HOME/.ssh/id_commit_signing.pub"`.

### Перевірка підписів локально (необов'язково)

Щоб `git log --show-signature` писав «Good signature», git має знати, яким ключам довіряти:

```bash
echo "$(git config user.email) namespaces=\"git\" $(cat ~/.ssh/id_commit_signing.pub)" >> ~/.ssh/allowed_signers
```

```bash
git config --global gpg.ssh.allowedSignersFile ~/.ssh/allowed_signers
```

У PowerShell перший крок — `Add-Content $HOME\.ssh\allowed_signers "$(git config user.email) namespaces=`"git`" $(Get-Content $HOME\.ssh\id_commit_signing.pub)"`. Для container-forest цей файл не потрібен: ЦА перевіряє підпис ключами з панелі.

## 4. Додати ключ у container-forest

Скопіюйте публічний ключ: `cat ~/.ssh/id_commit_signing.pub` (Windows: `Get-Content $HOME\.ssh\id_commit_signing.pub`).

Панель → «Обліковий запис» → «Ключі підпису комітів» → «Додати ключ»: назва (пристрій), публічний ключ, середовища.

- **Середовища.** Ключ засвідчує деплой стеку, лише якщо ви самі можете деплоїти цей стек. Обмеження середовищами звужує ще більше: ключ на пристрої, якому ви довіряєте менше (робочий комп'ютер, спільна машина), обмежте середовищами того самого рівня довіри.
- **Через API-токен чи CLI** свій ключ додати не можна, лише із сесії в панелі.
- **Ключ службового користувача** (наприклад, `ci`) додає адміністратор: `forest user signing-key add ci runner ./ci.pub -e <середовище>`.

## 5. Додати ключ у GitHub і Gitea

Ці сервіси показують «Verified» на комітах для зручності рев'ю. Для container-forest це нічого не означає: ЦА перевіряє підпис сама.

### GitHub

Settings → **SSH and GPG keys** → **New SSH key**, у полі **Key type** — **Signing Key**, у **Key** — вміст `.pub`.

Ключ типу Authentication коміти не верифікує, а ключ типу Signing не дає входу по SSH. Це якраз те розділення, якого ми хочемо.

### Gitea

Settings → **SSH / GPG Keys** → блок **Manage SSH Keys** → **Add Key**: назва й вміст `.pub`. Після збереження біля ключа натисніть **Verify**: Gitea покаже токен, який треба підписати цим ключем.

Linux і macOS:

```bash
echo -n '<токен>' | ssh-keygen -Y sign -n gitea -f ~/.ssh/id_commit_signing
```

Windows (PowerShell; токен записується у файл без переводу рядка, а підпис з'являється поруч у `token.txt.sig`):

```powershell
Set-Content -NoNewline -Path token.txt -Value '<токен>'
```

```powershell
ssh-keygen -Y sign -n gitea -f $HOME\.ssh\id_commit_signing token.txt
```

Вставте підпис від `-----BEGIN SSH SIGNATURE-----` до `-----END SSH SIGNATURE-----` у форму. Якщо кнопки **Verify** немає, адміністратор інстансу вимкнув перевірку, і ключ вважається підтвердженим за email.

⚠️ Gitea не розділяє ключі для входу й для підпису: доданий ключ підпису зможе ще й пушити по SSH з вашими правами. Для вашого власного ключа це нормально, але на ключ службового користувача це варто зважати.

## 6. Перевірити

```bash
git commit --allow-empty -m "test: signed commit"
```

```bash
git log --show-signature -1
```

Після push GitHub і Gitea показують «Verified» біля коміту. Для стеку з вимогою підпису історія деплоїв покаже «Підписав <ім'я>», а на екрані деплою — відбиток ключа.

## Як працювати далі

- **Звичайні коміти** підписуються автоматично (`commit.gpgsign true`), так само `git commit --amend` і `git rebase`: вони створюють нові коміти й підписують їх.
- **Злиття в гілку, з якої деплоїться стек**, робіть локально: `git merge -S` або fast-forward на коміт, який підписав автор із правом деплою. Merge кнопкою у веб-інтерфейсі Gitea чи GitHub підписує ключ платформи, і такий коміт деплоїться лише після підтвердження SHA в панелі (екран деплою → «Підтвердити й задеплоїти») або `forest stack deploy <стек> --approve <sha>`.
- **Підпис голови гілки засвідчує весь стан репозиторію**, включно з непідписаними комітами під ним. Не підписуйте коміт поверх чужих змін, яких не переглянули.
- **Force-push** у гілку стеку зупиняє деплой після push і хук CI: новий коміт має бути нащадком розгорнутого. Такий коміт деплоїться лише вручну.

## Новий пристрій, загублений пристрій

- **Новий пристрій:** новий ключ (кроки 1–6), старі не копіюйте.
- **Загублений або скомпрометований:** видаліть його ключ підпису в панелі («Обліковий запис» → «Ключі підпису комітів»), у GitHub і в Gitea, а також ключі входу по SSH цього пристрою. Видалений із панелі ключ одразу перестає засвідчувати деплої, зокрема для вже запушених комітів.

## Git по SSH, а не HTTPS

Для роботи з Gitea й GitHub користуйтеся адресами `git@…:…`, а не `https://…`. Ключі для входу — свої, окремі від ключа підпису.

### Чому

- **Немає паролів і токенів на диску.** Для HTTPS потрібен токен доступу (пароль GitHub уже не приймає), і git зберігає його в credential helper, а часто й відкритим текстом у `~/.git-credentials`. Такий токен зазвичай має доступ до всіх ваших репозиторіїв, а не лише до одного, і витікає разом із файлом. По SSH сервер перевіряє підпис запиту, а приватний ключ із пристрою не виходить.
- **Апаратні ключі.** Ключ входу теж може бути на FIDO-ключі (`ed25519-sk`): без дотику ніхто не запушить від вашого імені, навіть маючи доступ до ноутбука. Для токена HTTPS такого способу немає.
- **Ключ на пристрій і окреме відкликання.** Загубили ноутбук — видаляєте один ключ, решта пристроїв працює далі. Спільний токен довелося б перевипустити й прописати знову скрізь.
- **Немає строку дії й ротації.** Токени HTTPS спливають, і push раптом перестає працювати. Ключ SSH діє, доки ви його не видалите.
- **Захист від фішингу.** Ключ SSH нічого не відправляє на сервер, тож сторінка, що вдає Gitea, нічого не вкраде. Токен можна вставити не туди.
- **Перевірка сервера.** SSH запам'ятовує ключ хоста (`known_hosts`) і попереджає, якщо він змінився: підмінений Gitea помітно одразу.
- **Один агент.** Той самий `ssh-agent` дає ключі і для git, і для підпису, тож пароль вводиться раз за сесію.

### Як перейти

1. Ключ для входу (окремо від ключа підпису), по одному на сервіс або на пристрій:

```bash
ssh-keygen -t ed25519 -C "<ваше ім'я>@<пристрій> (gitea)" -f ~/.ssh/id_ed25519_gitea
```

2. Додати `.pub`: GitHub → Settings → SSH and GPG keys → **New SSH key**, тип **Authentication Key**; Gitea → Settings → SSH / GPG Keys → **Add Key**.

3. Указати, який ключ для якого сервера, у `~/.ssh/config` (Windows: `$HOME\.ssh\config`). Порт Gitea дізнайтеся в адміністратора: часто це не 22.

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

`IdentitiesOnly yes` не дає SSH пропонувати серверу всі ключі з агента, зокрема й ключ підпису.

4. Перевірити й переключити наявні клони:

```bash
ssh -T git@gitea.example.com
```

```bash
git remote set-url origin git@gitea.example.com:<організація>/<репозиторій>.git
```

Під час першого підключення SSH покаже відбиток ключа хоста. Звірте його з тим, що дає адміністратор Gitea або [документація GitHub](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/githubs-ssh-key-fingerprints), і лише тоді відповідайте `yes`.

Якщо мережа блокує порт 22, GitHub приймає SSH і на 443:

```
Host github.com
  HostName ssh.github.com
  Port 443
  User git
```

### Чому окремий ключ підпису

Технічно ключем входу можна й підписувати: формати підпису різні, і один підпис не підставиш замість іншого. Окремий ключ потрібен з інших причин:

- **Ключ входу ви використовуєте постійно**, часто з пересланим агентом. Ключ підпису засвідчує, що потрапить на продакшн-хости, і його краще не пускати туди, де він не потрібен.
- **Їх незалежно видаляють і обмежують:** у панелі — середовищами, на серверах — доступом.
- **GitHub** однаково розділяє ці ролі: ключ треба додавати окремо як Authentication і як Signing.

## Якщо щось не так

| Симптом | Причина |
|---|---|
| `error: Load key "…": invalid format` або `incorrect passphrase` при коміті | `user.signingkey` вказує на неіснуючий файл або ключа немає в агенті й пароль неправильний; перевірте шлях і `ssh-add -l` |
| `error: unsupported value for gpg.format: ssh` | git старіший за 2.34 |
| `No principal matched` у `git log --show-signature` | немає `allowed_signers` (крок 3); на деплой це не впливає |
| «Unverified» на GitHub чи в Gitea | `user.email` коміту не збігається з підтвердженою адресою облікового запису або ключ не доданий як Signing (GitHub) чи не підтверджений через Verify (Gitea) |
| `Could not open a connection to your authentication agent` (Linux) | агента немає або shell не знає `SSH_AUTH_SOCK`; див. крок 2, розділ Linux |
| Windows: git питає пароль ключа щоразу, хоча агент запущено | git користується власним OpenSSH; виконайте два `git config` з розділу «Windows: щоб git і агент працювали в парі» |
| Деплой: «commit … is signed with key …, which no user has added» | ключ не доданий у панель container-forest (крок 4) |
| Деплой: «… is limited to other environments» | ключ обмежено іншими середовищами; змініть його в «Ключах підпису комітів» |
| Деплой: «… may not deploy the stack» | у вас немає ролі Editor на стеку чи середовищі; підпис засвідчує лише те, що ви й так можете деплоїти |
| Деплой: «… does not descend from the deployed commit» | гілку переписано або змінено джерело стеку; задеплойте вручну з панелі |
