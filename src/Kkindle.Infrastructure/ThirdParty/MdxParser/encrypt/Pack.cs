using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MdxParser.encrypt
{
    internal static class Pack
    {
        internal static void UInt16_To_BE(ushort n, byte[] bs)
        {
            bs[0] = (byte)(n >> 8);
            bs[1] = (byte)n;
        }

        internal static void UInt16_To_BE(ushort n, byte[] bs, int off)
        {
            bs[off] = (byte)(n >> 8);
            bs[off + 1] = (byte)n;
        }

        internal static void UInt16_To_BE(ushort[] ns, byte[] bs, int off)
        {
            for (int i = 0; i < ns.Length; i++)
            {
                UInt16_To_BE(ns[i], bs, off);
                off += 2;
            }
        }

        internal static void UInt16_To_BE(ushort[] ns, int nsOff, int nsLen, byte[] bs, int bsOff)
        {
            for (int i = 0; i < nsLen; i++)
            {
                UInt16_To_BE(ns[nsOff + i], bs, bsOff);
                bsOff += 2;
            }
        }

        internal static byte[] UInt16_To_BE(ushort n)
        {
            byte[] array = new byte[2];
            UInt16_To_BE(n, array, 0);
            return array;
        }

        internal static byte[] UInt16_To_BE(ushort[] ns)
        {
            return UInt16_To_BE(ns, 0, ns.Length);
        }

        internal static byte[] UInt16_To_BE(ushort[] ns, int nsOff, int nsLen)
        {
            byte[] array = new byte[2 * nsLen];
            UInt16_To_BE(ns, nsOff, nsLen, array, 0);
            return array;
        }

        internal static ushort BE_To_UInt16(byte[] bs, int off)
        {
            return (ushort)((bs[off] << 8) | bs[off + 1]);
        }

        internal static void BE_To_UInt16(byte[] bs, int bsOff, ushort[] ns, int nsOff)
        {
            ns[nsOff] = BE_To_UInt16(bs, bsOff);
        }

        internal static ushort[] BE_To_UInt16(byte[] bs)
        {
            return BE_To_UInt16(bs, 0, bs.Length);
        }

        internal static ushort[] BE_To_UInt16(byte[] bs, int off, int len)
        {
            if (((uint)len & (true ? 1u : 0u)) != 0)
            {
                throw new ArgumentException("must be a multiple of 2", "len");
            }

            ushort[] array = new ushort[len / 2];
            for (int i = 0; i < len; i += 2)
            {
                BE_To_UInt16(bs, off + i, array, i >> 1);
            }

            return array;
        }

        internal static void UInt24_To_BE(uint n, byte[] bs)
        {
            bs[0] = (byte)(n >> 16);
            bs[1] = (byte)(n >> 8);
            bs[2] = (byte)n;
        }

        internal static void UInt24_To_BE(uint n, byte[] bs, int off)
        {
            bs[off] = (byte)(n >> 16);
            bs[off + 1] = (byte)(n >> 8);
            bs[off + 2] = (byte)n;
        }

        internal static uint BE_To_UInt24(byte[] bs)
        {
            return (uint)((bs[0] << 16) | (bs[1] << 8) | bs[2]);
        }

        internal static uint BE_To_UInt24(byte[] bs, int off)
        {
            return (uint)((bs[off] << 16) | (bs[off + 1] << 8) | bs[off + 2]);
        }

        internal static void UInt32_To_BE(uint n, byte[] bs)
        {
            bs[0] = (byte)(n >> 24);
            bs[1] = (byte)(n >> 16);
            bs[2] = (byte)(n >> 8);
            bs[3] = (byte)n;
        }

        internal static void UInt32_To_BE(uint n, byte[] bs, int off)
        {
            bs[off] = (byte)(n >> 24);
            bs[off + 1] = (byte)(n >> 16);
            bs[off + 2] = (byte)(n >> 8);
            bs[off + 3] = (byte)n;
        }

        internal static void UInt32_To_BE_High(uint n, byte[] bs, int off, int len)
        {
            int num = 24;
            bs[off] = (byte)(n >> num);
            for (int i = 1; i < len; i++)
            {
                num -= 8;
                bs[off + i] = (byte)(n >> num);
            }
        }

        internal static void UInt32_To_BE_Low(uint n, byte[] bs, int off, int len)
        {
            UInt32_To_BE_High(n << (4 - len << 3), bs, off, len);
        }

        internal static void UInt32_To_BE(uint[] ns, byte[] bs, int off)
        {
            for (int i = 0; i < ns.Length; i++)
            {
                UInt32_To_BE(ns[i], bs, off);
                off += 4;
            }
        }

        internal static void UInt32_To_BE(uint[] ns, int nsOff, int nsLen, byte[] bs, int bsOff)
        {
            for (int i = 0; i < nsLen; i++)
            {
                UInt32_To_BE(ns[nsOff + i], bs, bsOff);
                bsOff += 4;
            }
        }

        internal static byte[] UInt32_To_BE(uint n)
        {
            byte[] array = new byte[4];
            UInt32_To_BE(n, array, 0);
            return array;
        }

        internal static byte[] UInt32_To_BE(uint[] ns)
        {
            byte[] array = new byte[4 * ns.Length];
            UInt32_To_BE(ns, array, 0);
            return array;
        }

        internal static uint BE_To_UInt32(byte[] bs)
        {
            return (uint)((bs[0] << 24) | (bs[1] << 16) | (bs[2] << 8) | bs[3]);
        }

        internal static uint BE_To_UInt32(byte[] bs, int off)
        {
            return (uint)((bs[off] << 24) | (bs[off + 1] << 16) | (bs[off + 2] << 8) | bs[off + 3]);
        }

        internal static uint BE_To_UInt32_High(byte[] bs, int off, int len)
        {
            return BE_To_UInt32_Low(bs, off, len) << (4 - len << 3);
        }

        internal static uint BE_To_UInt32_Low(byte[] bs, int off, int len)
        {
            uint num = bs[off];
            for (int i = 1; i < len; i++)
            {
                num <<= 8;
                num |= bs[off + i];
            }

            return num;
        }

        internal static void BE_To_UInt32(byte[] bs, int off, uint[] ns)
        {
            for (int i = 0; i < ns.Length; i++)
            {
                ns[i] = BE_To_UInt32(bs, off);
                off += 4;
            }
        }

        internal static void BE_To_UInt32(byte[] bs, int bsOff, uint[] ns, int nsOff, int nsLen)
        {
            for (int i = 0; i < nsLen; i++)
            {
                ns[nsOff + i] = BE_To_UInt32(bs, bsOff);
                bsOff += 4;
            }
        }

        internal static byte[] UInt64_To_BE(ulong n)
        {
            byte[] array = new byte[8];
            UInt64_To_BE(n, array, 0);
            return array;
        }

        internal static void UInt64_To_BE(ulong n, byte[] bs)
        {
            UInt32_To_BE((uint)(n >> 32), bs);
            UInt32_To_BE((uint)n, bs, 4);
        }

        internal static void UInt64_To_BE(ulong n, byte[] bs, int off)
        {
            UInt32_To_BE((uint)(n >> 32), bs, off);
            UInt32_To_BE((uint)n, bs, off + 4);
        }

        internal static void UInt64_To_BE_High(ulong n, byte[] bs, int off, int len)
        {
            int num = 56;
            bs[off] = (byte)(n >> num);
            for (int i = 1; i < len; i++)
            {
                num -= 8;
                bs[off + i] = (byte)(n >> num);
            }
        }

        internal static void UInt64_To_BE_Low(ulong n, byte[] bs, int off, int len)
        {
            UInt64_To_BE_High(n << (8 - len << 3), bs, off, len);
        }

        internal static byte[] UInt64_To_BE(ulong[] ns)
        {
            byte[] array = new byte[8 * ns.Length];
            UInt64_To_BE(ns, array, 0);
            return array;
        }

        internal static void UInt64_To_BE(ulong[] ns, byte[] bs, int off)
        {
            for (int i = 0; i < ns.Length; i++)
            {
                UInt64_To_BE(ns[i], bs, off);
                off += 8;
            }
        }

        internal static void UInt64_To_BE(ulong[] ns, int nsOff, int nsLen, byte[] bs, int bsOff)
        {
            for (int i = 0; i < nsLen; i++)
            {
                UInt64_To_BE(ns[nsOff + i], bs, bsOff);
                bsOff += 8;
            }
        }

        internal static ulong BE_To_UInt64(byte[] bs)
        {
            uint num = BE_To_UInt32(bs);
            uint num2 = BE_To_UInt32(bs, 4);
            return ((ulong)num << 32) | num2;
        }

        internal static ulong BE_To_UInt64(byte[] bs, int off)
        {
            uint num = BE_To_UInt32(bs, off);
            uint num2 = BE_To_UInt32(bs, off + 4);
            return ((ulong)num << 32) | num2;
        }

        internal static ulong BE_To_UInt64_High(byte[] bs, int off, int len)
        {
            return BE_To_UInt64_Low(bs, off, len) << (8 - len << 3);
        }

        internal static ulong BE_To_UInt64_Low(byte[] bs, int off, int len)
        {
            ulong num = bs[off];
            for (int i = 1; i < len; i++)
            {
                num <<= 8;
                num |= bs[off + i];
            }

            return num;
        }

        internal static void BE_To_UInt64(byte[] bs, int off, ulong[] ns)
        {
            for (int i = 0; i < ns.Length; i++)
            {
                ns[i] = BE_To_UInt64(bs, off);
                off += 8;
            }
        }

        internal static void BE_To_UInt64(byte[] bs, int bsOff, ulong[] ns, int nsOff, int nsLen)
        {
            for (int i = 0; i < nsLen; i++)
            {
                ns[nsOff + i] = BE_To_UInt64(bs, bsOff);
                bsOff += 8;
            }
        }

        internal static void UInt16_To_LE(ushort n, byte[] bs)
        {
            bs[0] = (byte)n;
            bs[1] = (byte)(n >> 8);
        }

        internal static void UInt16_To_LE(ushort n, byte[] bs, int off)
        {
            bs[off] = (byte)n;
            bs[off + 1] = (byte)(n >> 8);
        }

        internal static byte[] UInt16_To_LE(ushort n)
        {
            byte[] array = new byte[2];
            UInt16_To_LE(n, array, 0);
            return array;
        }

        internal static byte[] UInt16_To_LE(ushort[] ns)
        {
            byte[] array = new byte[2 * ns.Length];
            UInt16_To_LE(ns, array, 0);
            return array;
        }

        internal static void UInt16_To_LE(ushort[] ns, byte[] bs, int off)
        {
            for (int i = 0; i < ns.Length; i++)
            {
                UInt16_To_LE(ns[i], bs, off);
                off += 2;
            }
        }

        internal static void UInt16_To_LE(ushort[] ns, int nsOff, int nsLen, byte[] bs, int bsOff)
        {
            for (int i = 0; i < nsLen; i++)
            {
                UInt16_To_LE(ns[nsOff + i], bs, bsOff);
                bsOff += 2;
            }
        }

        internal static ushort LE_To_UInt16(byte[] bs)
        {
            return (ushort)(bs[0] | (bs[1] << 8));
        }

        internal static ushort LE_To_UInt16(byte[] bs, int off)
        {
            return (ushort)(bs[off] | (bs[off + 1] << 8));
        }

        internal static void LE_To_UInt16(byte[] bs, int off, ushort[] ns)
        {
            for (int i = 0; i < ns.Length; i++)
            {
                ns[i] = LE_To_UInt16(bs, off);
                off += 2;
            }
        }

        internal static void LE_To_UInt16(byte[] bs, int bOff, ushort[] ns, int nOff, int count)
        {
            for (int i = 0; i < count; i++)
            {
                ns[nOff + i] = LE_To_UInt16(bs, bOff);
                bOff += 2;
            }
        }

        internal static ushort[] LE_To_UInt16(byte[] bs, int off, int count)
        {
            ushort[] array = new ushort[count];
            LE_To_UInt16(bs, off, array);
            return array;
        }

        internal static byte[] UInt32_To_LE(uint n)
        {
            byte[] array = new byte[4];
            UInt32_To_LE(n, array, 0);
            return array;
        }

        internal static void UInt32_To_LE(uint n, byte[] bs)
        {
            bs[0] = (byte)n;
            bs[1] = (byte)(n >> 8);
            bs[2] = (byte)(n >> 16);
            bs[3] = (byte)(n >> 24);
        }

        internal static void UInt32_To_LE(uint n, byte[] bs, int off)
        {
            bs[off] = (byte)n;
            bs[off + 1] = (byte)(n >> 8);
            bs[off + 2] = (byte)(n >> 16);
            bs[off + 3] = (byte)(n >> 24);
        }

        internal static byte[] UInt32_To_LE(uint[] ns)
        {
            byte[] array = new byte[4 * ns.Length];
            UInt32_To_LE(ns, array, 0);
            return array;
        }

        internal static void UInt32_To_LE(uint[] ns, byte[] bs, int off)
        {
            for (int i = 0; i < ns.Length; i++)
            {
                UInt32_To_LE(ns[i], bs, off);
                off += 4;
            }
        }

        internal static void UInt32_To_LE(uint[] ns, int nsOff, int nsLen, byte[] bs, int bsOff)
        {
            for (int i = 0; i < nsLen; i++)
            {
                UInt32_To_LE(ns[nsOff + i], bs, bsOff);
                bsOff += 4;
            }
        }

        internal static uint LE_To_UInt24(byte[] bs, int off)
        {
            return (uint)(bs[off] | (bs[off + 1] << 8) | (bs[off + 2] << 16));
        }

        internal static uint LE_To_UInt32(byte[] bs)
        {
            return (uint)(bs[0] | (bs[1] << 8) | (bs[2] << 16) | (bs[3] << 24));
        }

        internal static uint LE_To_UInt32(byte[] bs, int off)
        {
            return (uint)(bs[off] | (bs[off + 1] << 8) | (bs[off + 2] << 16) | (bs[off + 3] << 24));
        }

        internal static void LE_To_UInt32(byte[] bs, int off, uint[] ns)
        {
            for (int i = 0; i < ns.Length; i++)
            {
                ns[i] = LE_To_UInt32(bs, off);
                off += 4;
            }
        }

        internal static void LE_To_UInt32(byte[] bs, int bOff, uint[] ns, int nOff, int count)
        {
            for (int i = 0; i < count; i++)
            {
                ns[nOff + i] = LE_To_UInt32(bs, bOff);
                bOff += 4;
            }
        }

        internal static uint[] LE_To_UInt32(byte[] bs, int off, int count)
        {
            uint[] array = new uint[count];
            LE_To_UInt32(bs, off, array);
            return array;
        }

        internal static byte[] UInt64_To_LE(ulong n)
        {
            byte[] array = new byte[8];
            UInt64_To_LE(n, array, 0);
            return array;
        }

        internal static void UInt64_To_LE(ulong n, byte[] bs)
        {
            UInt32_To_LE((uint)n, bs);
            UInt32_To_LE((uint)(n >> 32), bs, 4);
        }

        internal static void UInt64_To_LE(ulong n, byte[] bs, int off)
        {
            UInt32_To_LE((uint)n, bs, off);
            UInt32_To_LE((uint)(n >> 32), bs, off + 4);
        }

        internal static byte[] UInt64_To_LE(ulong[] ns)
        {
            byte[] array = new byte[8 * ns.Length];
            UInt64_To_LE(ns, array, 0);
            return array;
        }

        internal static void UInt64_To_LE(ulong[] ns, byte[] bs, int off)
        {
            for (int i = 0; i < ns.Length; i++)
            {
                UInt64_To_LE(ns[i], bs, off);
                off += 8;
            }
        }

        internal static void UInt64_To_LE(ulong[] ns, int nsOff, int nsLen, byte[] bs, int bsOff)
        {
            for (int i = 0; i < nsLen; i++)
            {
                UInt64_To_LE(ns[nsOff + i], bs, bsOff);
                bsOff += 8;
            }
        }

        internal static ulong LE_To_UInt64(byte[] bs)
        {
            uint num = LE_To_UInt32(bs);
            return ((ulong)LE_To_UInt32(bs, 4) << 32) | num;
        }

        internal static ulong LE_To_UInt64(byte[] bs, int off)
        {
            uint num = LE_To_UInt32(bs, off);
            return ((ulong)LE_To_UInt32(bs, off + 4) << 32) | num;
        }

        internal static void LE_To_UInt64(byte[] bs, int off, ulong[] ns)
        {
            for (int i = 0; i < ns.Length; i++)
            {
                ns[i] = LE_To_UInt64(bs, off);
                off += 8;
            }
        }

        internal static void LE_To_UInt64(byte[] bs, int bsOff, ulong[] ns, int nsOff, int nsLen)
        {
            for (int i = 0; i < nsLen; i++)
            {
                ns[nsOff + i] = LE_To_UInt64(bs, bsOff);
                bsOff += 8;
            }
        }

        internal static ulong[] LE_To_UInt64(byte[] bs, int off, int count)
        {
            ulong[] array = new ulong[count];
            LE_To_UInt64(bs, off, array);
            return array;
        }
    }
}
