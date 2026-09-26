using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AppleDrive.Application.Interfaces;

namespace AppleDrive.Infrastructure.Metadata;

/// <summary>
/// Reads capture dates without any imaging library:
/// EXIF <c>DateTimeOriginal</c> (with <c>OffsetTimeOriginal</c>) from JPEG and from HEIC/HEIF,
/// where EXIF is an item located through the <c>meta</c> box; and for MOV/MP4 the Apple
/// <c>com.apple.quicktime.creationdate</c> (local time with offset), else the <c>mvhd</c> time (UTC).
/// </summary>
/// <remarks>
/// Only headers are read, by seeking from box to box, so a multi-gigabyte video costs a few reads.
/// Malformed or unknown files give <c>null</c>, never an exception.
/// </remarks>
public sealed class CaptureDateReader : ICaptureDateReader
{
    private const int MaxMetadataBytes = 1024 * 1024;
    private const int MaxBoxes = 4096;
    private static readonly DateTime QuickTimeEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public Task<CaptureDate?> ReadAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
                return Read(stream);
            },
            cancellationToken);

    /// <summary>Reads from a seekable stream positioned anywhere.</summary>
    internal static CaptureDate? Read(Stream stream)
    {
        try
        {
            stream.Position = 0;
            Span<byte> header = stackalloc byte[12];
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                return null;
            }

            if (header[0] == 0xFF && header[1] == 0xD8)
            {
                return ReadJpeg(stream);
            }

            return header[4..8].SequenceEqual("ftyp"u8) ? ReadIsoMedia(stream) : null;
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException or OverflowException or InvalidDataException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ JPEG

    private static CaptureDate? ReadJpeg(Stream stream)
    {
        stream.Position = 2;
        for (var segment = 0; segment < 64; segment++)
        {
            var marker = ReadMarker(stream);
            if (marker is null or 0xD9 or 0xDA)
            {
                return null; // End of image, or start of scan: no metadata after this.
            }

            var length = ReadUInt16BigEndian(stream);
            if (length < 2)
            {
                return null;
            }

            var dataLength = length - 2;
            if (marker == 0xE1 && dataLength > 6)
            {
                var data = ReadBytes(stream, dataLength);
                if (data.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8))
                {
                    return Exif.ReadCaptureDate(data.AsSpan(6));
                }
            }
            else
            {
                stream.Seek(dataLength, SeekOrigin.Current);
            }
        }

        return null;
    }

    private static int? ReadMarker(Stream stream)
    {
        var value = stream.ReadByte();
        if (value != 0xFF)
        {
            return null;
        }

        do
        {
            value = stream.ReadByte(); // Skip fill bytes.
        }
        while (value == 0xFF);

        return value < 0 ? null : value;
    }

    // ------------------------------------------------------------------ ISO base media (HEIF, MOV, MP4)

    private static CaptureDate? ReadIsoMedia(Stream stream)
    {
        var topLevel = Boxes(stream, 0, stream.Length).ToList();
        if (topLevel.FirstOrDefault(box => box.Type == "moov") is { Type: not null } moov)
        {
            return ReadQuickTime(stream, moov);
        }

        if (topLevel.FirstOrDefault(box => box.Type == "meta") is { Type: not null } meta)
        {
            return ReadHeifExif(stream, meta);
        }

        return null;
    }

    private static CaptureDate? ReadHeifExif(Stream stream, Box meta)
    {
        // 'meta' is a full box: 4 bytes of version and flags before its children.
        var children = Boxes(stream, meta.ContentStart + 4, meta.End).ToList();
        var iinf = children.FirstOrDefault(box => box.Type == "iinf");
        var iloc = children.FirstOrDefault(box => box.Type == "iloc");
        if (iinf.Type is null || iloc.Type is null)
        {
            return null;
        }

        var exifItem = FindExifItemId(ReadContent(stream, iinf));
        if (exifItem is not { } itemId)
        {
            return null;
        }

        var extents = FindItemExtents(ReadContent(stream, iloc), itemId);
        if (extents is null)
        {
            return null;
        }

        using var data = new MemoryStream();
        foreach (var (offset, length) in extents)
        {
            if (length <= 0 || data.Length + length > MaxMetadataBytes || offset < 0 || offset + length > stream.Length)
            {
                return null;
            }

            stream.Position = offset;
            data.Write(ReadBytes(stream, (int)length));
        }

        // The item starts with a 4-byte offset to the TIFF header (skipping an "Exif\0\0" prefix).
        var exif = data.GetBuffer().AsSpan(0, (int)data.Length);
        if (exif.Length < 4)
        {
            return null;
        }

        var tiffOffset = 4 + (long)BinaryPrimitives.ReadUInt32BigEndian(exif);
        return tiffOffset < exif.Length ? Exif.ReadCaptureDate(exif[(int)tiffOffset..]) : null;
    }

    /// <summary>Item id of the 'Exif' entry in an item information box.</summary>
    private static uint? FindExifItemId(byte[] iinf)
    {
        var version = iinf[0];
        var position = version == 0 ? 6 : 8; // version/flags + 16- or 32-bit entry count
        while (position + 8 <= iinf.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(iinf.AsSpan(position));
            if (size < 8 || position + size > iinf.Length)
            {
                return null;
            }

            if (iinf.AsSpan(position + 4, 4).SequenceEqual("infe"u8))
            {
                var entry = iinf.AsSpan(position + 8, (int)size - 8);
                var entryVersion = entry[0];
                if (entryVersion >= 2)
                {
                    var idSize = entryVersion == 2 ? 2 : 4;
                    var itemId = idSize == 2 ? BinaryPrimitives.ReadUInt16BigEndian(entry[4..]) : BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
                    var itemType = entry.Slice(4 + idSize + 2, 4);
                    if (itemType.SequenceEqual("Exif"u8))
                    {
                        return itemId;
                    }
                }
            }

            position += (int)size;
        }

        return null;
    }

    /// <summary>File extents (absolute offset, length) of an item, from an item location box.</summary>
    private static List<(long Offset, long Length)>? FindItemExtents(byte[] iloc, uint wantedId)
    {
        var reader = new SpanCursor(iloc);
        var version = reader.Byte();
        reader.Skip(3);
        var sizes = reader.Byte();
        var offsetSize = sizes >> 4;
        var lengthSize = sizes & 0xF;
        var moreSizes = reader.Byte();
        var baseOffsetSize = moreSizes >> 4;
        var indexSize = version is 1 or 2 ? moreSizes & 0xF : 0;
        var itemCount = version < 2 ? reader.UInt16() : reader.UInt32();

        for (var item = 0u; item < itemCount; item++)
        {
            var itemId = version < 2 ? reader.UInt16() : reader.UInt32();
            var constructionMethod = 0;
            if (version is 1 or 2)
            {
                constructionMethod = reader.UInt16() & 0xF;
            }

            reader.Skip(2); // data_reference_index
            var baseOffset = reader.Sized(baseOffsetSize);
            var extentCount = reader.UInt16();
            var extents = new List<(long, long)>(extentCount);
            for (var extent = 0; extent < extentCount; extent++)
            {
                reader.Sized(indexSize);
                var offset = reader.Sized(offsetSize);
                var length = reader.Sized(lengthSize);
                extents.Add((baseOffset + offset, length));
            }

            if (itemId == wantedId)
            {
                // Only offsets into the file itself (method 0) are supported.
                return constructionMethod == 0 ? extents : null;
            }
        }

        return null;
    }

    private static CaptureDate? ReadQuickTime(Stream stream, Box moov)
    {
        CaptureDate? fromMovieHeader = null;
        foreach (var box in Boxes(stream, moov.ContentStart, moov.End))
        {
            if (box.Type == "meta" && ReadAppleCreationDate(stream, box) is { } appleDate)
            {
                return appleDate;
            }

            if (box.Type == "mvhd")
            {
                fromMovieHeader = ReadMovieHeaderDate(stream, box);
            }
        }

        return fromMovieHeader;
    }

    /// <summary><c>com.apple.quicktime.creationdate</c> from a QuickTime metadata box (keys + ilst).</summary>
    private static CaptureDate? ReadAppleCreationDate(Stream stream, Box meta)
    {
        // QuickTime's 'meta' has no version/flags; ISO's does. Tell them apart by where 'hdlr' is.
        var probe = new byte[12];
        stream.Position = meta.ContentStart;
        if (stream.ReadAtLeast(probe, probe.Length, throwOnEndOfStream: false) < probe.Length)
        {
            return null;
        }

        var childrenStart = probe.AsSpan(4, 4).SequenceEqual("hdlr"u8) ? meta.ContentStart : meta.ContentStart + 4;
        var children = Boxes(stream, childrenStart, meta.End).ToList();
        var keysBox = children.FirstOrDefault(box => box.Type == "keys");
        var ilstBox = children.FirstOrDefault(box => box.Type == "ilst");
        if (keysBox.Type is null || ilstBox.Type is null)
        {
            return null;
        }

        var keys = ReadContent(stream, keysBox);
        var cursor = new SpanCursor(keys);
        cursor.Skip(4);
        var count = cursor.UInt32();
        uint? wantedIndex = null;
        for (var index = 1u; index <= count; index++)
        {
            var keySize = (int)cursor.UInt32();
            cursor.Skip(4); // namespace, 'mdta'
            var name = Encoding.UTF8.GetString(cursor.Bytes(keySize - 8));
            if (name == "com.apple.quicktime.creationdate")
            {
                wantedIndex = index;
                break;
            }
        }

        if (wantedIndex is not { } keyIndex)
        {
            return null;
        }

        foreach (var entry in Boxes(stream, ilstBox.ContentStart, ilstBox.End))
        {
            if (entry.TypeCode != keyIndex)
            {
                continue;
            }

            foreach (var data in Boxes(stream, entry.ContentStart, entry.End))
            {
                if (data.Type != "data")
                {
                    continue;
                }

                var content = ReadContent(stream, data);
                return content.Length > 8 ? ParseIsoDate(Encoding.UTF8.GetString(content, 8, content.Length - 8)) : null;
            }
        }

        return null;
    }

    private static CaptureDate? ReadMovieHeaderDate(Stream stream, Box mvhd)
    {
        stream.Position = mvhd.ContentStart;
        var version = stream.ReadByte();
        stream.Seek(3, SeekOrigin.Current);
        var seconds = version == 1 ? ReadUInt64BigEndian(stream) : ReadUInt32BigEndian(stream);
        if (seconds == 0 || seconds > 200L * 365 * 24 * 3600)
        {
            return null;
        }

        var utc = new DateTimeOffset(QuickTimeEpoch.AddSeconds(seconds));
        return new CaptureDate(utc.ToLocalTime(), HasKnownOffset: true);
    }

    /// <summary>ISO 8601 as Apple writes it, e.g. <c>2026-09-25T14:03:11+0545</c>.</summary>
    internal static CaptureDate? ParseIsoDate(string text)
    {
        text = text.Trim('\0', ' ');
        if (text.Length >= 5 && text[^5] is '+' or '-' && text[^4..].All(char.IsAsciiDigit))
        {
            text = text[..^2] + ":" + text[^2..];
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            && value.Year > 1900
            ? new CaptureDate(value, HasKnownOffset: true)
            : null;
    }

    // ------------------------------------------------------------------ box helpers

    private readonly record struct Box(string? Type, uint TypeCode, long ContentStart, long End);

    private static IEnumerable<Box> Boxes(Stream stream, long start, long end)
    {
        var position = start;
        var header = new byte[16];
        for (var count = 0; count < MaxBoxes && position + 8 <= end; count++)
        {
            stream.Position = position;
            if (stream.ReadAtLeast(header.AsSpan(0, 8), 8, throwOnEndOfStream: false) < 8)
            {
                yield break;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var typeCode = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            var contentStart = position + 8;
            if (size == 1)
            {
                if (stream.ReadAtLeast(header.AsSpan(8, 8), 8, throwOnEndOfStream: false) < 8)
                {
                    yield break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                contentStart += 8;
            }
            else if (size == 0)
            {
                size = end - position; // Extends to the end of its parent.
            }

            if (size < contentStart - position || position + size > end)
            {
                yield break;
            }

            yield return new Box(Encoding.ASCII.GetString(header, 4, 4), typeCode, contentStart, position + size);
            position += size;
        }
    }

    private static byte[] ReadContent(Stream stream, Box box)
    {
        var length = box.End - box.ContentStart;
        if (length is < 0 or > MaxMetadataBytes)
        {
            throw new InvalidDataException("Metadata box is too large.");
        }

        stream.Position = box.ContentStart;
        return ReadBytes(stream, (int)length);
    }

    private static byte[] ReadBytes(Stream stream, int count)
    {
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static ushort ReadUInt16BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }

    private static uint ReadUInt32BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private static long ReadUInt64BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        stream.ReadExactly(buffer);
        return (long)BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }

    /// <summary>Big-endian reader over a byte array; throws <see cref="ArgumentException"/> past the end.</summary>
    private ref struct SpanCursor(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public byte Byte() => _data[_position++];

        public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

        public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

        public long Sized(int size) => size switch
        {
            0 => 0,
            4 => UInt32(),
            8 => (long)BinaryPrimitives.ReadUInt64BigEndian(Take(8)),
            _ => throw new InvalidDataException("Unsupported field size."),
        };

        public byte[] Bytes(int count) => Take(count).ToArray();

        public void Skip(int count) => Take(count);

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _position + count > _data.Length)
            {
                throw new ArgumentException("Truncated metadata.");
            }

            var slice = _data.Slice(_position, count);
            _position += count;
            return slice;
        }
    }
}
