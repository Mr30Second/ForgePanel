using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForgePanel;

// Иконки мода грузятся отдельно и кэшируются на время сессии: сетка каталога перестраивается
// при каждом изменении размера окна, и дёргать CDN по второму разу незачем.
public static class RemoteImage
{
    private const long MaxBytes = 4_000_000;

    public readonly record struct Result(ImageSource? Image, string? Error);

    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, Result> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // Картинки лежат на том же CDN, что и API Modrinth, а он отрезает запросы без User-Agent:
        // без этой строки иконки молча падали с 403.
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "ForgePanel/2.0 (Minecraft server manager for Windows)");
        return client;
    }

    public static async Task<Result> GetAsync(string? url)
    {
        if (!TryHttpUrl(url, out string address)) return new Result(null, null);

        lock (Cache)
        {
            if (Cache.TryGetValue(address, out Result known)) return known;
        }

        Result loaded;
        try
        {
            using var response = await Http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync();

            if (bytes.LongLength > MaxBytes)
            {
                loaded = new Result(null, "Картинка больше 4 МБ — не качаем.");
            }
            else
            {
                ImageSource? image = Decode(bytes, out _);
                string? skiaWhy = null;
                if (image == null)
                {
                    (image, skiaWhy) = DecodeWithSkia(bytes);
                }
                loaded = image != null
                    ? new Result(image, null)
                    : new Result(null, bytes.Length == 0
                        ? "CDN вернул пустой ответ."
                        : Describe(bytes, mediaType, address) + (skiaWhy == null ? "" : $". Skia: {skiaWhy}"));
            }
        }
        catch (TaskCanceledException)
        {
            loaded = new Result(null, "CDN не ответил за 15 секунд.");
        }
        catch (Exception ex)
        {
            loaded = new Result(null, "Иконка не загрузилась: " + ex.Message);
        }

        lock (Cache)
        {
            Cache[address] = loaded;
        }
        return loaded;
    }

    // Второй шанс для форматов, которых нет у WPF: Modrinth отдаёт большинство иконок в WebP
    // (проверено живым запросом — Content-Type image/webp, сигнатура RIFF).
    // Главный путь — пиксели Skia напрямую в BitmapSource; PNG-перекодировка остаётся запасным
    // ходом для экзотических цветовых форматов, которых у декодированных иконок не бывает.
    // Второе значение — почему не вышло: без него отказ выглядел как «просто заглушка».
    private static (ImageSource? Image, string? Why) DecodeWithSkia(byte[] bytes)
    {
        if (bytes.Length == 0) return (null, null);

        try
        {
            // var, а не SKBitmap?: в этом файле нет using SkiaSharp, и имя без префикса
            // компилятор не найдёт даже при подключённом пакете.
            using var bitmap = SkiaSharp.SKBitmap.Decode(bytes);
            if (bitmap is null) return (null, "поток не распознан");

            ImageSource? pixels = FromSkia(bitmap);
            if (pixels != null) return (pixels, null);

            using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            if (png is null) return (null, "не удалось перекодировать в PNG");

            byte[] pngBytes = png.ToArray();
            string kind = bitmap.Width + "x" + bitmap.Height + ", " + bitmap.ColorType;
            if (pngBytes.Length == 0) return (null, "Skia вернула пустой PNG (" + kind + ")");

            ImageSource? converted = Decode(pngBytes, out string? why);
            return converted is null
                ? (null, $"{pngBytes.Length} байт PNG, {kind}: {why}")
                : (converted, null);
        }
        catch (Exception ex)
        {
            // Сюда же попадает отсутствующая нативная библиотека Skia (DllNotFoundException):
            // в карточке остаётся заглушка с буквой, а не падение всей сетки каталога.
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    // Пиксели из Skia в обход всех декодеров WPF. После декодирования иконки у SKBitmap всегда
    // 4 байта на пиксель, и остаётся лишь угадать порядок каналов: Rgba8888 меняем R и B местами,
    // Bgra8888 ложится в PixelFormats.Bgra32 как есть. Если Skia отдала канал с уже
    // перемноженным альфа, просим у WPF Pbgra32 — иначе полупрозрачные края светлятся.
    private static ImageSource? FromSkia(SkiaSharp.SKBitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int rowBytes = bitmap.RowBytes;
        if (width <= 0 || height <= 0 || rowBytes <= 0) return null;
        if (bitmap.ColorType is not (SkiaSharp.SKColorType.Rgba8888 or SkiaSharp.SKColorType.Bgra8888)) return null;

        IntPtr source = bitmap.GetPixels();
        if (source == IntPtr.Zero) return null;

        bool sourceIsBgra = bitmap.ColorType == SkiaSharp.SKColorType.Bgra8888;
        int stride = width * 4;
        var raw = new byte[rowBytes * height];
        System.Runtime.InteropServices.Marshal.Copy(source, raw, 0, raw.Length);

        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int from = y * rowBytes;
            int to = y * stride;
            for (int x = 0; x < width; x++)
            {
                int s = from + x * 4;
                int d = to + x * 4;
                byte first = raw[s];
                byte third = raw[s + 2];
                pixels[d] = sourceIsBgra ? first : third;
                pixels[d + 1] = raw[s + 1];
                pixels[d + 2] = sourceIsBgra ? third : first;
                pixels[d + 3] = raw[s + 3];
            }
        }

        PixelFormat format = bitmap.AlphaType == SkiaSharp.SKAlphaType.Premul
            ? PixelFormats.Pbgra32
            : PixelFormats.Bgra32;
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
        image.Freeze();
        return image;
    }

    // Если декодер отказал, по первым байтам видно, что реально прилетело: WPF читает только
    // PNG/JPEG/GIF/BMP/TIFF/ICO, а CDN отдают и WebP, и AVIF, и страницы ошибок под видом 200 OK.
    // Подсказка попадает в тултип заглушки, и по ней понятно, нужна ли поддержка нового формата.
    private static string Describe(byte[] bytes, string? mediaType, string address)
    {
        string kind = Kind(bytes);
        bool readable = kind is "PNG" or "JPEG" or "GIF" or "BMP" or "TIFF" or "ICO";
        string head = bytes.Length >= 4 ? Convert.ToHexString(bytes, 0, 4) : Convert.ToHexString(bytes);
        string what = readable
            ? $"похоже на {kind}, но декодер не прочитал (файл оборван или битые данные)"
            : $"{kind} — WPF такое не декодирует";

        return $"CDN вернул {what}. Content-Type «{mediaType ?? "отсутствует"}», {bytes.Length} байт, " +
               $"начало {head}, адрес {Shorten(address)}";
    }

    // В тултипе адрес целиком не помещается, а домен и расширение — как раз то, что нужно увидеть.
    private static string Shorten(string address)
    {
        int slash = address.IndexOf('/', "https://".Length);
        return slash < 0 ? address : address[..Math.Min(address.Length, slash)] + "…" + Path.GetFileName(address);
    }

    private static string Kind(byte[] bytes)
    {
        if (StartsWith(bytes, 0x89, 0x50, 0x4E, 0x47)) return "PNG";
        if (StartsWith(bytes, 0xFF, 0xD8, 0xFF)) return "JPEG";
        if (StartsWith(bytes, 0x47, 0x49, 0x46, 0x38)) return "GIF";
        if (StartsWith(bytes, 0x42, 0x4D)) return "BMP";
        if (StartsWith(bytes, 0x49, 0x49, 0x52, 0x4F) || StartsWith(bytes, 0x4D, 0x4D, 0x00, 0x2A)) return "TIFF";
        if (StartsWith(bytes, 0x00, 0x00, 0x01, 0x00)) return "ICO";
        if (StartsWith(bytes, 0x1F, 0x8B)) return "gzip-поток";
        if (StartsWith(bytes, (byte)'R', (byte)'I', (byte)'F', (byte)'F') &&
            bytes.Length >= 12 && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return "WebP";
        if (StartsWith(bytes, 0x66, 0x74, 0x79, 0x70)) return "AVIF/HEIF (контейнер ftyp)";
        if (bytes[0] == (byte)'<' && bytes.Length > 5 && bytes[1] is (byte)'!' or (byte)'h' or (byte)'?') return "HTML/XML";
        if (StartsWith(bytes, (byte)'<', (byte)'s', (byte)'v', (byte)'g')) return "SVG";
        if (bytes[0] == (byte)'{') return "JSON";
        return "неизвестный формат";
    }

    private static bool StartsWith(byte[] bytes, params int[] prefix)
    {
        if (bytes.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != (byte)prefix[i]) return false;
        }
        return true;
    }

    // Отказ BitmapImage без причины неотличим от битого потока, поэтому причину отдаём наружу.
    private static ImageSource? Decode(byte[] bytes, out string? why)
    {
        why = null;
        if (bytes.Length == 0)
        {
            why = "пустой поток";
            return null;
        }
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoded = new BitmapImage();
            decoded.BeginInit();
            decoded.CacheOption = BitmapCacheOption.OnLoad;
            decoded.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            decoded.StreamSource = stream;
            decoded.EndInit();
            decoded.Freeze();
            return decoded;
        }
        catch (Exception ex)
        {
            // Битый или незнакомый формат: в карточке остаётся заглушка с первой буквой названия.
            why = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    // Принимаем только http/https: локальный путь или file:// в загрузку попасть не должен.
    public static bool TryHttpUrl(string? url, out string address)
    {
        address = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        address = parsed.AbsoluteUri;
        return true;
    }
}
