using System.Buffers.Binary;
using System.IO.Compression;

namespace Glance3D
{
    // Minimal RGBA8 PNG encoder for --capture. The BCL already ships zlib (ZLibStream), so a
    // PNG is just three chunks around it. Cheaper than another package dependency.
    internal static class PngWriter
    {
        public static void Write(string path, int width, int height, byte[] rgba, bool flipVertically)
        {
            using var file = File.Create(path);
            file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

            var ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
            ihdr[8] = 8; // bit depth
            ihdr[9] = 6; // color type: RGBA
            WriteChunk(file, "IHDR", ihdr);

            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
                {
                    int stride = width * 4;
                    for (int y = 0; y < height; y++)
                    {
                        // GL reads bottom row first; PNG wants the top row first.
                        int src = (flipVertically ? height - 1 - y : y) * stride;
                        z.WriteByte(0); // filter: none
                        z.Write(rgba, src, stride);
                    }
                }
                WriteChunk(file, "IDAT", raw.ToArray());
            }

            WriteChunk(file, "IEND", []);
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            s.Write(len);

            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes);
            s.Write(data);

            uint crc = Crc32(Crc32(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            s.Write(crcBytes);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(uint crc, byte[] data)
        {
            foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
