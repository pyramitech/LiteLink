using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections;
using K4os.Compression.LZ4;
using static LiteLink.Shared.Serializer.LiteSerializer;

namespace LiteLink.Shared.Serializer
{
    public ref partial struct LiteWriter
    {
        
        private byte[] array;
        private Buffer buffer;
        private List<Buffer> buffers = new List<Buffer>();
        private int index = 0;
        private int length = 0;

        public LiteWriter() {
            buffer = new Buffer();
            array = buffer.Array;
            length = array.Length;
        }

        /// <summary>
        /// Writing will begin within the supplied array
        /// </summary>
        /// <param name="array"></param>
        public LiteWriter(byte[] arr)
        {
            buffer = new Buffer(arr);
            array = buffer.Array;
            length = array.Length;
        }

        internal LiteWriter(Buffer buffer)
        {
            this.buffer = buffer;
            array = buffer.Array;
            length = array.Length;
        }

        public int Length => length;

        public unsafe byte[] Close(int headerOffset = -1)
        {
            var finSize = buffers.Sum(x => x.Length) + index;
            var arr = new byte[finSize];
            var ix = 0;
            
            for(int i = 0; i < buffers.Count; i++)
            {
                var buf = buffers[i];
                Array.Copy(buf.Array, 0, arr, ix, buf.Length);
                ix += buf.Length;
            }

            Array.Copy(array, 0, arr, ix, index);

            if (headerOffset != -1 && finSize > 80)
            {

                //Unsafe.WriteUnaligned(arr, value);

                var compressed = new byte[LZ4Codec.MaximumOutputSize(arr.Length - headerOffset + 4)];
                var len = LZ4Codec.Encode(
                    arr, headerOffset, arr.Length - headerOffset,
                    compressed, 0, compressed.Length);

                //size didn't change, send uncompressed
                if (len > arr.Length)
                    return arr;

                //set header isCompressed
                arr[headerOffset - 1] = 1; //headerOffset - 1 = isCompressed

                var dataStart = headerOffset + 4;

                var finRes = new byte[len + dataStart];
                Array.Copy(arr, 0, finRes, 0, headerOffset);
                fixed (byte* pArr = &finRes[headerOffset])
                {
                    Unsafe.WriteUnaligned(pArr, (uint)(arr.Length - headerOffset)); //uint32 for uncompressed length
                }
                Array.Copy(compressed, 0, finRes, dataStart, len);
                return finRes;
            }

            return arr;
        }

        public void WriteBoolean(bool value)
        {
            if (index + sizeof(sbyte) > length)
            {
                ExpandBuffer();
            }
            array[index++] = (byte)(value ? 1 : 0);
        }

        public void WriteSByte(sbyte value)
        {
            if (index + sizeof(sbyte) > length)
            {
                ExpandBuffer();
            }
            array[index++] = (byte)(value & 0xFF);
        }

        public void WriteByte(byte value)
        {
            if (index + sizeof(byte) > length)
            {
                ExpandBuffer();
            }
            array[index++] = value;
        }

        public unsafe void WriteRaw<T>(T value)
        {
            if (index + sizeof(T) > length)
            {
                ExpandBuffer();
            }

            fixed (byte* arr = &array[index])
            {
                Unsafe.WriteUnaligned(arr, value);
            }
            index += sizeof(T);
        }

        public void WriteInt16(short value)
        {
            WriteRaw(value);
        }

        public void WriteUInt16(ushort value)
        {
            WriteRaw(value);
        }

        public void WriteInt32(int value)
        {
            WriteRaw(value);
        }

        public void WriteUInt32(uint value)
        {
            WriteRaw(value);
        }

        public void WriteInt64(long value)
        {
            WriteRaw(value);
        }

        public void WriteUInt64(ulong value)
        {
            WriteRaw(value);
        }

        public void WriteSingle(float value)
        {
            WriteRaw(value);
        }

        public void WriteDouble(double value)
        {
            WriteRaw(value);
        }

        public unsafe void WriteLength(ulong len)
        {
            if (index + 8 > length)
            {
                ExpandBuffer();
            }

            //TODO: might be faster to reverse byte order before writing

            //We need to write in big byte order so the first byte is always in the same position
            if (len < 0x80)
            {
                len |= 0x80;
                array[index++] = (byte)len;
            }
            else if (len < 0x4000)
            {
                len |= 0x4000;
                array[index++] = (byte)(len >> 8);
                array[index++] = (byte)(len);
                //fixed (byte* arr = &array[index])
                //{
                //    Unsafe.WriteUnaligned(arr, (ushort)len);
                //}
                //index += sizeof(ushort);
            }
            else if (len < 0x20000000)
            {
                len |= 0x20000000;

                array[index++] = (byte)(len >> 24);
                array[index++] = (byte)(len >> 16);
                array[index++] = (byte)(len >> 8);
                array[index++] = (byte)(len);
                //fixed (byte* arr = &array[index])
                //{
                //    Unsafe.WriteUnaligned(arr, (uint)len);
                //}
                //index += sizeof(uint);
            }
            else if (len < 0x1000000000000000)
            {
                //fixed (byte* arr = &array[index])
                //{
                //    Unsafe.WriteUnaligned(arr, len);
                //}
                //index += sizeof(ulong);

                array[index++] = (byte)(len >> 56);
                array[index++] = (byte)(len >> 48);
                array[index++] = (byte)(len >> 40);
                array[index++] = (byte)(len >> 32);
                array[index++] = (byte)(len >> 24);
                array[index++] = (byte)(len >> 16);
                array[index++] = (byte)(len >> 8);
                array[index++] = (byte)(len);
            }
            else //max value == null
            {
                array[index++] = 0;
            }
        }

        public unsafe void WriteString(string value)
        {
            if (value == null)
            {
                WriteLength(ulong.MaxValue);
                return;
            }

            int strLen = Encoding.UTF8.GetByteCount(value) + 8; //for size testing, 8 might not be actual string length

            if (index + strLen > length)
            {
                if (strLen > Buffer.BUFFER_SIZE)
                    ExpandBuffer(strLen);
                else
                    ExpandBuffer();
            }

            //we added 8 for the size in buffer (4 bytes for len), now we remove it
            strLen -= 8;

            WriteLength((uint)strLen);

            Encoding.UTF8.GetBytes(value, 0, value.Length, array, index);
            index += strLen;
        }

        /// <summary>
        /// runtime fallback for nullables outside generated code (e.g. as collection/dictionary/tuple
        /// elements); same wire format as the generator emits: [bool hasValue][value]
        /// </summary>
        public void WriteNullable<T>(T? value) where T : struct
        {
            if (value.HasValue)
            {
                WriteBoolean(true);
                var writeFunction = LiteSerializer.GetWriterDelegate<T>();
                writeFunction(ref this, value.Value);
            }
            else
            {
                WriteBoolean(false);
            }
        }

        public unsafe void WriteDecimal(decimal value)
        {
            //12 = long + int
            if (index + 12 > length)
            {
                ExpandBuffer();
            }
            
            var integerPart = (long)value;
            var decimalPart = (int)((value - integerPart) * 1_000_000_000m);

            fixed (byte* arr = &array[index])
            {
                Unsafe.WriteUnaligned(arr, integerPart);
                Unsafe.WriteUnaligned(arr + 8, decimalPart);
            }

            index += 12;
        }

        public void WriteDate(DateTime dateTime)
        {
            //write as UTC
            WriteInt64(dateTime.ToUniversalTime().ToBinary());
        }

        public void WriteGuid(Guid guid)
        {
            WriteRaw(guid);
        }

        public void WriteCollection<T, TChild>(T list) where T : IList<TChild>
        {            
            if (list == null)
            {
                WriteLength(ulong.MaxValue);
                return;
            }
            else
            {
                WriteLength((ulong)list.Count);
                var writeFunction = LiteSerializer.GetWriterDelegate<TChild>();
                for (int i = 0; i < list.Count; i++)
                {
                    writeFunction(ref this, list[i]);
                }
            }
        }

        public void WriteCollection<T>(IList<T> list, WriteFunctionDelegate<T> writeFunction)
        {
            if (list == null)
            {
                WriteLength(ulong.MaxValue);
                return;
            }
            else
            {
                WriteLength((ulong)list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    writeFunction(ref this, list[i]);
                }
            }
        }

        public void WriteDictionary<TKey, TValue>(Dictionary<TKey, TValue> list)
        {
            if (list == null)
            {
                WriteLength(ulong.MaxValue);
                return;
            }
            else
            {
                WriteLength((ulong)list.Count);
                var keyFunction = LiteSerializer.GetWriterDelegate<TKey>();
                var valueFunction = LiteSerializer.GetWriterDelegate<TValue>();
                foreach (var kvp in list)
                {
                    keyFunction(ref this, kvp.Key);
                    valueFunction(ref this, kvp.Value);
                }
            }
        }

        public void WriteDictionary<TKey, TValue>(Dictionary<TKey, TValue> list, 
            WriteFunctionDelegate<TKey> keyFunction, 
            WriteFunctionDelegate<TValue> valueFunction)
        {
            if (list == null)
            {
                WriteLength(ulong.MaxValue);
                return;
            }
            else
            {
                WriteLength((ulong)list.Count);
                foreach (var kvp in list)
                {
                    keyFunction(ref this, kvp.Key);
                    valueFunction(ref this, kvp.Value);
                }
            }
        }

        //public void WriteObject<T>(T obj) //where T : object
        //{
        //    if (obj == null)
        //    {
        //        WriteByte(0);
        //        return;
        //    }
        //    else
        //    {
        //        WriteByte(1);
        //        obj.Write(this);               
        //    }
        //}

        public unsafe void WriteByteArray(ReadOnlySpan<byte> bytes)
        {
            int strLen = bytes.Length + 8;
            if (index + strLen > length)
            {
                if (strLen >= Buffer.BUFFER_SIZE)
                {
                    ExpandBuffer(strLen);
                }
                else
                {
                    ExpandBuffer();
                }
            }
            WriteLength((uint)bytes.Length);
            var target = new Span<byte>(array, index, bytes.Length);
            bytes.CopyTo(target);
            index += bytes.Length;
        }

        /// <summary>
        /// writes a byte array without the preceding length. This should only be used when manually managing serialization and you know the length of the array
        /// </summary>
        /// <param name="bytes"></param>
        public unsafe void WriteFixedByteArray(ReadOnlySpan<byte> bytes)
        {
            if (index + bytes.Length > length)
            {
                if (bytes.Length >= Buffer.BUFFER_SIZE)
                {
                    ExpandBuffer(bytes.Length);
                }
                else
                {
                    ExpandBuffer();
                }
            }
            var target = new Span<byte>(array, index, bytes.Length);
            bytes.CopyTo(target);
            index += bytes.Length;
        }

        private void ExpandBuffer()
        {
            buffer.Length = index;
            buffers.Add(buffer);

            buffer = new Buffer();
            array = buffer.Array;
            index = 0;
        }

        private void ExpandBuffer(int len)
        {
            buffer.Length = index;
            buffers.Add(buffer);

            buffer = new Buffer(len);
            array = buffer.Array;
            index = 0;
        }

        #region Tuples
        public void WriteValueTuple<T>(ValueTuple<T> tuple)
        {
            var T1value = (T)tuple.Item1;

            var writeFunction = LiteSerializer.GetWriterDelegate<T>();
            writeFunction(ref this, T1value);
        }

        public void WriteValueTuple<T1, T2>(ValueTuple<T1, T2> tuple)
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;

            var writeFunction1 = LiteSerializer.GetWriterDelegate<T1>();
            var writeFunction2 = LiteSerializer.GetWriterDelegate<T2>();
            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
        }

        public void WriteValueTuple<T1, T2, T3>(ValueTuple<T1, T2, T3> tuple)
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;
            var T3value = tuple.Item3;

            var writeFunction1 = LiteSerializer.GetWriterDelegate<T1>();
            var writeFunction2 = LiteSerializer.GetWriterDelegate<T2>();
            var writeFunction3 = LiteSerializer.GetWriterDelegate<T3>();
            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
            writeFunction3(ref this, T3value);
        }

        public void WriteValueTuple<T1, T2, T3, T4>(ValueTuple<T1, T2, T3, T4> tuple)
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;
            var T3value = tuple.Item3;
            var T4value = tuple.Item4;

            var writeFunction1 = LiteSerializer.GetWriterDelegate<T1>();
            var writeFunction2 = LiteSerializer.GetWriterDelegate<T2>();
            var writeFunction3 = LiteSerializer.GetWriterDelegate<T3>();
            var writeFunction4 = LiteSerializer.GetWriterDelegate<T4>();
            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
            writeFunction3(ref this, T3value);
            writeFunction4(ref this, T4value);
        }

        public void WriteValueTuple<T1, T2, T3, T4, T5>(ValueTuple<T1, T2, T3, T4, T5> tuple)
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;
            var T3value = tuple.Item3;
            var T4value = tuple.Item4;
            var T5value = tuple.Item5;

            var writeFunction1 = LiteSerializer.GetWriterDelegate<T1>();
            var writeFunction2 = LiteSerializer.GetWriterDelegate<T2>();
            var writeFunction3 = LiteSerializer.GetWriterDelegate<T3>();
            var writeFunction4 = LiteSerializer.GetWriterDelegate<T4>();
            var writeFunction5 = LiteSerializer.GetWriterDelegate<T5>();
            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
            writeFunction3(ref this, T3value);
            writeFunction4(ref this, T4value);
            writeFunction5(ref this, T5value);
        }

        public void WriteValueTuple<T1, T2, T3, T4, T5, T6>(ValueTuple<T1, T2, T3, T4, T5, T6> tuple)
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;
            var T3value = tuple.Item3;
            var T4value = tuple.Item4;
            var T5value = tuple.Item5;
            var T6value = tuple.Item6;

            var writeFunction1 = LiteSerializer.GetWriterDelegate<T1>();
            var writeFunction2 = LiteSerializer.GetWriterDelegate<T2>();
            var writeFunction3 = LiteSerializer.GetWriterDelegate<T3>();
            var writeFunction4 = LiteSerializer.GetWriterDelegate<T4>();
            var writeFunction5 = LiteSerializer.GetWriterDelegate<T5>();
            var writeFunction6 = LiteSerializer.GetWriterDelegate<T6>();
            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
            writeFunction3(ref this, T3value);
            writeFunction4(ref this, T4value);
            writeFunction5(ref this, T5value);
            writeFunction6(ref this, T6value);
        }

        public void WriteValueTuple<T1, T2, T3, T4, T5, T6, T7>(ValueTuple<T1, T2, T3, T4, T5, T6, T7> tuple)
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;
            var T3value = tuple.Item3;
            var T4value = tuple.Item4;
            var T5value = tuple.Item5;
            var T6value = tuple.Item6;
            var T7value = tuple.Item7;

            var writeFunction1 = LiteSerializer.GetWriterDelegate<T1>();
            var writeFunction2 = LiteSerializer.GetWriterDelegate<T2>();
            var writeFunction3 = LiteSerializer.GetWriterDelegate<T3>();
            var writeFunction4 = LiteSerializer.GetWriterDelegate<T4>();
            var writeFunction5 = LiteSerializer.GetWriterDelegate<T5>();
            var writeFunction6 = LiteSerializer.GetWriterDelegate<T6>();
            var writeFunction7 = LiteSerializer.GetWriterDelegate<T7>();
            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
            writeFunction3(ref this, T3value);
            writeFunction4(ref this, T4value);
            writeFunction5(ref this, T5value);
            writeFunction6(ref this, T6value);
            writeFunction7(ref this, T7value);
        }

        public void WriteValueTuple<T1, T2, T3, T4, T5, T6, T7, T8>(ValueTuple<T1, T2, T3, T4, T5, T6, T7, T8> tuple) where T8 : struct
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;
            var T3value = tuple.Item3;
            var T4value = tuple.Item4;
            var T5value = tuple.Item5;
            var T6value = tuple.Item6;
            var T7value = tuple.Item7;
            var T8value = tuple.Rest;

            var writeFunction1 = LiteSerializer.GetWriterDelegate<T1>();
            var writeFunction2 = LiteSerializer.GetWriterDelegate<T2>();
            var writeFunction3 = LiteSerializer.GetWriterDelegate<T3>();
            var writeFunction4 = LiteSerializer.GetWriterDelegate<T4>();
            var writeFunction5 = LiteSerializer.GetWriterDelegate<T5>();
            var writeFunction6 = LiteSerializer.GetWriterDelegate<T6>();
            var writeFunction7 = LiteSerializer.GetWriterDelegate<T7>();
            var writeFunction8 = LiteSerializer.GetWriterDelegate<T8>();
            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
            writeFunction3(ref this, T3value);
            writeFunction4(ref this, T4value);
            writeFunction5(ref this, T5value);
            writeFunction6(ref this, T6value);
            writeFunction7(ref this, T7value);
            writeFunction8(ref this, T8value);
        }

        public void WriteValueTuple<T>(ValueTuple<T> tuple, WriteFunctionDelegate<T> writeFunction)
        {
            var T1value = tuple.Item1;

            writeFunction(ref this, T1value);
        }

        public void WriteValueTuple<T1, T2>(ValueTuple<T1, T2> tuple,
            WriteFunctionDelegate<T1> writeFunction1,
            WriteFunctionDelegate<T2> writeFunction2)
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;

            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
        }

        public void WriteValueTuple<T1, T2, T3>(ValueTuple<T1, T2, T3> tuple,
            WriteFunctionDelegate<T1> writeFunction1,
            WriteFunctionDelegate<T2> writeFunction2,
            WriteFunctionDelegate<T3> writeFunction3)
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;
            var T3value = tuple.Item3;

            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
            writeFunction3(ref this, T3value);
        }

        public void WriteValueTuple<T1, T2, T3, T4>(ValueTuple<T1, T2, T3, T4> tuple,
            WriteFunctionDelegate<T1> writeFunction1,
            WriteFunctionDelegate<T2> writeFunction2,
            WriteFunctionDelegate<T3> writeFunction3,
            WriteFunctionDelegate<T4> writeFunction4)
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;
            var T3value = tuple.Item3;
            var T4value = tuple.Item4;

            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
            writeFunction3(ref this, T3value);
            writeFunction4(ref this, T4value);
        }

        public void WriteValueTuple<T1, T2, T3, T4, T5>(ValueTuple<T1, T2, T3, T4, T5> tuple,
            WriteFunctionDelegate<T1> writeFunction1,
            WriteFunctionDelegate<T2> writeFunction2,
            WriteFunctionDelegate<T3> writeFunction3,
            WriteFunctionDelegate<T4> writeFunction4,
            WriteFunctionDelegate<T5> writeFunction5)
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;
            var T3value = tuple.Item3;
            var T4value = tuple.Item4;
            var T5value = tuple.Item5;

            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
            writeFunction3(ref this, T3value);
            writeFunction4(ref this, T4value);
            writeFunction5(ref this, T5value);
        }

        public void WriteValueTuple<T1, T2, T3, T4, T5, T6>(ValueTuple<T1, T2, T3, T4, T5, T6> tuple,
            WriteFunctionDelegate<T1> writeFunction1,
            WriteFunctionDelegate<T2> writeFunction2,
            WriteFunctionDelegate<T3> writeFunction3,
            WriteFunctionDelegate<T4> writeFunction4,
            WriteFunctionDelegate<T5> writeFunction5,
            WriteFunctionDelegate<T6> writeFunction6)
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;
            var T3value = tuple.Item3;
            var T4value = tuple.Item4;
            var T5value = tuple.Item5;
            var T6value = tuple.Item6;

            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
            writeFunction3(ref this, T3value);
            writeFunction4(ref this, T4value);
            writeFunction5(ref this, T5value);
            writeFunction6(ref this, T6value);
        }

        public void WriteValueTuple<T1, T2, T3, T4, T5, T6, T7>(ValueTuple<T1, T2, T3, T4, T5, T6, T7> tuple,
            WriteFunctionDelegate<T1> writeFunction1,
            WriteFunctionDelegate<T2> writeFunction2,
            WriteFunctionDelegate<T3> writeFunction3,
            WriteFunctionDelegate<T4> writeFunction4,
            WriteFunctionDelegate<T5> writeFunction5,
            WriteFunctionDelegate<T6> writeFunction6,
            WriteFunctionDelegate<T7> writeFunction7)
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;
            var T3value = tuple.Item3;
            var T4value = tuple.Item4;
            var T5value = tuple.Item5;
            var T6value = tuple.Item6;
            var T7value = tuple.Item7;

            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
            writeFunction3(ref this, T3value);
            writeFunction4(ref this, T4value);
            writeFunction5(ref this, T5value);
            writeFunction6(ref this, T6value);
            writeFunction7(ref this, T7value);
        }

        public void WriteValueTuple<T1, T2, T3, T4, T5, T6, T7, T8>(ValueTuple<T1, T2, T3, T4, T5, T6, T7, T8> tuple,
            WriteFunctionDelegate<T1> writeFunction1,
            WriteFunctionDelegate<T2> writeFunction2,
            WriteFunctionDelegate<T3> writeFunction3,
            WriteFunctionDelegate<T4> writeFunction4,
            WriteFunctionDelegate<T5> writeFunction5,
            WriteFunctionDelegate<T6> writeFunction6,
            WriteFunctionDelegate<T7> writeFunction7,
            WriteFunctionDelegate<T8> writeFunction8) where T8 : struct
        {
            var T1value = tuple.Item1;
            var T2value = tuple.Item2;
            var T3value = tuple.Item3;
            var T4value = tuple.Item4;
            var T5value = tuple.Item5;
            var T6value = tuple.Item6;
            var T7value = tuple.Item7;
            var T8value = tuple.Rest;

            writeFunction1(ref this, T1value);
            writeFunction2(ref this, T2value);
            writeFunction3(ref this, T3value);
            writeFunction4(ref this, T4value);
            writeFunction5(ref this, T5value);
            writeFunction6(ref this, T6value);
            writeFunction7(ref this, T7value);
            writeFunction8(ref this, T8value);
        }

        #endregion

        #region Static
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBoolean(ref LiteWriter writer, bool obj)
        {
            writer.WriteBoolean(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteSByte(ref LiteWriter writer, sbyte obj)
        {
            writer.WriteSByte(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteByte(ref LiteWriter writer, byte obj)
        {
            writer.WriteByte(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt16(ref LiteWriter writer, short obj)
        {
            writer.WriteInt16(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt16(ref LiteWriter writer, ushort obj)
        {
            writer.WriteUInt16(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt32(ref LiteWriter writer, int obj)
        {
            writer.WriteInt32(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt32(ref LiteWriter writer, uint obj)
        {
            writer.WriteUInt32(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt64(ref LiteWriter writer, long obj)
        {
            writer.WriteInt64(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt64(ref LiteWriter writer, ulong obj)
        {
            writer.WriteUInt64(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteSingle(ref LiteWriter writer, float obj)
        {
            writer.WriteSingle(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDouble(ref LiteWriter writer, double obj)
        {
            writer.WriteDouble(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteString(ref LiteWriter writer, string obj)
        {
            writer.WriteString(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDecimal(ref LiteWriter writer, decimal obj)
        {
            writer.WriteDecimal(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDateTime(ref LiteWriter writer, DateTime obj)
        {
            writer.WriteDate(obj);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteGuid(ref LiteWriter writer, Guid obj)
        {
            writer.WriteGuid(obj);
        }
        #endregion
    }
}
