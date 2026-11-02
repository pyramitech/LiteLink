using K4os.Compression.LZ4;
using System;
using System.Runtime.CompilerServices;

namespace LiteLink.Shared.Serializer
{
    /// <summary>
    /// Applies LiteLink's wire compression to an already-finalized message, mirroring what
    /// <see cref="LiteWriter.Close(int)"/> does inline: everything after <c>headerOffset</c> is
    /// LZ4-compressed behind a u32 uncompressed-size prefix, and the flag byte at
    /// <c>headerOffset - 1</c> is set. Used where the compression decision happens after a
    /// message is built - top-level response compression and batch envelopes.
    /// </summary>
    public static class LiteCompression
    {
        /// <summary>messages at or below this size are never compressed, matching LiteWriter.Close</summary>
        public const int Threshold = 80;

        public static byte[] Compress(byte[] data, int headerOffset)
        {
            if (data.Length <= Threshold)
                return data;

            var compressed = new byte[LZ4Codec.MaximumOutputSize(data.Length - headerOffset + 4)];
            var len = LZ4Codec.Encode(
                data, headerOffset, data.Length - headerOffset,
                compressed, 0, compressed.Length);

            //no size win, send uncompressed (same fallback as LiteWriter.Close)
            if (len > data.Length)
                return data;

            var dataStart = headerOffset + 4;
            var fin = new byte[len + dataStart];
            Array.Copy(data, 0, fin, 0, headerOffset);
            fin[headerOffset - 1] = 1; //isCompressed flag
            Unsafe.WriteUnaligned(ref fin[headerOffset], (uint)(data.Length - headerOffset));
            Array.Copy(compressed, 0, fin, dataStart, len);
            return fin;
        }
    }
}
