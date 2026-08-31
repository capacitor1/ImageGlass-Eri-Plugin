//! ERI/EMI decode pipeline for ImageGlass. Replaces GARbro's EriFormat
//! plumbing (IBinaryStream / ImageData / VFS) with plain System APIs and
//! BGRA8 output. The heavy lifting stays in EriReader (copied from GARbro,
//! WPF types replaced).
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace EriCodec;

// ============================== enums / metadata ==============================

internal enum CvType
{
    Lossless_EMI = 0x03010000,
    Lossless_ERI = 0x03020000,
    DCT_ERI      = 0x00000001,
    LOT_ERI      = 0x00000005,
    LOT_ERI_MSS  = 0x00000105,
}

internal enum EriCode
{
    ArithmeticCode   = 32,
    RunlengthGamma   = -1,
    RunlengthHuffman = -4,
    Nemesis          = -16,
}

[Flags]
internal enum EriType
{
    RGB         = 0x00000001,
    Gray        = 0x00000002,
    BGR         = 0x00000003,
    YUV         = 0x00000004,
    HSB         = 0x00000006,
    RGBA        = 0x04000001,
    BGRA        = 0x04000003,
    Mask        = 0x0000FFFF,
    WithPalette = 0x01000000,
    UseClipping = 0x02000000,
    WithAlpha   = 0x04000000,
    SideBySide  = 0x10000000,
}

internal enum EriSampling
{
    YUV_4_4_4 = 0x00040404,
    YUV_4_2_2 = 0x00040202,
    YUV_4_1_1 = 0x00040101,
}

/// <summary>Pixel format reported by the (de-WPF'd) EriReader.</summary>
internal enum EriPixelFormat
{
    Unknown = 0,
    Bgra32,   // 32bpp，带 alpha（4 字节/像素）
    Bgr32,    // 32bpp，无 alpha（4 字节/像素，A 字节为填充）
    Bgr24,    // 24bpp，无 alpha（3 字节/像素）★新增
    Gray8,    // 8bpp 灰度
    Indexed8, // 8bpp 索引色（带调色板）
    Bgr555
}

internal sealed class EriFileHeader
{
    public int Version;
    public int ContainedFlag;
    public int KeyFrameCount;
    public int FrameCount;
    public int AllFrameTime;
}

internal sealed class EriMetaData
{
    public int StreamPos;
    public int Version;
    public CvType Transformation;
    public EriCode Architecture;
    public EriType FormatType;
    public bool VerticalFlip;
    public int ClippedPixel;
    public EriSampling SamplingFlags;
    public ulong QuantumizedBits;
    public ulong AllottedBits;
    public int BlockingDegree;
    public int LappedBlock;
    public int FrameTransform;
    public int FrameDegree;
    public EriFileHeader? Header;
    public string Description = string.Empty;
    // image dims / bpp (was ImageMetaData)
    public int Width;
    public int Height;
    public int BPP;
    public string FileName = string.Empty;
}

/// <summary>Simple RGBA color, replacing System.Windows.Media.Color.</summary>
internal readonly struct RgbColor
{
    public readonly byte R, G, B, A;
    public RgbColor(byte r, byte g, byte b, byte a = 255) { R = r; G = g; B = b; A = a; }
}

internal sealed class EriDecoded
{
    public int Width;
    public int Height;
    public bool HasAlpha;
    public byte[] Pixels = Array.Empty<byte>(); // BGRA8, stride = Width*4
}

// ============================== decoder ==============================

internal static class EriDecoder
{
    // Safety cap for hostile dimensions.
    private const long MaxPixels = 0x10000000;

    public static EriMetaData? ReadMetaData(byte[] data)
    {
        if (data.Length < 0x40) return null;

        uint id = ToUInt32(data, 8);
        if (0x03000100 != id && 0x02000100 != id) return null;
        if (!AsciiEqual(data, 0x10, "Entis Rasterized Image") &&
            !AsciiEqual(data, 0x10, "Moving Entis Image") &&
            !AsciiEqual(data, 0x10, "EMSAC-Image"))
            return null;

        int pos = 0x40;
        var section = ReadSection(data, ref pos);
        if (section.Id != "Header  " || section.Length <= 0) return null;
        int header_size = (int)section.Length;
        int stream_pos = 0x50 + header_size;
        EriFileHeader? file_header = null;
        EriMetaData? info = null;
        string? desc = null;

        while (header_size > 0x10)
        {
            section = ReadSection(data, ref pos);
            header_size -= 0x10;
            if (section.Length <= 0 || section.Length > header_size) break;

            if ("FileHdr " == section.Id)
            {
                file_header = new EriFileHeader { Version = ReadInt32(data, ref pos) };
                if (file_header.Version > 0x00020100) return null;
                file_header.ContainedFlag    = ReadInt32(data, ref pos);
                file_header.KeyFrameCount    = ReadInt32(data, ref pos);
                file_header.FrameCount       = ReadInt32(data, ref pos);
                file_header.AllFrameTime     = ReadInt32(data, ref pos);
            }
            else if ("ImageInf" == section.Id)
            {
                int version = ReadInt32(data, ref pos);
                if (version != 0x00020100 && version != 0x00020200) return null;
                info = new EriMetaData { StreamPos = stream_pos, Version = version };
                info.Transformation = (CvType)ReadInt32(data, ref pos);
                info.Architecture   = (EriCode)ReadInt32(data, ref pos);
                info.FormatType     = (EriType)ReadInt32(data, ref pos);
                int w = ReadInt32(data, ref pos);
                int h = ReadInt32(data, ref pos);
                info.Width  = Math.Abs(w);
                info.Height = Math.Abs(h);
                info.VerticalFlip = h < 0;
                info.BPP = ReadInt32(data, ref pos);
                info.ClippedPixel  = ReadInt32(data, ref pos);
                info.SamplingFlags = (EriSampling)ReadInt32(data, ref pos);
                info.QuantumizedBits = ReadUInt64(data, ref pos);
                info.AllottedBits    = ReadUInt64(data, ref pos);
                info.BlockingDegree  = ReadInt32(data, ref pos);
                info.LappedBlock     = ReadInt32(data, ref pos);
                info.FrameTransform  = ReadInt32(data, ref pos);
                info.FrameDegree     = ReadInt32(data, ref pos);
            }
            else if ("descript" == section.Id)
            {
                int len = (int)section.Length;
                if (len >= 2 && data[pos] == 0xFF && data[pos + 1] == 0xFE)
                {
                    // UTF-16 LE with BOM
                    pos += 2;
                    len -= 2;
                    desc = Encoding.Unicode.GetString(data, pos, len);
                    pos += len;
                }
                else
                {
                    desc = Encoding.UTF8.GetString(data, pos, len);
                    pos += len;
                }
            }
            else
            {
                pos += (int)section.Length;
            }
            header_size -= (int)section.Length;
        }

        if (info != null)
        {
            if (file_header != null) info.Header = file_header;
            if (desc != null) info.Description = desc;
        }
        return info;
    }

    public static EriDecoded? Decode(byte[] data, string filePath)
    {
        var meta = ReadMetaData(data);
        if (meta == null) return null;
        if (meta.Width <= 0 || meta.Height <= 0 ||
            (long)meta.Width * meta.Height > MaxPixels) return null;
        meta.FileName = filePath;

        EriReader? reader;
        try { reader = ReadImageData(data, meta); }
        catch { return null; }
        if (reader == null) return null;

        // Optional KiriKiri-style delta: "#reference-file" points at a base ERI.
        if (!string.IsNullOrEmpty(meta.Description))
        {
            try
            {
                var tags = ParseTagInfo(meta.Description);
                if (tags.TryGetValue("reference-file", out var ref_file))
                {
                    ref_file = ref_file.TrimEnd(null);
                    if (!string.IsNullOrEmpty(ref_file) && (meta.BPP + 7) / 8 >= 3)
                    {
                        ref_file = Path.Combine(Path.GetDirectoryName(filePath) ?? string.Empty, ref_file);
                        var ref_data = File.ReadAllBytes(ref_file);
                        var ref_meta = ReadMetaData(ref_data);
                        if (ref_meta != null)
                        {
                            ref_meta.FileName = ref_file;
                            var ref_reader = ReadImageData(ref_data, ref_meta);
                            if (ref_reader != null)
                                reader.AddImageBuffer(ref_reader);
                        }
                    }
                }
            }
            catch { /* best-effort: skip reference blending */ }
        }

        return ConvertToBgra(reader, meta);
    }

    private static EriReader? ReadImageData(byte[] data, EriMetaData meta)
    {
        int pos = meta.StreamPos;
        RgbColor[]? palette = null;

        for (;;)
        {
            if (pos + 16 > data.Length) return null;
            var section = ReadSection(data, ref pos);
            if (section.Id == "Stream  ")
                continue;
            if (section.Id == "ImageFrm")
                break;
            if (section.Id == "Palette " && meta.BPP <= 8 && section.Length <= 0x400)
            {
                palette = ReadPalette(data, ref pos, (int)section.Length);
                continue;
            }
            if (section.Length < 0 || pos + section.Length > data.Length) return null;
            pos += (int)section.Length;
        }

        using var ms = new MemoryStream(data, writable: false);
        ms.Position = pos; // start of "ImageFrm" payload
        var reader = new EriReader(ms, meta, palette);
        reader.DecodeImage();
        return reader;
    }

    private static RgbColor[] ReadPalette(byte[] data, ref int pos, int palette_length)
    {
        int colors = palette_length / 4;
        if (colors <= 0 || colors > 0x100) throw new InvalidDataException("bad palette");
        var palette = new RgbColor[colors];
        for (int i = 0; i < colors; i++)
        {
            byte b = data[pos++];
            byte g = data[pos++];
            byte r = data[pos++];
            byte a = data[pos++];
            palette[i] = new RgbColor(r, g, b, a);
        }
        return palette;
    }

    // ============================== BGRA conversion ==============================

    private static EriDecoded? ConvertToBgra(EriReader reader, EriMetaData meta)
    {
        int w = meta.Width, h = meta.Height;
        byte[] src = reader.Data;
        int srcStride = reader.Stride;
        if (w <= 0 || h <= 0 || srcStride < 1 || src.Length < (long)srcStride * (h - 1) + w) return null;

        var outPixels = new byte[w * h * 4];
        switch (reader.Format)
        {
            case EriPixelFormat.Bgra32:
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(src, y * srcStride, outPixels, y * w * 4, w * 4);
                return new EriDecoded { Width = w, Height = h, HasAlpha = true, Pixels = outPixels };

            case EriPixelFormat.Bgr32:
                for (int y = 0; y < h; y++)
                {
                    int s = y * srcStride, d = y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        outPixels[d++] = src[s++];
                        outPixels[d++] = src[s++];
                        outPixels[d++] = src[s++];
                        outPixels[d++] = 0xFF;
                    }
                }
                return new EriDecoded { Width = w, Height = h, HasAlpha = false, Pixels = outPixels };

            case EriPixelFormat.Gray8:
                for (int y = 0; y < h; y++)
                {
                    int s = y * srcStride, d = y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        byte g = src[s++];
                        outPixels[d++] = g; outPixels[d++] = g; outPixels[d++] = g;
                        outPixels[d++] = 0xFF;
                    }
                }
                return new EriDecoded { Width = w, Height = h, HasAlpha = false, Pixels = outPixels };

            case EriPixelFormat.Indexed8:
            {
                var pal = reader.Palette;
                for (int y = 0; y < h; y++)
                {
                    int s = y * srcStride, d = y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        int idx = src[s++];
                        var p = idx >= 0 && idx < pal.Length ? pal[idx] : new RgbColor(0, 0, 0, 0);
                        outPixels[d++] = p.B;
                        outPixels[d++] = p.G;
                        outPixels[d++] = p.R;
                        outPixels[d++] = p.A;
                    }
                }
                return new EriDecoded { Width = w, Height = h, HasAlpha = true, Pixels = outPixels };
            }

            default:
                return null;
        }
    }

    // ============================== tag parsing (replaces Regex/VFS) ==============================

    private static Dictionary<string, string> ParseTagInfo(string desc)
    {
        var dict = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(desc)) return dict;
        if (desc[0] != '#')
        {
            dict["comment"] = desc;
            return dict;
        }

        using var reader = new StringReader(desc);
        string? line = reader.ReadLine();
        while (line != null)
        {
            // tag line: optional leading spaces, '#', tag word
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            if (i >= line.Length || line[i] != '#') break;
            i++;
            while (i < line.Length && line[i] == ' ') i++;
            int tagStart = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            if (i == tagStart) break;
            string tag = line[tagStart..i];

            var value = new StringBuilder();
            for (;;)
            {
                line = reader.ReadLine();
                if (line == null) break;
                if (line.StartsWith("#"))
                {
                    if (line.Length < 2 || line[1] != '#') break;
                    line = line.Substring(1);
                }
                value.AppendLine(line);
            }
            dict[tag] = value.ToString();
        }
        return dict;
    }

    // ============================== byte helpers ==============================

    private static (string Id, long Length) ReadSection(byte[] data, ref int pos)
    {
        if (pos + 16 > data.Length) throw new EndOfStreamException();
        var id = Encoding.ASCII.GetString(data, pos, 8);
        pos += 8;
        long len = data[pos] | ((long)data[pos + 1] << 8) | ((long)data[pos + 2] << 16) | ((long)data[pos + 3] << 24)
                 | ((long)data[pos + 4] << 32) | ((long)data[pos + 5] << 40) | ((long)data[pos + 6] << 48) | ((long)data[pos + 7] << 56);
        pos += 8;
        return (id, len);
    }

    private static int ReadInt32(byte[] b, ref int i)
    {
        int v = b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
        i += 4;
        return v;
    }

    private static ulong ReadUInt64(byte[] b, ref int i)
    {
        ulong v = (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24))
                | (ulong)(uint)(b[i + 4] | (b[i + 5] << 8) | (b[i + 6] << 16) | (b[i + 7] << 24)) << 32;
        i += 8;
        return v;
    }

    private static uint ToUInt32(byte[] b, int i)
        => (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

    private static bool AsciiEqual(byte[] b, int off, string s)
    {
        if (off < 0 || off + s.Length > b.Length) return false;
        for (int i = 0; i < s.Length; i++)
            if (b[off + i] != (byte)s[i]) return false;
        return true;
    }
}

/// <summary>Little-endian helpers for the copied ErisaMatrix/ErisaNemesis code.</summary>
internal static class LittleEndian
{
    public static int ToInt32(byte[] b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
    public static uint ToUInt32(byte[] b, int i) => (uint)ToInt32(b, i);
    public static ushort ToUInt16(byte[] b, int i) => (ushort)(b[i] | (b[i + 1] << 8));
    public static void Pack(short value, byte[] b, int i)
    {
        b[i] = (byte)value;
        b[i + 1] = (byte)(value >> 8);
    }
    public static void Pack(int value, byte[] b, int i)
    {
        b[i] = (byte)value; b[i + 1] = (byte)(value >> 8);
        b[i + 2] = (byte)(value >> 16); b[i + 3] = (byte)(value >> 24);
    }
}