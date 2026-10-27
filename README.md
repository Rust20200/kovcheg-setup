# KovchegVPN

Клиент Windows и установка своего VPS. В репозитории нет чужого IP, паролей и ключей: их создаёт `server/install.sh` на сервере, который вы сами арендовали.

Программа поднимает на компьютере туннель (нужны права администратора). Российские сайты идут напрямую, остальное — через ваш VPS.

## Куда идёт трафик

Через VPN:

- YouTube и его картинки (`youtube.com`, `youtu.be`, `googlevideo.com`, `ytimg.com`, `ggpht.com`, `googleusercontent.com`)
- ChatGPT, Grok, Instagram, Telegram и прочий зарубежный трафик

Мимо VPN, через домашний интернет:

- зоны `.ru`, `.рф`, `.su`
- VK, Яндекс и российские сайты из списка geosite (Госуслуги, банки, маркетплейсы)

DNS для YouTube и остальных зарубежных имён запрашивается через туннель (DoH на `1.1.1.1:443`), чтобы провайдер не подсовывал поддельные адреса. Для `.ru` / VK / Яндекса DNS берётся с домашнего роутера — так эти сайты открываются.

## Что делает человек

1. Арендует VPS с Ubuntu 22.04 или 24.04 (хватит 1 ГБ RAM и одного публичного IPv4). Это отдельная регистрация и оплата, не чей-то уже настроенный сервер.
2. Отдаёт агенту только данные этого нового сервера: IP, пользователь `root`, пароль или SSH-ключ, страну сервера словами («Финляндия», «Нидерланды», «Германия»).
3. На Windows ставит [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), если собирать программу будет этот же компьютер.
4. Скачивает ссылку `http://IP-СЕРВЕРА/KovchegVPN.exe`, которую пришлёт агент, и запускает её. Windows спросит права администратора — они нужны туннелю.

Дальше всё делает агент по [AGENT.md](AGENT.md).

## Что делает агент

Коротко: залить `server/install.sh` на VPS, выполнить его, забрать `/root/kovcheg-client.env`, на Windows выполнить `scripts/build.ps1`, залить `dist/KovchegVPN.exe` на сервер и подписать манифест. Готовая ссылка — `http://IP/KovchegVPN.exe`.

`client.env` в git не класть. В нём пароль Shadowsocks и ключи именно этого сервера.

## Сборка вручную

```powershell
# client.env лежит в корне и скачан с VPS
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

На Linux то же самое: `bash scripts/build.sh client.env` (нужен .NET 8 SDK). Сборка штампует ключи во временную копию исходников, дерево репозитория остаётся с заглушками.

## macOS

Клиент в `src/EcoplanVPN.Mac` (Avalonia) и общая логика в `src/EcoplanVPN.Core`. Windows-проект не менялся. Подключение то же: VLESS Reality, xHTTP :443 и Vision :8443, карточки «Нидерланды» и «Через Россию → …» из подписанного `servers.json`. YouTube идёт через VPN, зоны `.ru` / VK / Яндекс — напрямую.

```bash
# .NET 8 SDK. client.env — тот же файл, что для Windows (нужен OTA_PUB).
# Без него образ соберётся, но список серверов не пройдёт проверку подписи.
bash scripts/build-mac.sh client.env
```

Артефакт: `dist/EcoplanVPN-1.0.9.dmg` (Apple Silicon и Intel в одном .app). Проверка без боевых ключей: `bash scripts/build-mac.sh scripts/client.env.example` — заглушка, к рабочим серверам не подключится.

На Mac `hdiutil` собирает DMG. На Linux скрипт кросс-компилирует `osx-arm64` и `osx-x64` и упаковывает DMG через mozilla/libdmg-hfsplus. Для этого нужны `cmake`, `g++`, заголовки zlib/bzip2/lzma/openssl и `mkfs.hfsplus` (`hfsprogs`). Запуск и tun проверяются только на macOS.

Режим «Весь Mac» поднимает sing-box tun от администратора (пароль в диалоге macOS). Network Extension и подпись Apple не нужны. «Только прокси» включает системный SOCKS. Неподписанное приложение Gatekeeper открывает через правый клик → «Открыть» или `xattr -dr com.apple.quarantine`. Нотаризация (`notarytool`) возможна только с Apple Developer ID — в этой сборке её нет.

Обновление Mac — отдельный подписанный `latest-mac.json` (тот же конверт, что `latest.json`: строка `version\nurl\nsha256\nsize\n`). Файл кладётся рядом с зеркалами и на `http://147.45.48.35/dl/latest-mac.json`. Имя образа: `EcoplanVPN-<версия>.dmg`. Список IP по-прежнему берётся из `servers.json`. Ядра xray/sing-box для Darwin лежат внутри .app (версии в `scripts/mac-cores.lock`), windows-`bins.json` их не содержит.
