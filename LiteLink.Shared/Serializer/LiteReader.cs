using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using System.Collections;
using LiteLink.Shared.Serializer;
using System.Reflection.PortableExecutable;
using K4os.Compression.LZ4;
using static LiteLink.Shared.Serializer.LiteSerializer;

namespace LiteLink.Shared.Serializer
{
    public ref partial struct LiteReader
    {
        private ReadOnlySpan<byte> _array;
        private int index;

        public LiteReader(ReadOnlySpan<byte> array)
        {
            _array = array;
            index = 0;
        }

        public LiteReader(ReadOnlySpan<byte> array, int index)
        {
            _array = array;
            this.index = index;
        }

        public ReadOnlySpan<byte> Data => _array;

        public void Decompress()
        {
            var count = this.ReadInt32();
            var target = new byte[count];
            var x = LZ4Codec.Decode(
                _array.ToArray(), index, _array.Length - index,
                target, 0, target.Length);

            index = 0;
            _array = target;
        }

        public bool ReadBoolean()
        {
            return _array[index++] != 0;
        }

        public sbyte ReadSByte()
        {
            return ReadRaw<sbyte>();
        }

        public byte ReadByte()
        {
            return _array[index++];
        }

        public unsafe T ReadRaw<T>()
        {
            T res;
            fixed (byte* arr = &_array[index])
            {
                res = Unsafe.ReadUnaligned<T>(arr);
            }
            index += sizeof(T);

            return res;
        }

        public short ReadInt16() { return ReadRaw<short>(); }
        public ushort ReadUInt16() {  return ReadRaw<ushort>(); }
        public int ReadInt32() {  return ReadRaw<int>(); }
        public uint ReadUInt32() { return ReadRaw<uint>(); }
        public long ReadInt64() {  return ReadRaw<long>(); }
        public ulong ReadUInt64() { return ReadRaw<ulong>(); }
        public float ReadSingle() {  return ReadRaw<float>(); }
        public double ReadDouble() {  return ReadRaw<double>(); }

        public ulong ReadLength()
        {
            var len = _array[index];

            if (len == 0) //0 means null
            {
                index++;
                return ulong.MaxValue;
            }
            else if ((len & 0x80) != 0)
            {
                index++;
                return len ^ 0x80u;
            }
            else if ((len & 0x40) != 0)
            {
                return (ushort)(_array[index++] << 8 | _array[index++]) ^ 0x4000u;
                //return ReadUInt16() ^ 0x4000u;
            }
            else if ((len & 0x20) != 0)
            {
                return (uint)(_array[index++] << 24 | _array[index++] << 16 | _array[index++] << 8 | _array[index++]) ^ 0x20000000;
                //return ReadUInt32() ^ 0x20000000;
            }
            else // if((len & 0x10) != 0), Max value, do we even bother setting anything here?
            {
                return (ulong)(_array[index++] << 56 | _array[index++] << 48 | _array[index++] << 40 | _array[index++] << 32 | _array[index++] << 24 | _array[index++] << 16 | _array[index++] << 8 | _array[index++]) ^ 0x1000000000000000;
                //return ReadUInt64() ^ 0x1000000000000000;
            }
        }

        public decimal ReadDecimal()
        {
            var integerPart = (decimal)ReadInt64();
            var decimalPart = (decimal)ReadInt32() / 1_000_000_000m;

            return integerPart + decimalPart;
        }

        public DateTime ReadDate()
        {
            //set first 2 bits to 01 for UTC
            //see: https://github.com/microsoft/referencesource/blob/master/mscorlib/system/datetime.cs#L124
            return DateTime.FromBinary((ReadInt64() & 0x7fffffffffffffff) | 0x4000000000000000);
        }        

        public Guid ReadGuid()
        {
            return ReadRaw<Guid>();
        }

        //public T ReadObject<T>() where T : class, new()
        //{
        //    if (ReadByte() == 0)
        //        return null;

        //    var obj = new T();
        //    //obj.Read(this);
        //    return obj;
        //}

        /// <summary>
        /// returns everything from the current position to the end of the buffer
        /// </summary>
        public byte[] ReadRemaining()
        {
            var res = _array.Slice(index).ToArray();
            index = _array.Length;
            return res;
        }

        public byte[] ReadByteArray()
        {
            var len = ReadLength();
            if (len == ulong.MaxValue)
                return null;

            var res = _array.Slice(index, (int)len);
            index += (int)len;

            return res.ToArray();
        }

        public string ReadString()
        {
            var len = ReadLength();
            if (len == ulong.MaxValue)
                return null;

            var res = Encoding.UTF8.GetString(_array.Slice(index, unchecked((int)len)));
            index += (int)len;
            return res;
        }


        public List<T> ReadCollection<T>()
        {
            var len = ReadLength();
            if (len == ulong.MaxValue)
                return default;

            var list = new List<T>(unchecked((int)len));
            
            var readFunction = LiteSerializer.GetReaderDelegate<T>();
            
            for (uint i = 0; i < len; i++)
            {
                list.Add(readFunction(ref this));
            }

            return list;
        }

        public List<T> ReadCollection<T>(ReadFunctionDelegate<T> readFunction)
        {
            var len = ReadLength();
            if (len == ulong.MaxValue)
                return default;

            var list = new List<T>(unchecked((int)len));

            for (uint i = 0; i < len; i++)
            {
                list.Add(readFunction(ref this));
            }

            return list;
        }

        public T[] ReadFixedCollection<T>()
        {
            var len = ReadLength();
            if (len == ulong.MaxValue)
                return default;

            var array = new T[len];

            var readFunction = LiteSerializer.GetReaderDelegate<T>();

            for (uint i = 0; i < len; i++)
            {
                array[i] = readFunction(ref this);
            }

            return array;
        }

        public T[] ReadFixedCollection<T>(ReadFunctionDelegate<T> readFunction)
        {
            var len = ReadLength();
            if (len == ulong.MaxValue)
                return default;

            var array = new T[len];

            for (uint i = 0; i < len; i++)
            {
                array[i] = readFunction(ref this);
            }

            return array;
        }

        public Dictionary<TKey, TValue> ReadDictionary<TKey, TValue>()
        {
            var len = ReadLength();
            if (len == ulong.MaxValue)
                return default;

            var list = new Dictionary<TKey, TValue>(unchecked((int)len));

            var keyFunction = LiteSerializer.GetReaderDelegate<TKey>();
            var valueFunction = LiteSerializer.GetReaderDelegate<TValue>();

            for (uint i = 0; i < len; i++)
            {
                var key = keyFunction(ref this);
                list[key] = valueFunction(ref this);
            }

            return list;
        }

        public Dictionary<TKey, TValue> ReadDictionary<TKey, TValue>(ReadFunctionDelegate<TKey> keyFunction, ReadFunctionDelegate<TValue> valueFunction)
        {
            var len = ReadLength();
            if (len == ulong.MaxValue)
                return default;

            var list = new Dictionary<TKey, TValue>(unchecked((int)len));

            for (uint i = 0; i < len; i++)
            {
                var key = keyFunction(ref this);
                list[key] = valueFunction(ref this);
            }

            return list;
        }

        public Nullable<T> ReadNullable<T>() where T: struct
        {
            if (ReadBoolean())
            {
                var readFunction = LiteSerializer.GetReaderDelegate<T>();
                return new Nullable<T>(readFunction(ref this));
            }
            else return null;
        }

        public Nullable<T> ReadNullable<T>(ReadFunctionDelegate<T> readFunction) where T : struct
        {
            if (ReadBoolean())
            {
                return new Nullable<T>(readFunction(ref this));
            }
            else return null;
        }

        #region Tuples
        public Tuple<T> ReadTuple<T>()
        {
            var readFunction = LiteSerializer.GetReaderDelegate<T>();
            return new Tuple<T>(readFunction(ref this));
        }
        public Tuple<T1, T2> ReadTuple<T1, T2>()
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            return new Tuple<T1, T2>(
                readFunction1(ref this),
                readFunction2(ref this));
        }

        public Tuple<T1, T2, T3> ReadTuple<T1, T2, T3>()
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            var readFunction3 = LiteSerializer.GetReaderDelegate<T3>();
            return new Tuple<T1, T2, T3>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this));
        }

        public Tuple<T1, T2, T3, T4> ReadTuple<T1, T2, T3, T4>()
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            var readFunction3 = LiteSerializer.GetReaderDelegate<T3>();
            var readFunction4 = LiteSerializer.GetReaderDelegate<T4>();
            return new Tuple<T1, T2, T3, T4>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this));
        }

        public Tuple<T1, T2, T3, T4, T5> ReadTuple<T1, T2, T3, T4, T5>()
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            var readFunction3 = LiteSerializer.GetReaderDelegate<T3>();
            var readFunction4 = LiteSerializer.GetReaderDelegate<T4>();
            var readFunction5 = LiteSerializer.GetReaderDelegate<T5>();
            return new Tuple<T1, T2, T3, T4, T5>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this));
        }

        public Tuple<T1, T2, T3, T4, T5, T6> ReadTuple<T1, T2, T3, T4, T5, T6>()
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            var readFunction3 = LiteSerializer.GetReaderDelegate<T3>();
            var readFunction4 = LiteSerializer.GetReaderDelegate<T4>();
            var readFunction5 = LiteSerializer.GetReaderDelegate<T5>();
            var readFunction6 = LiteSerializer.GetReaderDelegate<T6>();
            return new Tuple<T1, T2, T3, T4, T5, T6>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this),
                readFunction6(ref this));
        }

        public Tuple<T1, T2, T3, T4, T5, T6, T7> ReadTuple<T1, T2, T3, T4, T5, T6, T7>()
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            var readFunction3 = LiteSerializer.GetReaderDelegate<T3>();
            var readFunction4 = LiteSerializer.GetReaderDelegate<T4>();
            var readFunction5 = LiteSerializer.GetReaderDelegate<T5>();
            var readFunction6 = LiteSerializer.GetReaderDelegate<T6>();
            var readFunction7 = LiteSerializer.GetReaderDelegate<T7>();
            return new Tuple<T1, T2, T3, T4, T5, T6, T7>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this),
                readFunction6(ref this),
                readFunction7(ref this));
        }

        public Tuple<T1, T2, T3, T4, T5, T6, T7, TRest> ReadTuple<T1, T2, T3, T4, T5, T6, T7, TRest>()
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            var readFunction3 = LiteSerializer.GetReaderDelegate<T3>();
            var readFunction4 = LiteSerializer.GetReaderDelegate<T4>();
            var readFunction5 = LiteSerializer.GetReaderDelegate<T5>();
            var readFunction6 = LiteSerializer.GetReaderDelegate<T6>();
            var readFunction7 = LiteSerializer.GetReaderDelegate<T7>();
            var readFunction8 = LiteSerializer.GetReaderDelegate<TRest>();
            return new Tuple<T1, T2, T3, T4, T5, T6, T7, TRest>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this),
                readFunction6(ref this),
                readFunction7(ref this),
                readFunction8(ref this));
        }

        public ValueTuple<T> ReadValueTuple<T>()
        {
            var readFunction = LiteSerializer.GetReaderDelegate<T>();
            return new ValueTuple<T>(readFunction(ref this));
        }
        public ValueTuple<T1, T2> ReadValueTuple<T1, T2>()
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            return new ValueTuple<T1, T2>(
                readFunction1(ref this),
                readFunction2(ref this));
        }

        public ValueTuple<T1, T2, T3> ReadValueTuple<T1, T2, T3>()
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            var readFunction3 = LiteSerializer.GetReaderDelegate<T3>();
            return new ValueTuple<T1, T2, T3>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this));
        }

        public ValueTuple<T1, T2, T3, T4> ReadValueTuple<T1, T2, T3, T4>()
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            var readFunction3 = LiteSerializer.GetReaderDelegate<T3>();
            var readFunction4 = LiteSerializer.GetReaderDelegate<T4>();
            return new ValueTuple<T1, T2, T3, T4>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this));
        }

        public ValueTuple<T1, T2, T3, T4, T5> ReadValueTuple<T1, T2, T3, T4, T5>()
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            var readFunction3 = LiteSerializer.GetReaderDelegate<T3>();
            var readFunction4 = LiteSerializer.GetReaderDelegate<T4>();
            var readFunction5 = LiteSerializer.GetReaderDelegate<T5>();
            return new ValueTuple<T1, T2, T3, T4, T5>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this));
        }

        public ValueTuple<T1, T2, T3, T4, T5, T6> ReadValueTuple<T1, T2, T3, T4, T5, T6>()
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            var readFunction3 = LiteSerializer.GetReaderDelegate<T3>();
            var readFunction4 = LiteSerializer.GetReaderDelegate<T4>();
            var readFunction5 = LiteSerializer.GetReaderDelegate<T5>();
            var readFunction6 = LiteSerializer.GetReaderDelegate<T6>();
            return new ValueTuple<T1, T2, T3, T4, T5, T6>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this),
                readFunction6(ref this));
        }

        public ValueTuple<T1, T2, T3, T4, T5, T6, T7> ReadValueTuple<T1, T2, T3, T4, T5, T6, T7>()
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            var readFunction3 = LiteSerializer.GetReaderDelegate<T3>();
            var readFunction4 = LiteSerializer.GetReaderDelegate<T4>();
            var readFunction5 = LiteSerializer.GetReaderDelegate<T5>();
            var readFunction6 = LiteSerializer.GetReaderDelegate<T6>();
            var readFunction7 = LiteSerializer.GetReaderDelegate<T7>();
            return new ValueTuple<T1, T2, T3, T4, T5, T6, T7>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this),
                readFunction6(ref this),
                readFunction7(ref this));
        }

        public ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> ReadValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>() where TRest : struct
        {
            var readFunction1 = LiteSerializer.GetReaderDelegate<T1>();
            var readFunction2 = LiteSerializer.GetReaderDelegate<T2>();
            var readFunction3 = LiteSerializer.GetReaderDelegate<T3>();
            var readFunction4 = LiteSerializer.GetReaderDelegate<T4>();
            var readFunction5 = LiteSerializer.GetReaderDelegate<T5>();
            var readFunction6 = LiteSerializer.GetReaderDelegate<T6>();
            var readFunction7 = LiteSerializer.GetReaderDelegate<T7>();
            var readFunction8 = LiteSerializer.GetReaderDelegate<TRest>();
            return new ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this),
                readFunction6(ref this),
                readFunction7(ref this),
                readFunction8(ref this));
        }

        public Tuple<T> ReadTuple<T>(ReadFunctionDelegate<T> readFunction)
        {
            return new Tuple<T>(readFunction(ref this));
        }

        public Tuple<T1, T2> ReadTuple<T1, T2>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2)
        {
            return new Tuple<T1, T2>(
                readFunction1(ref this),
                readFunction2(ref this));
        }

        public Tuple<T1, T2, T3> ReadTuple<T1, T2, T3>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2,
            ReadFunctionDelegate<T3> readFunction3)
        {
            return new Tuple<T1, T2, T3>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this));
        }

        public Tuple<T1, T2, T3, T4> ReadTuple<T1, T2, T3, T4>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2,
            ReadFunctionDelegate<T3> readFunction3,
            ReadFunctionDelegate<T4> readFunction4)
        {
            return new Tuple<T1, T2, T3, T4>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this));
        }

        public Tuple<T1, T2, T3, T4, T5> ReadTuple<T1, T2, T3, T4, T5>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2,
            ReadFunctionDelegate<T3> readFunction3,
            ReadFunctionDelegate<T4> readFunction4,
            ReadFunctionDelegate<T5> readFunction5)
        {
            return new Tuple<T1, T2, T3, T4, T5>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this));
        }

        public Tuple<T1, T2, T3, T4, T5, T6> ReadTuple<T1, T2, T3, T4, T5, T6>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2,
            ReadFunctionDelegate<T3> readFunction3,
            ReadFunctionDelegate<T4> readFunction4,
            ReadFunctionDelegate<T5> readFunction5,
            ReadFunctionDelegate<T6> readFunction6)
        {
            return new Tuple<T1, T2, T3, T4, T5, T6>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this),
                readFunction6(ref this));
        }

        public Tuple<T1, T2, T3, T4, T5, T6, T7> ReadTuple<T1, T2, T3, T4, T5, T6, T7>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2,
            ReadFunctionDelegate<T3> readFunction3,
            ReadFunctionDelegate<T4> readFunction4,
            ReadFunctionDelegate<T5> readFunction5,
            ReadFunctionDelegate<T6> readFunction6,
            ReadFunctionDelegate<T7> readFunction7)
        {
            return new Tuple<T1, T2, T3, T4, T5, T6, T7>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this),
                readFunction6(ref this),
                readFunction7(ref this));
        }

        public Tuple<T1, T2, T3, T4, T5, T6, T7, TRest> ReadTuple<T1, T2, T3, T4, T5, T6, T7, TRest>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2,
            ReadFunctionDelegate<T3> readFunction3,
            ReadFunctionDelegate<T4> readFunction4,
            ReadFunctionDelegate<T5> readFunction5,
            ReadFunctionDelegate<T6> readFunction6,
            ReadFunctionDelegate<T7> readFunction7,
            ReadFunctionDelegate<TRest> readFunction8)
        {
            return new Tuple<T1, T2, T3, T4, T5, T6, T7, TRest>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this),
                readFunction6(ref this),
                readFunction7(ref this),
                readFunction8(ref this));
        }

        public ValueTuple<T> ReadValueTuple<T>(ReadFunctionDelegate<T> readFunction)
        {
            return new ValueTuple<T>(readFunction(ref this));
        }

        public ValueTuple<T1, T2> ReadValueTuple<T1, T2>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2)
        {
            return new ValueTuple<T1, T2>(
                readFunction1(ref this),
                readFunction2(ref this));
        }

        public ValueTuple<T1, T2, T3> ReadValueTuple<T1, T2, T3>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2,
            ReadFunctionDelegate<T3> readFunction3)
        {
            return new ValueTuple<T1, T2, T3>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this));
        }

        public ValueTuple<T1, T2, T3, T4> ReadValueTuple<T1, T2, T3, T4>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2,
            ReadFunctionDelegate<T3> readFunction3,
            ReadFunctionDelegate<T4> readFunction4)
        {
            return new ValueTuple<T1, T2, T3, T4>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this));
        }

        public ValueTuple<T1, T2, T3, T4, T5> ReadValueTuple<T1, T2, T3, T4, T5>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2,
            ReadFunctionDelegate<T3> readFunction3,
            ReadFunctionDelegate<T4> readFunction4,
            ReadFunctionDelegate<T5> readFunction5)
        {
            return new ValueTuple<T1, T2, T3, T4, T5>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this));
        }

        public ValueTuple<T1, T2, T3, T4, T5, T6> ReadValueTuple<T1, T2, T3, T4, T5, T6>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2,
            ReadFunctionDelegate<T3> readFunction3,
            ReadFunctionDelegate<T4> readFunction4,
            ReadFunctionDelegate<T5> readFunction5,
            ReadFunctionDelegate<T6> readFunction6)
        {
            return new ValueTuple<T1, T2, T3, T4, T5, T6>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this),
                readFunction6(ref this));
        }

        public ValueTuple<T1, T2, T3, T4, T5, T6, T7> ReadValueTuple<T1, T2, T3, T4, T5, T6, T7>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2,
            ReadFunctionDelegate<T3> readFunction3,
            ReadFunctionDelegate<T4> readFunction4,
            ReadFunctionDelegate<T5> readFunction5,
            ReadFunctionDelegate<T6> readFunction6,
            ReadFunctionDelegate<T7> readFunction7)
        {
            return new ValueTuple<T1, T2, T3, T4, T5, T6, T7>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this),
                readFunction6(ref this),
                readFunction7(ref this));
        }

        public ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> ReadValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>(
            ReadFunctionDelegate<T1> readFunction1,
            ReadFunctionDelegate<T2> readFunction2,
            ReadFunctionDelegate<T3> readFunction3,
            ReadFunctionDelegate<T4> readFunction4,
            ReadFunctionDelegate<T5> readFunction5,
            ReadFunctionDelegate<T6> readFunction6,
            ReadFunctionDelegate<T7> readFunction7,
            ReadFunctionDelegate<TRest> readFunction8) where TRest : struct
        {
            return new ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>(
                readFunction1(ref this),
                readFunction2(ref this),
                readFunction3(ref this),
                readFunction4(ref this),
                readFunction5(ref this),
                readFunction6(ref this),
                readFunction7(ref this),
                readFunction8(ref this));
        }

        #endregion

        #region Static
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ReadBoolean(ref LiteReader reader)
        {
            return reader.ReadBoolean();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sbyte ReadSByte(ref LiteReader reader)
        {
            return reader.ReadSByte();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ReadByte(ref LiteReader reader)
        {
            return reader.ReadByte();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short ReadInt16(ref LiteReader reader)
        {
            return reader.ReadInt16();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ReadUInt16(ref LiteReader reader)
        {
            return reader.ReadUInt16();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadInt32(ref LiteReader reader)
        {
            return reader.ReadInt32();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadUInt32(ref LiteReader reader)
        {
            return reader.ReadUInt32();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReadInt64(ref LiteReader reader)
        {
            return reader.ReadInt64();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadUInt64(ref LiteReader reader)
        {
            return reader.ReadUInt64();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ReadSingle(ref LiteReader reader)
        {
            return reader.ReadSingle();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ReadDouble(ref LiteReader reader)
        {
            return reader.ReadDouble();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string ReadString(ref LiteReader reader)
        {
            return reader.ReadString();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static decimal ReadDecimal(ref LiteReader reader)
        {
            return reader.ReadDecimal();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DateTime ReadDateTime(ref LiteReader reader)
        {
            return reader.ReadDate();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Guid ReadGuid(ref LiteReader reader)
        {
            return reader.ReadGuid();
        }
        #endregion

    }
}