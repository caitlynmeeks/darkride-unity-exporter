// OpenEXR, the little of it a lightmap needs: a reader for single-part scanline files with NONE, RLE, ZIPS or ZIP
// compression and half, float or uint channels (what Unity's Lightmap-N_comp_light.exr files are: float RGBA, ZIP),
// and a writer for half RGB with ZIP compression (what darkride loads as an uncompressed half-float texture).
// Images are float RGB, row 0 at the top, as the engine's reader sees them.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Darkride.Exporter
{
    public class FloatImage
    {
        public int Width;
        public int Height;
        public float[] Rgb;    // 3 floats a pixel, row 0 at the top

        public FloatImage(int width, int height)
        {
            Width = width;
            Height = height;
            Rgb = new float[width * height * 3];
        }
    }

    public static class Exr
    {
        const uint MAGIC = 20000630;
        const int VERSION = 2;
        const int FLAG_TILED = 0x200;
        const int FLAG_DEEP = 0x800;
        const int FLAG_MULTIPART = 0x1000;

        enum Compression { None = 0, Rle = 1, Zips = 2, Zip = 3, Piz = 4, Pxr24 = 5, B44 = 6, B44a = 7, Dwaa = 8, Dwab = 9 }
        enum PixelType { Uint = 0, Half = 1, Float = 2 }

        class Channel
        {
            public string Name;
            public PixelType Type;
            public int Size => Type == PixelType.Half ? 2 : 4;
        }

        public static string DescribeCompression(string path)
        {
            try
            {
                var header = ReadHeader(File.ReadAllBytes(path), out _);
                return header.Compression.ToString();
            }
            catch (Exception e)
            {
                return "unreadable (" + e.Message + ")";
            }
        }

        class Header
        {
            public List<Channel> Channels = new List<Channel>();
            public Compression Compression;
            public int XMin, YMin, XMax, YMax;
            public int Width => XMax - XMin + 1;
            public int Height => YMax - YMin + 1;
        }

        static Header ReadHeader(byte[] data, out int pos)
        {
            pos = 0;
            if (data.Length < 8 || ReadU32(data, ref pos) != MAGIC) throw new InvalidDataException("not an OpenEXR file");
            int version = ReadI32(data, ref pos);
            if ((version & 0xff) != VERSION) throw new InvalidDataException("OpenEXR version " + (version & 0xff) + " not supported");
            if ((version & FLAG_TILED) != 0) throw new InvalidDataException("tiled OpenEXR files are not supported");
            if ((version & (FLAG_DEEP | FLAG_MULTIPART)) != 0) throw new InvalidDataException("deep and multi-part OpenEXR files are not supported");

            var header = new Header();
            while (true)
            {
                string name = ReadCString(data, ref pos);
                if (name.Length == 0) break;
                string type = ReadCString(data, ref pos);
                int size = ReadI32(data, ref pos);
                int valueStart = pos;
                switch (name)
                {
                    case "channels":
                    {
                        int p = pos;
                        while (true)
                        {
                            string channelName = ReadCString(data, ref p);
                            if (channelName.Length == 0) break;
                            int pixelType = ReadI32(data, ref p);
                            p += 4;    // pLinear and three reserved bytes
                            p += 8;    // x and y sampling
                            header.Channels.Add(new Channel { Name = channelName, Type = (PixelType)pixelType });
                        }
                        break;
                    }
                    case "compression":
                        header.Compression = (Compression)data[pos];
                        break;
                    case "dataWindow":
                    {
                        int p = pos;
                        header.XMin = ReadI32(data, ref p);
                        header.YMin = ReadI32(data, ref p);
                        header.XMax = ReadI32(data, ref p);
                        header.YMax = ReadI32(data, ref p);
                        break;
                    }
                }
                pos = valueStart + size;
            }
            if (header.Channels.Count == 0) throw new InvalidDataException("OpenEXR file without channels");
            foreach (var channel in header.Channels)
                if (channel.Type != PixelType.Half && channel.Type != PixelType.Float && channel.Type != PixelType.Uint)
                    throw new InvalidDataException("OpenEXR channel " + channel.Name + " has an unknown pixel type");
            return header;
        }

        static int LinesPerChunk(Compression compression)
        {
            switch (compression)
            {
                case Compression.None:
                case Compression.Rle:
                case Compression.Zips: return 1;
                case Compression.Zip:
                case Compression.Pxr24: return 16;
                case Compression.Piz:
                case Compression.B44:
                case Compression.B44a: return 32;
                case Compression.Dwaa: return 32;
                case Compression.Dwab: return 256;
            }
            return 1;
        }

        // Reads the R, G and B channels (a Y channel stands in for all three) as float RGB.
        public static FloatImage Read(string path)
        {
            var data = File.ReadAllBytes(path);
            var header = ReadHeader(data, out int pos);
            if (header.Compression != Compression.None && header.Compression != Compression.Rle &&
                header.Compression != Compression.Zips && header.Compression != Compression.Zip)
                throw new InvalidDataException("OpenEXR " + header.Compression + " compression is not supported (NONE, RLE, ZIPS and ZIP are)");

            int width = header.Width, height = header.Height;
            int linesPerChunk = LinesPerChunk(header.Compression);
            int chunkCount = (height + linesPerChunk - 1) / linesPerChunk;
            var offsets = new long[chunkCount];
            for (int i = 0; i < chunkCount; i++) offsets[i] = (long)ReadU64(data, ref pos);

            int bytesPerPixel = 0;
            foreach (var channel in header.Channels) bytesPerPixel += channel.Size;
            var targets = new int[header.Channels.Count];
            bool hasRgb = false;
            for (int c = 0; c < header.Channels.Count; c++)
            {
                string n = header.Channels[c].Name;
                targets[c] = n == "R" ? 0 : n == "G" ? 1 : n == "B" ? 2 : -1;
                if (targets[c] >= 0) hasRgb = true;
            }
            if (!hasRgb)
            {
                for (int c = 0; c < header.Channels.Count; c++)
                    if (header.Channels[c].Name == "Y") targets[c] = 3;    // luminance: all three
            }

            var image = new FloatImage(width, height);
            for (int chunk = 0; chunk < chunkCount; chunk++)
            {
                int p = (int)offsets[chunk];
                int y = ReadI32(data, ref p);
                int packedSize = ReadI32(data, ref p);
                int lines = Math.Min(linesPerChunk, header.YMax - y + 1);
                int unpackedSize = lines * width * bytesPerPixel;
                byte[] raw;
                if (packedSize == unpackedSize || header.Compression == Compression.None)
                {
                    raw = new byte[unpackedSize];
                    Array.Copy(data, p, raw, 0, unpackedSize);
                }
                else
                {
                    byte[] packed = header.Compression == Compression.Rle
                        ? RleDecode(data, p, packedSize, unpackedSize)
                        : Zlib.Inflate(data, p, packedSize, unpackedSize);
                    raw = Unpredict(packed);
                }

                int rp = 0;
                for (int l = 0; l < lines; l++)
                {
                    int row = y + l - header.YMin;
                    for (int c = 0; c < header.Channels.Count; c++)
                    {
                        var channel = header.Channels[c];
                        int target = targets[c];
                        if (target < 0)
                        {
                            rp += width * channel.Size;
                            continue;
                        }
                        for (int x = 0; x < width; x++)
                        {
                            float v;
                            switch (channel.Type)
                            {
                                case PixelType.Half: v = Mathf.HalfToFloat(BitConverter.ToUInt16(raw, rp)); break;
                                case PixelType.Float: v = BitConverter.ToSingle(raw, rp); break;
                                default: v = BitConverter.ToUInt32(raw, rp); break;
                            }
                            rp += channel.Size;
                            int pixel = (row * width + x) * 3;
                            if (target == 3)
                            {
                                image.Rgb[pixel] = v;
                                image.Rgb[pixel + 1] = v;
                                image.Rgb[pixel + 2] = v;
                            }
                            else
                            {
                                image.Rgb[pixel + target] = v;
                            }
                        }
                    }
                }
            }
            return image;
        }

        // Writes half RGB (channels B, G, R as the format orders them), ZIP compressed in blocks of 16 scanlines.
        public static void WriteHalfRgb(string path, FloatImage image, bool compress = true)
        {
            int width = image.Width, height = image.Height;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(MAGIC);
                w.Write(VERSION);

                var channels = new MemoryStream();
                foreach (string name in new[] { "B", "G", "R" })
                {
                    WriteCString(channels, name);
                    WriteI32(channels, (int)PixelType.Half);
                    channels.WriteByte(0);              // pLinear
                    channels.WriteByte(0);              // reserved
                    channels.WriteByte(0);
                    channels.WriteByte(0);
                    WriteI32(channels, 1);              // x sampling
                    WriteI32(channels, 1);              // y sampling
                }
                channels.WriteByte(0);
                WriteAttribute(w, "channels", "chlist", channels.ToArray());
                WriteAttribute(w, "compression", "compression", new[] { (byte)(compress ? Compression.Zip : Compression.None) });
                var box = new MemoryStream();
                WriteI32(box, 0); WriteI32(box, 0); WriteI32(box, width - 1); WriteI32(box, height - 1);
                WriteAttribute(w, "dataWindow", "box2i", box.ToArray());
                WriteAttribute(w, "displayWindow", "box2i", box.ToArray());
                WriteAttribute(w, "lineOrder", "lineOrder", new byte[] { 0 });
                WriteAttribute(w, "pixelAspectRatio", "float", BitConverter.GetBytes(1.0f));
                WriteAttribute(w, "screenWindowCenter", "v2f", new byte[8]);
                WriteAttribute(w, "screenWindowWidth", "float", BitConverter.GetBytes(1.0f));
                w.Write((byte)0);

                int linesPerChunk = compress ? 16 : 1;
                int chunkCount = (height + linesPerChunk - 1) / linesPerChunk;
                long tablePosition = ms.Position;
                for (int i = 0; i < chunkCount; i++) w.Write((ulong)0);
                var offsets = new ulong[chunkCount];

                int lineBytes = width * 6;
                for (int chunk = 0; chunk < chunkCount; chunk++)
                {
                    int y0 = chunk * linesPerChunk;
                    int lines = Math.Min(linesPerChunk, height - y0);
                    var raw = new byte[lines * lineBytes];
                    int o = 0;
                    for (int l = 0; l < lines; l++)
                    {
                        int rowStart = (y0 + l) * width * 3;
                        for (int channel = 2; channel >= 0; channel--)    // B, G, R
                        {
                            for (int x = 0; x < width; x++)
                            {
                                float v = image.Rgb[rowStart + x * 3 + channel];
                                if (float.IsNaN(v) || v < 0) v = 0;
                                ushort h = Mathf.FloatToHalf(v);
                                raw[o++] = (byte)h;
                                raw[o++] = (byte)(h >> 8);
                            }
                        }
                    }
                    byte[] packed = raw;
                    if (compress)
                    {
                        var z = Zlib.Deflate(Predict(raw));
                        if (z.Length < raw.Length) packed = z;
                    }
                    offsets[chunk] = (ulong)ms.Position;
                    w.Write(y0);
                    w.Write(packed.Length);
                    w.Write(packed);
                }
                ms.Position = tablePosition;
                foreach (ulong offset in offsets) w.Write(offset);
                w.Flush();
                File.WriteAllBytes(path, ms.ToArray());
            }
        }

        // The format's byte reordering before deflate: split into two halves (even bytes, odd bytes), then deltas.
        static byte[] Predict(byte[] raw)
        {
            int n = raw.Length;
            var t = new byte[n];
            int half = (n + 1) / 2;
            int t1 = 0, t2 = half;
            for (int i = 0; i < n; i++)
            {
                if ((i & 1) == 0) t[t1++] = raw[i];
                else t[t2++] = raw[i];
            }
            int previous = t[0];
            for (int i = 1; i < n; i++)
            {
                int d = t[i] - previous + (128 + 256);
                previous = t[i];
                t[i] = (byte)d;
            }
            return t;
        }

        static byte[] Unpredict(byte[] t)
        {
            int n = t.Length;
            for (int i = 1; i < n; i++)
            {
                t[i] = (byte)(t[i - 1] + t[i] - 128);
            }
            var raw = new byte[n];
            int half = (n + 1) / 2;
            int t1 = 0, t2 = half;
            for (int i = 0; i < n; i++)
            {
                raw[i] = (i & 1) == 0 ? t[t1++] : t[t2++];
            }
            return raw;
        }

        static byte[] RleDecode(byte[] data, int offset, int length, int expectedSize)
        {
            var output = new byte[expectedSize];
            int o = 0, i = offset, end = offset + length;
            while (i < end)
            {
                sbyte code = (sbyte)data[i++];
                if (code < 0)
                {
                    int count = -code;
                    if (o + count > expectedSize) throw new InvalidDataException("RLE data overruns the chunk");
                    Array.Copy(data, i, output, o, count);
                    i += count;
                    o += count;
                }
                else
                {
                    int count = code + 1;
                    if (o + count > expectedSize) throw new InvalidDataException("RLE data overruns the chunk");
                    byte value = data[i++];
                    for (int k = 0; k < count; k++) output[o++] = value;
                }
            }
            if (o != expectedSize) throw new InvalidDataException("RLE chunk shorter than expected");
            return output;
        }

        static void WriteAttribute(BinaryWriter w, string name, string type, byte[] value)
        {
            w.Write(Encoding.ASCII.GetBytes(name));
            w.Write((byte)0);
            w.Write(Encoding.ASCII.GetBytes(type));
            w.Write((byte)0);
            w.Write(value.Length);
            w.Write(value);
        }

        static void WriteCString(Stream s, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            s.Write(bytes, 0, bytes.Length);
            s.WriteByte(0);
        }

        static void WriteI32(Stream s, int value)
        {
            s.Write(BitConverter.GetBytes(value), 0, 4);
        }

        static string ReadCString(byte[] data, ref int pos)
        {
            int start = pos;
            while (pos < data.Length && data[pos] != 0) pos++;
            string s = Encoding.ASCII.GetString(data, start, pos - start);
            pos++;
            return s;
        }

        static int ReadI32(byte[] data, ref int pos)
        {
            int v = BitConverter.ToInt32(data, pos);
            pos += 4;
            return v;
        }

        static uint ReadU32(byte[] data, ref int pos)
        {
            uint v = BitConverter.ToUInt32(data, pos);
            pos += 4;
            return v;
        }

        static ulong ReadU64(byte[] data, ref int pos)
        {
            ulong v = BitConverter.ToUInt64(data, pos);
            pos += 8;
            return v;
        }
    }
}
