// zlib streams (RFC 1950) over .NET's raw deflate, for the EXR ZIP chunks.

using System;
using System.IO;
using System.IO.Compression;

namespace Darkride.Exporter
{
    public static class Zlib
    {
        public static byte[] Deflate(byte[] input)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0xDA);
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
                {
                    ds.Write(input, 0, input.Length);
                }
                uint adler = Adler32(input, 0, input.Length);
                ms.WriteByte((byte)(adler >> 24));
                ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8));
                ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }

        public static byte[] Inflate(byte[] data, int offset, int length, int expectedSize)
        {
            if (length < 6) throw new InvalidDataException("zlib stream too short");
            int cmf = data[offset], flg = data[offset + 1];
            if ((cmf & 0x0f) != 8 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
                throw new InvalidDataException("not a zlib stream");
            var output = new byte[expectedSize];
            using (var src = new MemoryStream(data, offset + 2, length - 2, false))
            using (var ds = new DeflateStream(src, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < expectedSize)
                {
                    int n = ds.Read(output, read, expectedSize - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != expectedSize) throw new InvalidDataException("zlib stream shorter than expected: " + read + " of " + expectedSize);
            }
            return output;
        }

        public static uint Adler32(byte[] data, int offset, int length)
        {
            uint a = 1, b = 0;
            int end = offset + length;
            for (int i = offset; i < end; )
            {
                int block = Math.Min(5552, end - i);
                for (int j = 0; j < block; j++)
                {
                    a += data[i++];
                    b += a;
                }
                a %= 65521;
                b %= 65521;
            }
            return (b << 16) | a;
        }
    }
}
