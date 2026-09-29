using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KovchegVPN;

    // Свой xray 26.7.28 на 10818/10819. Сплит-маршрутизация (РФ напрямую)
    // живёт внутри своего ядра на http-inbound.
public static class Core
{
    private static Process? _own;

    // geosite-теги v2fly (тот же источник, что у lib4u/amnezia-tunneling-ru):
    // всё российское — напрямую. YouTube не здесь: он в DomainForceProxy.
    private static readonly string[] GeoFull =
    {
        "category-ru", "category-bank-ru", "category-gov-ru", "category-media-ru",
        "category-ecommerce-ru", "category-retail-ru", "category-entertainment-ru",
        "category-travel-ru", "yandex", "vk", "mailru", "mailru-group", "ok", "dzen",
        "kinopoisk", "okko", "wink", "rutube", "ozon", "wildberries", "avito", "x5",
        "aviasales", "2gis", "mosmetro", "kaspersky", "drweb"
    };
    private static readonly string[] GeoCore =
    {
        "category-ru", "category-bank-ru", "category-gov-ru", "yandex", "vk", "mailru"
    };
    private static readonly string[] DomainAlways =
    {
        "domain:ru", "domain:xn--p1ai", "domain:su",
        "domain:vk.com", "domain:vk.me", "domain:vk.cc", "domain:mvk.com",
        "domain:userapi.com", "domain:vk-cdn.net", "domain:vk-portal.net",
        "domain:vkuseraudio.net", "domain:vkuservideo.net", "domain:vkuser.net",
        "domain:yandex.net", "domain:yandex.com", "domain:yastatic.net",
        "domain:mycdn.me", "domain:ozonstatic.net", "domain:wstatic.net",
        "domain:2gis.com",
    };
    // Сплит/geosite не должны утащить их в РФ-direct. Instagram и Cursor IDE —
    // отдельный SS без mux: HTTP/2 иначе ловит CANCEL. Telegram — другой
    // outbound с mux, иначе с дома сотни TCP и LigaLink душит все сайты.
    private static readonly string[] DomainForceProxy =
    {
        "domain:instagram.com", "domain:cdninstagram.com", "domain:ig.me",
        "domain:facebook.com", "domain:facebook.net", "domain:fbcdn.net",
        "domain:fbsbx.com", "domain:meta.com",
        "domain:cursor.sh", "domain:cursor.com", "domain:cursorapi.com",
        "domain:cursorvm.com", "domain:cursor-cdn.com",
        "domain:anysphere.co", "domain:anysphere.com",
        "domain:grok.com", "domain:x.ai",
        "domain:livekit.cloud", "domain:livekit.io",
        "domain:openai.com", "domain:chatgpt.com",
        "domain:oaistatic.com", "domain:oaiusercontent.com",
        "domain:youtube.com", "domain:youtu.be", "domain:googlevideo.com",
        "domain:ytimg.com", "domain:ggpht.com", "domain:youtube-nocookie.com",
        "domain:youtubei.googleapis.com", "full:youtube.googleapis.com",
        "domain:googleusercontent.com",
        // Охота ядра (icanhazip) иначе идёт в default mux и LigaLink её вешает.
        "domain:icanhazip.com", "domain:ipify.org",
        "full:checkip.amazonaws.com",
    };
    private static readonly string[] DomainTelegram =
    {
        "domain:telegram.org", "domain:t.me", "domain:telegram.me",
        "domain:cdn-telegram.org",
    };

PLACEHOLDER_INCOMPLETE
}
