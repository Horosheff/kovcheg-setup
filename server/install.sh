#!/bin/bash
# KovchegVPN: чистый Ubuntu/Debian VPS → рабочий сервер.
# Сам придумывает UUID, ключ Reality, пароль Shadowsocks и ключ подписи обновлений.
# В конце печатает /root/kovcheg-client.env — его забирает сборка клиента.
set -euo pipefail

XRAY_VER="${XRAY_VER:-v26.7.28}"
SB_VER="${SB_VER:-1.13.19}"
DEST_HOST="${DEST_HOST:-www.intel.com}"
DEST="${DEST_HOST}:443"
SS_METHOD="2022-blake3-aes-128-gcm"

COUNTRY="${KOVCHEG_COUNTRY:-Сервер}"
COUNTRY_ISO="${KOVCHEG_COUNTRY_ISO:-VPS}"
COUNTRY_BANNER="${KOVCHEG_COUNTRY_BANNER:-VPN}"

[[ $EUID -eq 0 ]] || { echo "нужен root"; exit 1; }
export DEBIAN_FRONTEND=noninteractive

apt-get update
apt-get install -y curl ca-certificates jq openssl ufw fail2ban wget tar unzip nginx nginx-extras python3

cat >/etc/sysctl.d/99-kovcheg.conf <<'EOF'
net.core.default_qdisc=fq
net.ipv4.tcp_congestion_control=bbr
net.ipv6.conf.all.forwarding=1
EOF
sysctl --system >/dev/null || true

mkdir -p /var/www/html/logs /var/www/html/bin /var/www/html/geo /var/log/xray /root/kovcheg-sign /root/kovcheg
chown www-data:www-data /var/www/html/logs
touch /var/log/xray/access.log /var/log/xray/error.log
chown -R nobody:nogroup /var/log/xray
chmod 755 /var/log/xray /var/www/html/logs
chmod 644 /var/log/xray/access.log /var/log/xray/error.log

if [[ ! -f /root/kovcheg-sign/sign.key ]]; then
  openssl ecparam -name prime256v1 -genkey -noout -out /root/kovcheg-sign/sign.key
fi
chmod 600 /root/kovcheg-sign/sign.key
OTA_PUB=$(openssl ec -in /root/kovcheg-sign/sign.key -pubout -outform DER 2>/dev/null | base64 -w0)

UUID=$(cat /proc/sys/kernel/random/uuid)
SS_PASS=$(openssl rand -base64 16 | tr -d '\n')
SID=$(openssl rand -hex 8)
PATH_XHTTP=$(openssl rand -hex 4)

cat >/var/www/html/index.html <<'EOF'
<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><title>Sign in</title>
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
body{font-family:system-ui,sans-serif;background:#0f1419;color:#e7ecf1;margin:0}
main{max-width:22rem;margin:12vh auto;padding:2rem;background:#1a2330;border-radius:12px}
input,button{width:100%;box-sizing:border-box;margin:.4rem 0;padding:.7rem;border-radius:8px;border:1px solid #334}
button{background:#3d7ea6;color:#fff;border:0;cursor:pointer}
</style></head>
<body><main>
<h1>Account</h1>
<p>Corporate access. Unauthorized use is logged.</p>
<form method="post" action="/api/v1/authenticate">
<input name="user" placeholder="Email" autocomplete="username">
<input name="pass" type="password" placeholder="Password" autocomplete="current-password">
<button type="submit">Continue</button>
</form>
</main></body></html>
EOF

NGINX_SITE=/etc/nginx/sites-available/default
[[ -f $NGINX_SITE ]] || { mkdir -p /etc/nginx/conf.d; NGINX_SITE=/etc/nginx/conf.d/default.conf; }
cat >"$NGINX_SITE" <<'EOF'
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    server_name _;
    server_tokens off;
    root /var/www/html;
    index index.html;
    client_max_body_size 80m;
    location /api/v1/authenticate {
        default_type application/json;
        return 401 '{"success":false,"code":"ERR_AUTH_FAILED","message":"Authentication failed"}';
    }
    location /health { default_type text/plain; return 200 "ok\n"; }
    location /KovchegVPN.exe {
        types { } default_type application/octet-stream;
        add_header Content-Disposition "attachment; filename=KovchegVPN.exe";
        try_files /KovchegVPN.exe =404;
    }
    location /latest.json { try_files /latest.json =404; }
    location /bin/ {
        add_header Cache-Control "public, max-age=3600";
        try_files $uri =404;
    }
    location /geo/ {
        add_header Accept-Ranges bytes;
        add_header Cache-Control "public, max-age=86400";
        try_files $uri =404;
    }
    location /logs/ {
        alias /var/www/html/logs/;
        dav_methods PUT DELETE;
        create_full_put_path on;
        dav_access user:rw group:rw all:r;
        limit_except GET PUT HEAD { deny all; }
    }
    location /vlessws {
        proxy_pass http://127.0.0.1:10086;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_read_timeout 3600s;
    }
    location /xh {
        proxy_pass http://127.0.0.1:10087;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_read_timeout 3600s;
    }
    location / { try_files $uri $uri/ /index.html; }
}
EOF
systemctl enable --now nginx
nginx -t
systemctl reload nginx

bash -c "$(curl -fsSL https://github.com/XTLS/Xray-install/raw/main/install-release.sh)" @ install --version "$XRAY_VER"

keys=$(xray x25519)
PRIV=$(printf '%s\n' "$keys" | awk -F': ' '/Private[Kk]ey/{print $2; exit}' | tr -d ' \r')
PUB=$(printf '%s\n' "$keys" | awk -F': ' '/Password/{print $2; exit}' | tr -d ' \r')
if [[ -z "$PUB" ]]; then
  PUB=$(printf '%s\n' "$keys" | awk -F': ' '/Public[Kk]ey/{print $2; exit}' | tr -d ' \r')
fi
[[ -n "$PRIV" && -n "$PUB" ]] || { echo "не разобрал xray x25519"; echo "$keys"; exit 1; }

python3 - "$UUID" "$DEST" "$DEST_HOST" "$PRIV" "$SID" "$PATH_XHTTP" "$SS_METHOD" "$SS_PASS" <<'PY'
import json, sys
uuid, dest, dest_host, priv, sid, path, ss_method, ss_pass = sys.argv[1:]

def reality(extra=None):
    d = {
        "show": False,
        "dest": dest,
        "xver": 0,
        "serverNames": [dest_host],
        "privateKey": priv,
        "shortIds": ["", sid],
        "spiderX": "/",
        "minClientVer": "1.8.0",
    }
    if extra:
        d.update(extra)
    return d

def vless_clients(email, flow=None):
    c = {"id": uuid, "email": email}
    if flow:
        c["flow"] = flow
    return [c]

sniff = {"enabled": True, "destOverride": ["http", "tls", "quic"]}

def ss(tag, port):
    return {
        "tag": tag,
        "listen": "::",
        "port": port,
        "protocol": "shadowsocks",
        "settings": {"method": ss_method, "password": ss_pass, "network": "tcp,udp"},
    }

cfg = {
    "log": {"loglevel": "info", "access": "/var/log/xray/access.log", "error": "/var/log/xray/error.log"},
    "inbounds": [
        {
            "tag": "in-443",
            "listen": "0.0.0.0",
            "port": 443,
            "protocol": "vless",
            "settings": {"clients": vless_clients("kovcheg"), "decryption": "none"},
            "sniffing": sniff,
            "streamSettings": {
                "network": "xhttp",
                "security": "reality",
                "realitySettings": reality(),
                "xhttpSettings": {
                    "path": "/" + path,
                    "mode": "auto",
                    "extra": {"xPaddingBytes": "100-1000", "noSSEHeader": True},
                },
            },
        },
        {
            "tag": "in-4436",
            "listen": "::",
            "port": 443,
            "protocol": "vless",
            "settings": {"clients": vless_clients("kovcheg-v6"), "decryption": "none"},
            "sniffing": sniff,
            "streamSettings": {
                "network": "xhttp",
                "security": "reality",
                "realitySettings": reality(),
                "xhttpSettings": {
                    "path": "/" + path,
                    "mode": "auto",
                    "extra": {"xPaddingBytes": "100-1000", "noSSEHeader": True},
                },
            },
        },
        {
            "tag": "in-vision",
            "listen": "0.0.0.0",
            "port": 8443,
            "protocol": "vless",
            "settings": {"clients": vless_clients("kovcheg-vision", "xtls-rprx-vision"), "decryption": "none"},
            "sniffing": sniff,
            "streamSettings": {
                "network": "tcp",
                "security": "reality",
                "realitySettings": reality(),
            },
        },
        ss("in-ss", 2096),
        ss("in-ss-alt", 2053),
        ss("in-ss-hi", 9443),
        ss("in-ss-fresh", 8444),
        ss("in-ss-amd", 2083),
        {
            "tag": "in-xh",
            "listen": "127.0.0.1",
            "port": 10087,
            "protocol": "vless",
            "settings": {"clients": vless_clients("kovcheg-xh"), "decryption": "none"},
            "sniffing": sniff,
            "streamSettings": {
                "network": "xhttp",
                "xhttpSettings": {
                    "path": "/xh",
                    "mode": "auto",
                    "extra": {"xPaddingBytes": "100-1000"},
                },
            },
        },
        {
            "tag": "in-ws",
            "listen": "127.0.0.1",
            "port": 10086,
            "protocol": "vless",
            "settings": {"clients": vless_clients("kovcheg-ws"), "decryption": "none"},
            "sniffing": sniff,
            "streamSettings": {"network": "ws", "wsSettings": {"path": "/vlessws"}},
        },
    ],
    "outbounds": [
        {"tag": "direct", "protocol": "freedom"},
        {"tag": "block", "protocol": "blackhole"},
    ],
    "routing": {
        "domainStrategy": "IPIfNonMatch",
        "rules": [{"type": "field", "ip": ["geoip:private"], "outboundTag": "block"}],
    },
    "policy": {"levels": {"0": {"handshake": 8, "connIdle": 90, "uplinkOnly": 4, "downlinkOnly": 8}}},
}
json.dump(cfg, open("/usr/local/etc/xray/config.json", "w"), indent=2)
print("xray config written")
PY

xray_test() {
  xray run -test -c /usr/local/etc/xray/config.json >/tmp/xray-test.out 2>&1 \
    || xray run -test -config /usr/local/etc/xray/config.json >/tmp/xray-test.out 2>&1 \
    || xray -test -config /usr/local/etc/xray/config.json >/tmp/xray-test.out 2>&1
}

if ! xray_test; then
  python3 - <<'PY'
import json
p="/usr/local/etc/xray/config.json"
d=json.load(open(p))
for ib in d["inbounds"]:
    rs=(ib.get("streamSettings") or {}).get("realitySettings") or {}
    if "dest" in rs:
        rs["target"]=rs.pop("dest")
json.dump(d, open(p,"w"), indent=2)
print("retry with target instead of dest")
PY
  xray_test || { cat /tmp/xray-test.out; exit 1; }
fi

if ! systemctl restart xray; then
  python3 - <<'PY'
import json
p="/usr/local/etc/xray/config.json"
d=json.load(open(p))
d["inbounds"]=[ib for ib in d["inbounds"] if ib.get("tag")!="in-4436"]
json.dump(d, open(p,"w"), indent=2)
print("dropped in-4436")
PY
  systemctl restart xray
fi
systemctl enable xray
sleep 1
systemctl is-active xray

install_mtg() {
  local api url tmp
  api=$(curl -fsSL https://api.github.com/repos/9seconds/mtg/releases/latest)
  url=$(printf '%s' "$api" | python3 -c '
import json,sys
rel=json.load(sys.stdin)
for a in rel.get("assets") or []:
    n=a["name"]
    if n.endswith("linux-amd64.tar.gz") and "-v3" not in n:
        print(a["browser_download_url"])
        break
')
  [[ -n "$url" ]] || return 1
  tmp=$(mktemp -d)
  curl -fsSL -o "$tmp/mtg.tar.gz" "$url"
  tar -xzf "$tmp/mtg.tar.gz" -C "$tmp"
  install -m 755 "$(find "$tmp" -type f -name mtg | head -1)" /usr/local/bin/mtg
  rm -rf "$tmp"
  MTG_SECRET=$(/usr/local/bin/mtg generate-secret --hex www.intel.com | tr -d ' \r\n')
  [[ -n "$MTG_SECRET" ]] || return 1
  cat >/etc/systemd/system/kovcheg-mtg.service <<EOF
[Unit]
Description=Kovcheg Telegram MTProto
After=network-online.target
[Service]
ExecStart=/usr/local/bin/mtg simple-run 0.0.0.0:2087 ${MTG_SECRET}
Restart=on-failure
[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload
  systemctl enable --now kovcheg-mtg
}

MTG_SECRET=""
if ! install_mtg; then
  echo "mtg не встал — VPN без Telegram-прокси. Ссылку t.me/proxy можно не использовать."
  MTG_SECRET="disabled"
fi

ufw --force reset
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw allow 2053/tcp
ufw allow 2083/tcp
ufw allow 2087/tcp
ufw allow 2096/tcp
ufw allow 8443/tcp
ufw allow 8444/tcp
ufw allow 9443/tcp
ufw allow 2053/udp
ufw allow 2083/udp
ufw allow 2096/udp
ufw allow 8444/udp
ufw allow 9443/udp
ufw --force enable

cat >/etc/fail2ban/jail.d/sshd.local <<'EOF'
[sshd]
enabled = true
backend = systemd
maxretry = 6
bantime = 1h
findtime = 10m
EOF
systemctl enable --now fail2ban || true

V4=$(curl -4 -fsSL --max-time 12 https://ifconfig.me || true)
if [[ ! "$V4" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  V4=$(hostname -I | awk '{print $1}')
fi
V6=$(curl -6 -fsSL --max-time 8 https://ifconfig.me || true)
if [[ ! "$V6" =~ : ]]; then
  V6=$(ip -6 addr show scope global 2>/dev/null | awk '/inet6/{print $2}' | head -1 | cut -d/ -f1 || true)
fi
if [[ -z "$V6" ]]; then
  V6=none
  V6_PREFIX="2001:db8:ffff:"
else
  V6_PREFIX=$(python3 -c 'import sys; ip=sys.argv[1]; print((ip.split("::")[0]+"::") if "::" in ip else ":".join(ip.split(":")[:-1])+":")' "$V6")
fi

mkdir -p /var/www/html/geo
curl -fsSL -o /var/www/html/geo/geosite.dat \
  https://github.com/v2fly/domain-list-community/releases/latest/download/dlc.dat \
  || echo "geosite.dat не скачался — клиент попробует GitHub сам"
curl -fsSL -o /var/www/html/geo/geoip.dat \
  https://github.com/v2fly/geoip/releases/latest/download/geoip.dat \
  || echo "geoip.dat не скачался — клиент попробует GitHub сам"

fetch_bins() {
  local T DIR
  DIR=/var/www/html/bin
  mkdir -p "$DIR"
  T=$(mktemp -d)
  curl -fsSL -o "$T/xray.zip" "https://github.com/XTLS/Xray-core/releases/download/${XRAY_VER}/Xray-windows-64.zip"
  unzip -o -q "$T/xray.zip" -d "$T/xray"
  curl -fsSL -o "$T/sb.zip" "https://github.com/SagerNet/sing-box/releases/download/v${SB_VER}/sing-box-${SB_VER}-windows-amd64.zip"
  unzip -o -q "$T/sb.zip" -d "$T/sb"
  cp "$T/xray/xray.exe" "$T/xray/wintun.dll" "$DIR/"
  cp "$T/sb/sing-box-${SB_VER}-windows-amd64/sing-box.exe" "$DIR/"
  rm -rf "$T"
  python3 - "$DIR" "$V4" /root/kovcheg-sign/sign.key <<'PY'
import base64, hashlib, json, os, subprocess, sys
d, ip, key = sys.argv[1:]
entries = []
for name in ("xray.exe", "sing-box.exe", "wintun.dll"):
    p = os.path.join(d, name)
    data = open(p, "rb").read()
    sha = hashlib.sha256(data).hexdigest()
    size = len(data)
    url = f"http://{ip}/bin/{name}"
    payload = f"{name}\n{url}\n{sha}\n{size}\n"
    sig = subprocess.check_output(
        ["openssl", "dgst", "-sha256", "-sign", key], input=payload.encode()
    )
    entries.append({
        "version": name, "url": url, "sha256": sha, "size": size,
        "sig": base64.b64encode(sig).decode(),
    })
open(os.path.join(d, "bins.json"), "w").write(json.dumps(entries, indent=2) + "\n")
print("bins.json written")
PY
}
fetch_bins

cat >/root/kovcheg/publish-release.sh <<'EOS'
#!/bin/bash
# Подписать уже залитый /var/www/html/KovchegVPN.exe и положить latest.json.
#   bash /root/kovcheg/publish-release.sh 1.0.0 "что нового"
set -euo pipefail
VER="${1:?version, например 1.0.0}"
NOTES="${2:-}"
DIR=/var/www/html
EXE="$DIR/KovchegVPN.exe"
KEY=/root/kovcheg-sign/sign.key
ENV=/root/kovcheg-client.env
[[ -f $EXE ]] || { echo "нет $EXE — сначала залейте собранный клиент"; exit 1; }
[[ -f $KEY && -f $ENV ]] || { echo "нет ключа подписи или client.env"; exit 1; }
# shellcheck disable=SC1090
source "$ENV"
URL="http://${SERVER_IP}/KovchegVPN.exe"
SHA=$(sha256sum "$EXE" | cut -d' ' -f1)
SIZE=$(stat -c%s "$EXE")
SIG=$(printf '%s\n%s\n%s\n%s\n' "$VER" "$URL" "$SHA" "$SIZE" | openssl dgst -sha256 -sign "$KEY" | base64 -w0)
NOTES_JSON=$(printf '%s' "$NOTES" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read(), ensure_ascii=False))')
cat > "$DIR/latest.json.tmp" <<EOF
{
  "version": "$VER",
  "url": "$URL",
  "sha256": "$SHA",
  "size": $SIZE,
  "notes": $NOTES_JSON,
  "sig": "$SIG"
}
EOF
mv -f "$DIR/latest.json.tmp" "$DIR/latest.json"
echo "latest.json:"
cat "$DIR/latest.json"
echo
echo "скачивание: $URL"
EOS
chmod 755 /root/kovcheg/publish-release.sh

umask 077
cat >/root/kovcheg-client.env <<EOF
SERVER_IP=${V4}
SERVER_IP6=${V6}
SERVER_IP6_PREFIX=${V6_PREFIX}
UUID=${UUID}
SS_PASSWORD=${SS_PASS}
REALITY_PUB=${PUB}
REALITY_SID=${SID}
XHTTP_PATH=/${PATH_XHTTP}
MTG_SECRET=${MTG_SECRET}
OTA_PUB=${OTA_PUB}
COUNTRY=${COUNTRY}
COUNTRY_ISO=${COUNTRY_ISO}
COUNTRY_BANNER=${COUNTRY_BANNER}
EOF
chmod 600 /root/kovcheg-client.env

echo "===KOVCHEG_CLIENT_ENV==="
cat /root/kovcheg-client.env
echo "===END_KOVCHEG_CLIENT_ENV==="
echo "xray: $(systemctl is-active xray)  nginx: $(systemctl is-active nginx)"
curl -fsS http://127.0.0.1/health || true
echo
echo "Дальше: собрать клиент скриптом scripts/build.ps1 (Windows) и залить exe:"
echo "  scp KovchegVPN.exe root@${V4}:/var/www/html/KovchegVPN.exe"
echo "  ssh root@${V4} bash /root/kovcheg/publish-release.sh 1.0.0 \"первая сборка\""
echo "Ссылка для человека: http://${V4}/KovchegVPN.exe"
