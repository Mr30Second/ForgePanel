using System;
using System.IO;
using System.IO.Compression;

namespace ForgePanel.Setup
{
    // Форма файла мастера: [сам .exe][zip с payload][16 байт футера].
    //
    // Почему не «просто приложить zip к .exe»: ZipArchive из .NET Framework 4.7.2 не понимает
    // self-extracting-архивы — он читает смещения центрального каталога как абсолютные позиции
    // в потоке и на файле с префиксом падает («число записей ... не соответствует»). Это проверено
    // на этой машине. Поэтому zip открывается через SliceStream: поток, который считает нулём
    // начало архива и не видит футер за его концом.
    //
    // Футер стоит ПОСЛЕ zip (а не перед), потому что ZipArchive ищет конец центрального каталога
    // с конца потока; SliceStream ограничивает длину так, что последними байтами архива оказывается
    // именно EOCD, а не хвост мастера.
    sealed class SliceStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public SliceStream(Stream inner, long start, long length)
        {
            _inner = inner;
            _start = start;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            long left = _length - _position;
            if (left <= 0) return 0;
            if (count > left) count = (int)left;
            _inner.Position = _start + _position;
            int read = _inner.Read(buffer, offset, count);
            if (read > 0) _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin == SeekOrigin.Begin ? offset
                : origin == SeekOrigin.Current ? _position + offset
                : _length + offset;
            if (target < 0) throw new IOException("Позиция в архиве ушла за начало.");
            _position = target;
            _inner.Position = _start + _position;
            return _position;
        }
    }

    static class Container
    {
        private static readonly byte[] Magic = { (byte)'F', (byte)'P', (byte)'S', (byte)'1' };
        public const int FooterSize = 16;

        public static void WriteFooter(Stream target, long zipLength)
        {
            target.Write(Magic, 0, Magic.Length);
            for (int i = 0; i < 8; i++) target.WriteByte((byte)((zipLength >> (8 * i)) & 0xFF));
            target.Write(new byte[4], 0, 4);
        }

        // Возвращает false, если футера нет или он не сходится: это голый деинсталлятор
        // либо мастер, у которого payload оборвался.
        public static bool TryReadFooter(Stream source, out long zipStart, out long zipLength)
        {
            zipStart = -1;
            zipLength = -1;
            if (source.Length < FooterSize) return false;

            source.Position = source.Length - FooterSize;
            byte[] footer = new byte[FooterSize];
            int got = 0;
            while (got < FooterSize)
            {
                int read = source.Read(footer, got, FooterSize - got);
                if (read <= 0) break;
                got += read;
            }
            if (got < FooterSize) return false;

            for (int i = 0; i < Magic.Length; i++)
                if (footer[i] != Magic[i]) return false;

            long length = 0;
            for (int i = 0; i < 8; i++) length |= (long)footer[4 + i] << (8 * i);
            if (length <= 0 || length > source.Length - FooterSize) return false;

            zipLength = length;
            zipStart = source.Length - FooterSize - length;
            return true;
        }

        public static Payload Open(string filePath)
        {
            FileStream file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                long zipStart, zipLength;
                if (!TryReadFooter(file, out zipStart, out zipLength))
                    throw new InvalidOperationException(
                        "В этом файле нет набора файлов панели. Так выглядит uninstall.exe: " +
                        "он удаляет установленный ForgePanel и не может ничего установить.");

                ZipArchive archive = new ZipArchive(new SliceStream(file, zipStart, zipLength), ZipArchiveMode.Read);
                return new Payload(file, archive, zipStart);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
    }

    public sealed class Payload : IDisposable
    {
        private readonly FileStream _file;
        private readonly ZipArchive _archive;

        // Сколько байт в начале файла занимает сам мастер: ровно столько же пишется в uninstall.exe.
        public long StubLength { get; }

        public int Entries => _archive.Entries.Count;

        public long TotalBytes
        {
            get
            {
                long total = 0;
                foreach (ZipArchiveEntry entry in _archive.Entries) total += entry.Length;
                return total;
            }
        }

        internal Payload(FileStream file, ZipArchive archive, long stubLength)
        {
            _file = file;
            _archive = archive;
            StubLength = stubLength;
        }

        public void ExtractTo(string targetDir, Action<string, long, long> onProgress, Func<bool> isCanceled)
        {
            string root = Path.GetFullPath(targetDir + Path.DirectorySeparatorChar);
            long total = TotalBytes;
            long done = 0;

            foreach (ZipArchiveEntry entry in _archive.Entries)
            {
                if (isCanceled != null && isCanceled())
                    throw new OperationCanceledException("Установка отменена.");

                string relative = entry.FullName
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                if (relative.Length == 0) continue;

                // zip-slip: запись вида "..\\..\\windows\\..." не должна дать мастеру право
                // писать за пределы выбранной папки.
                string target = Path.GetFullPath(Path.Combine(root, relative));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Запись в архиве указывает за пределы папки установки: " + entry.FullName);

                if (relative.EndsWith(Path.DirectorySeparatorChar.ToString()))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (Stream source = entry.Open())
                using (Stream destination = new FileStream(target, FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[1 << 16];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        destination.Write(buffer, 0, read);
                        done += read;
                        if (onProgress != null) onProgress(relative, done, total);
                    }
                }
            }

            if (onProgress != null) onProgress(null, total, total);
        }

        // Копирует из себя только стартовые байты — получается рабочий деинсталлятор без payload.
        public void WriteStubTo(string targetFile)
        {
            _file.Position = 0;
            using (FileStream target = new FileStream(targetFile, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[1 << 16];
                long left = StubLength;
                while (left > 0)
                {
                    int chunk = (int)Math.Min(buffer.Length, left);
                    int read = _file.Read(buffer, 0, chunk);
                    if (read <= 0) throw new EndOfStreamException("Файл мастера короче, чем обещает футер.");
                    target.Write(buffer, 0, read);
                    left -= read;
                }
            }
        }

        public void Dispose()
        {
            _archive.Dispose();
            _file.Dispose();
        }
    }
}
