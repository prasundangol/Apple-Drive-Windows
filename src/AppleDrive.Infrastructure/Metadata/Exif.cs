using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AppleDrive.Application.Interfaces;

namespace AppleDrive.Infrastructure.Metadata;

/// <summary>Reads date tags from an EXIF (TIFF-structured) block.</summary>
internal static class Exif
{
    private const ushort ExifIfdPointer = 0x8769;
    private const ushort DateTime = 0x0132;
    private const ushort DateTimeOriginal = 0x9003;
    private const ushort DateTimeDigitized = 0x9004;
    private const ushort OffsetTime = 0x9010;
    private const ushort OffsetTimeOriginal = 0x9011;
    private const ushort OffsetTimeDigitized = 0x9012;
    private const ushort AsciiType = 2;

    /// <summary>
    /// When the photo was taken: <c>DateTimeOriginal</c>, else <c>DateTimeDigitized</c>, else the
    /// file's <c>DateTime</c>, each with its offset tag when present.
    /// </summary>
    public static CaptureDate? ReadCaptureDate(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return null;
        }

        bool littleEndian;
        if (tiff[0] == 'I' && tiff[1] == 'I')
        {
            littleEndian = true;
        }
        else if (tiff[0] == 'M' && tiff[1] == 'M')
        {
            littleEndian = false;
        }
        else
        {
            return null;
        }

        var reader = new TiffReader(tiff, littleEndian);
        if (reader.UInt16(2) != 42)
        {
            return null;
        }

        var ifd0 = reader.ReadIfd(reader.UInt32(4));
        var exif = ifd0.TryGetValue(ExifIfdPointer, out var pointer) ? reader.ReadIfd(reader.UInt32(pointer.ValueOffset)) : new Dictionary<ushort, Entry>();

        return Combine(reader.Ascii(exif, DateTimeOriginal), reader.Ascii(exif, OffsetTimeOriginal))
            ?? Combine(reader.Ascii(exif, DateTimeDigitized), reader.Ascii(exif, OffsetTimeDigitized))
            ?? Combine(reader.Ascii(ifd0, DateTime), reader.Ascii(exif, OffsetTime));
    }

    /// <summary>EXIF time <c>2026:09:25 14:03:11</c> plus an optional offset <c>+05:45</c>.</summary>
    internal static CaptureDate? Combine(string? dateTime, string? offset)
    {
        if (dateTime is null
            || !System.DateTime.TryParseExact(dateTime.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            || local.Year < 1900)
        {
            return null;
        }

        if (offset is not null && TryParseOffset(offset.Trim(), out var known))
        {
            return new CaptureDate(new DateTimeOffset(local, known), HasKnownOffset: true);
        }

        // No offset recorded: the time is local to where it was taken; assume this PC's zone.
        return new CaptureDate(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)), HasKnownOffset: false);
    }

    private static bool TryParseOffset(string text, out TimeSpan offset)
    {
        offset = default;
        if (text.Length != 6 || text[0] is not ('+' or '-') || text[3] != ':'
            || !int.TryParse(text.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(text.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || hours > 14 || minutes > 59)
        {
            return false;
        }

        offset = new TimeSpan(hours, minutes, 0) * (text[0] == '-' ? -1 : 1);
        return true;
    }

    private readonly record struct Entry(ushort Type, uint Count, int ValueOffset);

    private readonly ref struct TiffReader(ReadOnlySpan<byte> data, bool littleEndian)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public ushort UInt16(int offset) => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(offset, 2));

        public int UInt32(int offset)
        {
            var value = littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(offset, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(offset, 4));
            return value > int.MaxValue ? throw new ArgumentException("Offset out of range.") : (int)value;
        }

        /// <summary>Tag → entry, where <see cref="Entry.ValueOffset"/> is the position of the 4-byte value field.</summary>
        public Dictionary<ushort, Entry> ReadIfd(int offset)
        {
            var entries = new Dictionary<ushort, Entry>();
            if (offset <= 0 || offset + 2 > _data.Length)
            {
                return entries;
            }

            var count = UInt16(offset);
            for (var index = 0; index < count; index++)
            {
                var position = offset + 2 + (index * 12);
                if (position + 12 > _data.Length)
                {
                    break;
                }

                var tag = UInt16(position);
                var type = UInt16(position + 2);
                var valueCount = littleEndian
                    ? BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(position + 4, 4))
                    : BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(position + 4, 4));
                entries.TryAdd(tag, new Entry(type, valueCount, position + 8));
            }

            return entries;
        }

        public string? Ascii(Dictionary<ushort, Entry> ifd, ushort tag)
        {
            if (!ifd.TryGetValue(tag, out var entry) || entry.Type != AsciiType || entry.Count is 0 or > 64)
            {
                return null;
            }

            var start = entry.Count <= 4 ? entry.ValueOffset : UInt32(entry.ValueOffset);
            if (start < 0 || start + entry.Count > _data.Length)
            {
                return null;
            }

            var text = Encoding.ASCII.GetString(_data.Slice(start, (int)entry.Count)).TrimEnd('\0', ' ');
            return text.Length == 0 ? null : text;
        }
    }
}
