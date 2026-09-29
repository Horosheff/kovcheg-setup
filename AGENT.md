# Инструкция агенту

Человек арендовал свой VPS и прислал доступ. Нужно поднять на нём KovchegVPN и отдать ссылку на `.exe`. Чужие серверы, чужие IP и чужие ключи не подставлять: в этом репозитории их нет, и `server/install.sh` создаёт новые.

## Что попросить у человека, если ещё не прислал

- IP VPS, логин (`root`) и пароль или приватный SSH-ключ
- страна сервера для подписи в окне программы, три поля:
  - `KOVCHEG_COUNTRY` — «Финляндия»
  - `KOVCHEG_COUNTRY_ISO` — `FI`
  - `KOVCHEG_COUNTRY_BANNER` — `ФИНЛЯНДИЯ`

Не просить домен. Скрипт работает по голому IP.

## Сервер

Скопировать на VPS `server/install.sh` и выполнить от root. Пример для Финляндии:

```bash
KOVCHEG_COUNTRY=Финляндия \
KOVCHEG_COUNTRY_ISO=FI \
KOVCHEG_COUNTRY_BANNER=ФИНЛЯНДИЯ \
bash install.sh
```

Скрипт ставит Xray (VLESS Reality :443 и Vision :8443), Shadowsocks-2022 на портах 2096, 2053, 9443, 8444 и 2083, nginx на порту 80, ufw, fail2ban, ядра `xray.exe` / `sing-box.exe` / `wintun.dll` для Windows и, если скачается, Telegram-прокси mtg на порту 2087.

В конце он печатает блок между `===KOVCHEG_CLIENT_ENV===` и `===END_KOVCHEG_CLIENT_ENV===` и пишет тот же текст в `/root/kovcheg-client.env` (права `600`). Это вход для сборки клиента. Не коммитить и не вставлять в репозиторий.

Проверка на сервере:

```bash
systemctl is-active xray nginx
curl -fsS http://127.0.0.1/health
```

Оба юнита должны быть `active`, health отвечает `ok`.

## Клиент

На Windows с .NET 8 SDK и Python 3:

```powershell
git clone <этот репозиторий> kovcheg
cd kovcheg
# положить скачанный с VPS файл как client.env в корень
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
scp dist\KovchegVPN.exe root@IP:/var/www/html/KovchegVPN.exe
ssh root@IP bash /root/kovcheg/publish-release.sh 1.0.0 "первая сборка"
```

`build.ps1` сам восстанавливает иконки и голос из `assets/parts`, штампует ключи во временную копию и публикует `dist/KovchegVPN.exe`. Каталог `src` после сборки по-прежнему с заглушками `203.0.113.10` и `REPLACE_*`.

Ссылка человеку: `http://IP/KovchegVPN.exe`

Программа сама скачает ядра с `http://IP/bin/`, если на компьютере нет v2rayN. Запускать от администратора.

## Маршрутизация, которую нельзя перевернуть

YouTube в этой сборке идёт через VPN. Его нет в `HomeDirectSuffixes`, нет в прямом списке sing-box и нет в российском обходе системного прокси. Он есть в `DomainForceProxy` и в DNS-списке туннеля.

Так и должно остаться: у обычного провайдера YouTube режется, напрямую его не открыть. Не возвращайте `youtube.com`, `googlevideo.com`, `ytimg.com`, `ggpht.com`, `googleusercontent.com` в прямой обход.

Напрямую остаются только `.ru` / `.рф` / `.su`, VK, Яндекс и российские geosite-категории.

## Чего не делать

- Не вписывать в `Cfg.cs` IP или ключи от другого сервера.
- Не коммитить `client.env` и собранный exe.
- Не выключать проверку подписи обновлений: приватный ключ P-256 остаётся в `/root/kovcheg-sign/sign.key` на VPS, в клиент попадает только публичный `OTA_PUB`.
