using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ForgePanel;

// Ответ проверки: адрес, который увидел интернет, сервис, который на него ответил, и причина
// неудачи, если адрес получить не удалось.
public sealed record CgnatProbeResult(string Address, string Host, string Error)
{
    public bool Ok => Error.Length == 0;
}

// Один GET наружу, и только по нажатию кнопки: «какой адрес видит интернет». Карточка друга
// остаётся локальной — в этом запросе нет ни имени сборки, ни порта, ни путей, только сам факт
// соединения. Нужен он для одного вопроса: имеет ли смысл проброс порта. Если провайдер даёт
// CGNAT, публичного адреса у машины нет вовсе, и любое правило в роутере повиснет в пустоте.
public static class CgnatProbe
{
    // Две независимые точки (ipify и Cloudflare) — не опрос половины интернета, а страховка:
    // один сервис легко закрыт hosts-файлом или блокировщиком, и тогда человек увидел бы «сети нет».
    private static readonly (string Url, string Host)[] Endpoints =
    {
        ("https://api.ipify.org", "api.ipify.org"),
        ("https://icanhazip.com", "icanhazip.com")
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Сетевые неудачи превращаются в текст результата, а не в исключение: ждёт этот метод
    // async-обработчик кнопки, где необработанное исключение стоило бы всей панели, а не одной
    // проверке. Отмена окна тоже становится обычным «не получилось».
    public static async Task<CgnatProbeResult> RunAsync(CancellationToken ct)
    {
        string lastError = "Интернет не ответил.";

        foreach ((string url, string host) in Endpoints)
        {
            try
            {
                string answer = (await Http.GetStringAsync(url, ct)).Trim();
                if (!IPAddress.TryParse(answer, out IPAddress? parsed) ||
                    parsed.AddressFamily != AddressFamily.InterNetwork)
                {
                    lastError = $"{host} вместо адреса вернул «{Cut(answer)}».";
                    continue;
                }

                return new CgnatProbeResult(parsed.ToString(), host, "");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) break;
                lastError = $"{host}: {Cut(ex.Message)}";
            }
        }

        return new CgnatProbeResult("", "", lastError);
    }

    // Ошибки сетки бывают в одну длинную строку, а место под результат — узкая колонка.
    private static string Cut(string text) => text.Length <= 160 ? text : text[..160] + "…";
}
