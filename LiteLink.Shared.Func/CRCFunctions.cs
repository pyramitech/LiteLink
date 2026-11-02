using System;
using System.Collections.Generic;
using System.Text;

namespace LiteLink.Shared.Func
{
    public static class CRCFunctions
    {
        public static byte CalculateCRC8(byte[] data)
        {
            byte crc = 0xFF;
            byte polynomial = 0x07; // CRC8 polynomial

            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x80) != 0)
                    {
                        crc = (byte)((crc << 1) ^ polynomial);
                    }
                    else
                    {
                        crc <<= 1;
                    }
                }
            }

            return crc;
        }

        public static ushort CalculateCRC16(byte[] data)
        {
            ushort crc = 0xFFFF;
            ushort polynomial = 0x1021; // CRC16 polynomial

            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x8000) != 0)
                    {
                        crc = (ushort)((crc << 1) ^ polynomial);
                    }
                    else
                    {
                        crc <<= 1;
                    }
                }
            }

            return crc;
        }

        public static uint CalculateCRC32(byte[] data)
        {
            uint[] crcTable = new uint[256];
            uint polynomial = 0xEDB88320; // CRC32 polynomial
            uint crc = 0xFFFFFFFF;

            for (uint i = 0; i < 256; i++)
            {
                uint crcValue = i;
                for (int j = 0; j < 8; j++)
                {
                    crcValue = (crcValue & 1) != 0 ? (crcValue >> 1) ^ polynomial : crcValue >> 1;
                }
                crcTable[i] = crcValue;
            }

            foreach (byte b in data)
            {
                crc = (crc >> 8) ^ crcTable[(crc ^ b) & 0xFF];
            }

            return ~crc;
        }

        public static ulong CalculateCRC64(byte[] data)
        {
            ulong[] crcTable = new ulong[256];
            ulong polynomial = 0xC96C5795D7870F42; // CRC64 polynomial
            ulong crc = 0xFFFFFFFFFFFFFFFF;

            for (uint i = 0; i < 256; i++)
            {
                ulong crcValue = i;
                for (int j = 0; j < 8; j++)
                {
                    crcValue = (crcValue & 1) != 0 ? (crcValue >> 1) ^ polynomial : crcValue >> 1;
                }
                crcTable[i] = crcValue;
            }

            foreach (byte b in data)
            {
                crc = (crc >> 8) ^ crcTable[(crc ^ b) & 0xFF];
            }

            return ~crc;
        }

        public static byte CalculateCRC8(string input)
        {
            byte[] data = Encoding.ASCII.GetBytes(input);
            return CalculateCRC8(data);
        }

        public static ushort CalculateCRC16(string input)
        {
            byte[] data = Encoding.ASCII.GetBytes(input);
            return CalculateCRC16(data);
        }

        public static uint CalculateCRC32(string input)
        {
            byte[] data = Encoding.ASCII.GetBytes(input);
            return CalculateCRC32(data);
        }

        public static ulong CalculateCRC64(string input)
        {
            byte[] data = Encoding.ASCII.GetBytes(input);
            return CalculateCRC64(data);
        }
    }
}
