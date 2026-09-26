using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AppleDrive.Testing;

/// <summary>
/// Minimal but structurally valid media files, built byte by byte, carrying the metadata the
/// capture-date reader looks for: JPEG with an EXIF APP1 segment, HEIC with an EXIF item located
/// through meta/iinf/iloc, and QuickTime with mvhd and Apple keys/ilst metadata.
/// </summary>
public static class MediaFixtures
{
    /// <summary>A TIFF/EXIF block with the given tags (values like <c>2024:03:15 09:30:00</c> and <c>+05:45</c>).</summary>
    public static byte[] Exif(string? dateTimeOriginal, string? offsetTimeOriginal = null, string? dateTime = null, bool bigEndian = false, ushort? orientation = null)
    {
        var ifd0 = new List<(ushort Tag, ushort Type, byte[] Value)>();
        if (orientation is { } value)
        {
            ifd0.Add((0x0112, 3, bigEndian ? [(byte)(value >> 8), (byte)value] : [(byte)value, (byte)(value >> 8)]));
        }

        var exif = new List<(ushort Tag, ushort Type, byte[] Value)>();
        if (dateTime is not null)
        {
            ifd0.Add((0x0132, 2, Ascii(dateTime)));
        }

        if (dateTimeOriginal is not null)
        {
            exif.Add((0x9003, 2, Ascii(dateTimeOriginal)));
        }

        if (offsetTimeOriginal is not null)
        {
            exif.Add((0x9011, 2, Ascii(offsetTimeOriginal)));
        }

        var ifd0Size = 2 + ((ifd0.Count + 1) * 12) + 4;
        var exifStart = 8 + ifd0Size;
        var exifSize = 2 + (exif.Count * 12) + 4;
        var dataStart = exifStart + exifSize;

        var buffer = new byte[dataStart + ifd0.Sum(e => e.Value.Length) + exif.Sum(e => e.Value.Length)];
        var span = buffer.AsSpan();
        span[0] = span[1] = (byte)(bigEndian ? 'M' : 'I');
        WriteU16(span[2..], 42, bigEndian);
        WriteU32(span[4..], 8, bigEndian);

        var data = dataStart;
        void WriteIfd(int start, List<(ushort Tag, ushort Type, byte[] Value)> entries, int? exifPointer)
        {
            var target = buffer.AsSpan();
            var all = entries.Select(e => (e.Tag, e.Type, Count: (uint)(e.Type == 3 ? e.Value.Length / 2 : e.Value.Length), e.Value)).ToList();
            if (exifPointer is { } pointer)
            {
                all.Add((0x8769, 4, 1u, BitConverter.GetBytes(pointer)));
            }

            WriteU16(target[start..], (ushort)all.Count, bigEndian);
            for (var i = 0; i < all.Count; i++)
            {
                var entry = target[(start + 2 + (i * 12))..];
                WriteU16(entry, all[i].Tag, bigEndian);
                WriteU16(entry[2..], all[i].Type, bigEndian);
                WriteU32(entry[4..], all[i].Count, bigEndian);
                if (all[i].Tag == 0x8769)
                {
                    WriteU32(entry[8..], (uint)exifPointer!.Value, bigEndian);
                }
                else if (all[i].Value.Length <= 4)
                {
                    all[i].Value.CopyTo(entry[8..]);
                }
                else
                {
                    WriteU32(entry[8..], (uint)data, bigEndian);
                    all[i].Value.CopyTo(target[data..]);
                    data += all[i].Value.Length;
                }
            }
        }

        WriteIfd(8, ifd0, exifStart);
        WriteIfd(exifStart, exif, null);
        return buffer;
    }

    /// <summary>A JPEG with an optional EXIF block, followed by <paramref name="payload"/> random bytes of "image data".</summary>
    public static byte[] Jpeg(byte[]? exif, int payload = 2_000)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        Segment(stream, 0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        if (exif is not null)
        {
            Segment(stream, 0xE1, [.. "Exif\0\0"u8, .. exif]);
        }

        Segment(stream, 0xDB, RandomNumberGenerator.GetBytes(64));
        Segment(stream, 0xDA, [1, 1, 0, 0, 0x3F, 0]);
        stream.Write(RandomNumberGenerator.GetBytes(payload).Select(b => b == 0xFF ? (byte)0 : b).ToArray());
        stream.Write([0xFF, 0xD9]);
        return stream.ToArray();
    }

    /// <summary>Inserts an EXIF APP1 segment right after the start of an existing JPEG.</summary>
    public static byte[] WithExif(byte[] jpeg, byte[] exif)
    {
        using var stream = new MemoryStream();
        stream.Write(jpeg.AsSpan(0, 2));
        Segment(stream, 0xE1, [.. "Exif\0\0"u8, .. exif]);
        stream.Write(jpeg.AsSpan(2));
        return stream.ToArray();
    }

    /// <summary>A HEIF file whose EXIF item (if any) lives in <c>mdat</c>, located through <c>iloc</c>.</summary>
    public static byte[] Heic(byte[]? exif, int payload = 4_000)
    {
        var ftyp = Box("ftyp", [.. "heic"u8, 0, 0, 0, 0, .. "mif1"u8, .. "heic"u8]);
        var image = RandomNumberGenerator.GetBytes(payload);
        byte[] exifItem = exif is null ? [] : [0, 0, 0, 6, .. "Exif\0\0"u8, .. exif];

        byte[] BuildMeta(uint mdatDataStart)
        {
            var hdlr = FullBox("hdlr", 0, [0, 0, 0, 0, .. "pict"u8, .. new byte[12], 0]);
            var entries = new List<byte[]> { FullBox("infe", 2, [0, 1, 0, 0, .. "hvc1"u8, 0]) };
            if (exif is not null)
            {
                entries.Add(FullBox("infe", 2, [0, 2, 0, 0, .. "Exif"u8, 0]));
            }

            var iinf = FullBox("iinf", 0, [.. U16((ushort)entries.Count), .. entries.SelectMany(e => e)]);
            var items = new List<byte>(U16((ushort)entries.Count));
            items.AddRange([0, 1, 0, 0, 0, 1, .. U32(mdatDataStart), .. U32((uint)image.Length)]);
            if (exif is not null)
            {
                items.AddRange([0, 2, 0, 0, 0, 1, .. U32(mdatDataStart + (uint)image.Length), .. U32((uint)exifItem.Length)]);
            }

            var iloc = FullBox("iloc", 0, [0x44, 0x00, .. items]);
            return FullBox("meta", 0, [.. hdlr, .. iinf, .. iloc]);
        }

        var metaLength = BuildMeta(0).Length;
        var meta = BuildMeta((uint)(ftyp.Length + metaLength + 8));
        var mdat = Box("mdat", [.. image, .. exifItem]);
        return [.. ftyp, .. meta, .. mdat];
    }

    /// <summary>
    /// A QuickTime movie with <c>mdat</c> first and <c>moov</c> last, as an iPhone writes them.
    /// <paramref name="movieHeaderUtc"/> sets the mvhd creation time; <paramref name="appleCreationDate"/>
    /// adds <c>com.apple.quicktime.creationdate</c> (e.g. <c>2024-03-15T09:30:00+0545</c>).
    /// </summary>
    public static byte[] QuickTime(DateTime? movieHeaderUtc, string? appleCreationDate = null, int payload = 6_000)
    {
        var ftyp = Box("ftyp", [.. "qt  "u8, 0, 0, 0, 0, .. "qt  "u8]);
        var mdat = Box("mdat", RandomNumberGenerator.GetBytes(payload));
        var seconds = movieHeaderUtc is { } utc ? (uint)(utc - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds : 0;
        var mvhd = FullBox("mvhd", 0, [.. U32(seconds), .. U32(seconds), .. U32(600), .. U32(6000), .. new byte[80]]);

        var moovChildren = new List<byte>(mvhd);
        if (appleCreationDate is not null)
        {
            var hdlr = FullBox("hdlr", 0, [0, 0, 0, 0, .. "mdta"u8, .. new byte[12], 0]);
            byte[] Key(string name) => [.. U32((uint)(8 + name.Length)), .. "mdta"u8, .. Encoding.UTF8.GetBytes(name)];
            var keys = FullBox("keys", 0, [.. U32(2), .. Key("com.apple.quicktime.make"), .. Key("com.apple.quicktime.creationdate")]);
            byte[] Value(uint index, string text) =>
                BoxRaw(index, Box("data", [0, 0, 0, 1, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(text)]));
            var ilst = Box("ilst", [.. Value(1, "Apple"), .. Value(2, appleCreationDate)]);

            // QuickTime-style 'meta': no version/flags, children follow the header directly.
            moovChildren.AddRange(Box("meta", [.. hdlr, .. keys, .. ilst]));
        }

        var moov = Box("moov", [.. moovChildren]);
        return [.. ftyp, .. mdat, .. moov];
    }

    private static void Segment(Stream stream, byte marker, byte[] data)
    {
        stream.Write([0xFF, marker]);
        stream.Write(U16((ushort)(data.Length + 2)));
        stream.Write(data);
    }

    private static byte[] Box(string type, byte[] content) => [.. U32((uint)(8 + content.Length)), .. Encoding.ASCII.GetBytes(type), .. content];

    private static byte[] BoxRaw(uint type, byte[] content) => [.. U32((uint)(8 + content.Length)), .. U32(type), .. content];

    private static byte[] FullBox(string type, byte version, byte[] content) => Box(type, [version, 0, 0, 0, .. content]);

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value + "\0");

    private static byte[] U16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static void WriteU16(Span<byte> target, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(target, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(target, value);
        }
    }

    private static void WriteU32(Span<byte> target, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(target, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(target, value);
        }
    }
}
