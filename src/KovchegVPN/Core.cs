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
    private static readonly string[] DomainForceProxy =
    {
        "domain:youtube.com", "domain:youtu.be", "domain:googlevideo.com",
    };
}
