using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text;

namespace LiteLink.Shared.Serializer
{
    public static class LiteSerializer
    {
        public delegate void WriteFunctionDelegate<T>(ref LiteWriter writer, T obj);
        public delegate T ReadFunctionDelegate<T>(ref LiteReader reader);
        public static Dictionary<Type, Delegate> Writers = new Dictionary<Type, Delegate>();
        public static Dictionary<Type, Delegate> Readers = new Dictionary<Type, Delegate>();

        public delegate void WriteObjectDelegate(ref LiteWriter writer, object value);
        private static readonly ConcurrentDictionary<Type, WriteObjectDelegate> ObjectWriters
            = new ConcurrentDictionary<Type, WriteObjectDelegate>();

        /// <summary>
        /// Serializes a value by its runtime type (used when the compile-time type is object,
        /// e.g. RpcLink.Invoke's model argument). A ref-struct writer can't flow through
        /// MethodInfo.Invoke, so a typed delegate is created once per type and cached.
        /// </summary>
        public static void SerializeObject(ref LiteWriter writer, object value)
        {
            var del = ObjectWriters.GetOrAdd(value.GetType(), static t =>
                (WriteObjectDelegate)typeof(LiteSerializer)
                    .GetMethod(nameof(WriteBoxed), BindingFlags.NonPublic | BindingFlags.Static)
                    .MakeGenericMethod(t)
                    .CreateDelegate(typeof(WriteObjectDelegate)));
            del(ref writer, value);
        }

        private static void WriteBoxed<T>(ref LiteWriter writer, object value) => Serialize(ref writer, (T)value);

        public static void AddWriter<T>(WriteFunctionDelegate<T> del)
        {
            Writers.Add(typeof(T), del);
        }

        public static void AddReader<T>(ReadFunctionDelegate<T> del)
        {
            Readers.Add(typeof(T), del);
        }

        public static byte[] Serialize<T>(T obj)
        {
            if (Writers.TryGetValue(typeof(T), out var writeFunction))
            {
                var writer = new LiteWriter();
                ((WriteFunctionDelegate<T>)writeFunction)(ref writer, obj);
                return writer.Close();
            }
            else
            {
                throw new Exception($"Type {typeof(T)} not marked for serialization");
            }
        }

        public static byte[] Serialize<T>(List<T> list)
        {
            var writer = new LiteWriter();
            writer.WriteCollection<List<T>, T>(list);
            return writer.Close();
        }

        public static byte[] Serialize<T>(T[] array)
        {
            var writer = new LiteWriter();
            writer.WriteCollection<T[], T>(array);
            return writer.Close();
        }

        public static void Serialize<T>(ref LiteWriter writer, T obj)
        {
            var type = typeof(T);
            if (Writers.TryGetValue(type, out var writeFunction))
            {
                ((WriteFunctionDelegate<T>)writeFunction)(ref writer, obj);
                return;
            }
            else if (type.IsEnum)
            {
                SerializeEnum(ref writer, obj);
                return;
            }
            else if (type.IsGenericType && (type.FullName.StartsWith("System.Collections.Generic.List")) && (type.GenericTypeArguments.Length == 1))
            {
                var genArgs = type.GetGenericArguments()[0];
                var method = typeof(LiteWriter).GetMethods().
                    FirstOrDefault(x =>
                    x.Name == "WriteCollection")
                    .MakeGenericMethod(type, genArgs);

                var newDelegate = method.CreateDelegate(typeof(WriteFunctionDelegate<T>));
                Writers.TryAdd(type, newDelegate);
                ((WriteFunctionDelegate<T>)newDelegate)(ref writer, obj);
                return;

            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                //mirrors the ReadDictionary fallback in Deserialize<T>, pick the parameterless-delegate overload
                var genArgs = type.GetGenericArguments();
                var method = typeof(LiteWriter).GetMethods().
                    FirstOrDefault(x =>
                    x.Name == "WriteDictionary" && x.GetParameters().Length == 1)
                    .MakeGenericMethod(genArgs);
                var newDelegate = method.CreateDelegate(typeof(WriteFunctionDelegate<T>));
                Writers.TryAdd(type, newDelegate);
                ((WriteFunctionDelegate<T>)newDelegate)(ref writer, obj);
                return;
            }
            else if (type.IsGenericType && (type.FullName.StartsWith("System.ValueTuple") || type.GetGenericTypeDefinition() == typeof(ValueTuple<>)))
            {
                var genArgs = type.GetGenericArguments();
                var method = typeof(LiteWriter).GetMethods().
                    FirstOrDefault(x =>
                    x.Name == "WriteValueTuple" && x.GetGenericArguments().Length == genArgs.Length)
                    .MakeGenericMethod(genArgs);
                var newDelegate = method.CreateDelegate(typeof(WriteFunctionDelegate<T>));
                Writers.TryAdd(type, newDelegate);
                ((WriteFunctionDelegate<T>)newDelegate)(ref writer, obj);
                return;
            }
            else
            {
                throw new Exception($"Type {type} not marked for serialization");
            }
        }

        public static void Serialize<T>(ref LiteWriter writer, Nullable<T> obj) where T: struct
        {
            writer.WriteBoolean(obj.HasValue);
            if(obj.HasValue)
            {
                Serialize(ref writer, obj.Value);
            }
        }

        public static void Serialize<T>(ref LiteWriter writer, List<T> list)
        {
            writer.WriteCollection<List<T>, T>(list);
        }

        public static void Serialize<T>(ref LiteWriter writer, T[] array)
        {
            writer.WriteCollection<T[], T>(array);            
        }

        public static byte[] Serialize<TKey, TValue>(Dictionary<TKey, TValue> dict)
        {
            var writer = new LiteWriter();
            writer.WriteDictionary(dict);
            return writer.Close();
        }

        public static T Deserialize<T>(byte[] obj)
        {
            var reader = new LiteReader(obj);
            return Deserialize<T>(ref reader);
        }

        public static T Deserialize<T>(ref LiteReader reader)
        {
            if (Readers.TryGetValue(typeof(T), out var readFunction))
            {
                return ((ReadFunctionDelegate<T>)readFunction)(ref reader);
            }
            else if (typeof(T).IsArray)
            {
                if (typeof(T).GetElementType() == typeof(byte))
                {
                    var method = typeof(LiteReader).GetMethod("ReadByteArray");
                    var newDelegate = method.CreateDelegate(typeof(ReadFunctionDelegate<T>));
                    Readers.TryAdd(typeof(T), newDelegate);
                    return ((ReadFunctionDelegate<T>)newDelegate)(ref reader);
                }
                else
                {
                    var method = typeof(LiteReader).GetMethods().
                        FirstOrDefault(x => x.Name == "ReadFixedCollection" && x.GetParameters().Length == 0)
                        .MakeGenericMethod(typeof(T).GetElementType());
                    var newDelegate = method.CreateDelegate(typeof(ReadFunctionDelegate<T>));
                    Readers.TryAdd(typeof(T), newDelegate);
                    return ((ReadFunctionDelegate<T>)newDelegate)(ref reader);
                }
            }
            else if (typeof(IList).IsAssignableFrom(typeof(T)))
            {
                var method = typeof(LiteReader).GetMethods().
                    FirstOrDefault(x => x.Name == "ReadCollection" && x.GetParameters().Length == 0)
                    .MakeGenericMethod(typeof(T).GetGenericArguments()[0]);
                var newDelegate = method.CreateDelegate(typeof(ReadFunctionDelegate<T>));
                Readers.TryAdd(typeof(T), newDelegate);
                return ((ReadFunctionDelegate<T>)newDelegate)(ref reader);
            }
            else if (typeof(IDictionary).IsAssignableFrom(typeof(T)))
            {
                var genArgs = typeof(T).GetGenericArguments();
                var method = typeof(LiteReader).GetMethods().
                    FirstOrDefault(x => x.Name == "ReadDictionary" && x.GetParameters().Length == 0)
                    .MakeGenericMethod(genArgs[0], genArgs[1]);
                var newDelegate = method.CreateDelegate(typeof(ReadFunctionDelegate<T>));
                Readers.TryAdd(typeof(T), newDelegate);
                return ((ReadFunctionDelegate<T>)newDelegate)(ref reader);
            }
            else if (typeof(T).IsGenericType && (typeof(T).FullName.StartsWith("System.ValueTuple") || typeof(T).GetGenericTypeDefinition() == typeof(ValueTuple<>)))
            {
                var genArgs = typeof(T).GetGenericArguments();
                var method = typeof(LiteReader).GetMethods().
                    FirstOrDefault(x => x.Name == "ReadValueTuple" && x.GetGenericArguments().Length == genArgs.Length)
                    .MakeGenericMethod(genArgs);
                var newDelegate = method.CreateDelegate(typeof(ReadFunctionDelegate<T>));
                Readers.TryAdd(typeof(T), newDelegate);
                return ((ReadFunctionDelegate<T>)newDelegate)(ref reader);
            }
            else if (typeof(T).IsEnum)
            {
                return DeserializeEnum<T>(ref reader);
            }
            else if (Nullable.GetUnderlyingType(typeof(T)) != null)
            {
                var actualType = Nullable.GetUnderlyingType(typeof(T));
                var method = typeof(LiteReader).GetMethods().
                    FirstOrDefault(x => x.Name == "ReadNullable" && x.GetParameters().Length == 0)
                    .MakeGenericMethod(actualType);
                var newDelegate = method.CreateDelegate(typeof(ReadFunctionDelegate<T>));
                Readers.TryAdd(typeof(T), newDelegate);
                return ((ReadFunctionDelegate<T>)newDelegate)(ref reader);
            }
            else
            {
                throw new Exception($"Type {typeof(T)} not marked for serialization");
            }
        }

        public static void Deserialize<T>(byte[] obj, out T res)
        {
            if (Readers.TryGetValue(typeof(T), out var readFunction))
            {
                var reader = new LiteReader(obj);
                res = ((ReadFunctionDelegate<T>)readFunction)(ref reader);                
            }
            else
            {
                throw new Exception($"Type {typeof(T)} not marked for serialization");
            }
        }

        public static void Deserialize<T>(byte[] obj, out T[] res)
        {
            var reader = new LiteReader(obj);
            res = reader.ReadFixedCollection<T>();
        }

        public static void Deserialize<T>(byte[] obj, out List<T> res)
        {
            var reader = new LiteReader(obj);
            res = reader.ReadCollection<T>();           
        }

        public static void Deserialize<TKey, TValue>(byte[] obj, out Dictionary<TKey, TValue> res)
        {
            var reader = new LiteReader(obj);
            res = reader.ReadDictionary<TKey, TValue>();
        }

        public static WriteFunctionDelegate<T> GetWriterDelegate<T>()
        {
            if (Writers.TryGetValue(typeof(T), out var writeFunction))
            {
                return writeFunction as WriteFunctionDelegate<T>;
            }
            else if (typeof(T).IsEnum)
            {
                //enums have no generated writer, synthesize the size-aware fallback so they
                //work as dictionary/tuple/collection elements
                WriteFunctionDelegate<T> enumWriter = SerializeEnum<T>;
                Writers.TryAdd(typeof(T), enumWriter);
                return enumWriter;
            }
            else if (Nullable.GetUnderlyingType(typeof(T)) is Type underlyingWrite)
            {
                //nullables have no generated writer either (models inline them), bind
                //LiteWriter.WriteNullable<underlying> so they work as container elements
                var method = typeof(LiteWriter).GetMethods()
                    .First(x => x.Name == "WriteNullable" && x.GetGenericArguments().Length == 1)
                    .MakeGenericMethod(underlyingWrite);
                var newDelegate = method.CreateDelegate(typeof(WriteFunctionDelegate<T>));
                Writers.TryAdd(typeof(T), newDelegate);
                return (WriteFunctionDelegate<T>)newDelegate;
            }
            else
            {
                throw new Exception($"Type {typeof(T)} not marked for serialization");
            }
        }

        public static ReadFunctionDelegate<T> GetReaderDelegate<T>()
        {
            if (Readers.TryGetValue(typeof(T), out var readFunction))
            {
                return readFunction as ReadFunctionDelegate<T>;
            }
            else if (typeof(T).IsEnum)
            {
                ReadFunctionDelegate<T> enumReader = DeserializeEnum<T>;
                Readers.TryAdd(typeof(T), enumReader);
                return enumReader;
            }
            else if (Nullable.GetUnderlyingType(typeof(T)) is Type underlyingRead)
            {
                var method = typeof(LiteReader).GetMethods()
                    .First(x => x.Name == "ReadNullable" && x.GetParameters().Length == 0)
                    .MakeGenericMethod(underlyingRead);
                var newDelegate = method.CreateDelegate(typeof(ReadFunctionDelegate<T>));
                Readers.TryAdd(typeof(T), newDelegate);
                return (ReadFunctionDelegate<T>)newDelegate;
            }
            else
            {
                throw new Exception($"Type {typeof(T)} not marked for serialization");
            }
        }

        /// <summary>
        /// Writes an enum at the size of its underlying type (Type.GetTypeCode of an enum type
        /// returns the underlying primitive's code). Matches what the generated serializers and
        /// the handshake type map declare.
        /// </summary>
        public static void SerializeEnum<T>(ref LiteWriter writer, T obj)
        {
            switch (Type.GetTypeCode(typeof(T)))
            {
                case TypeCode.SByte: writer.WriteSByte(Convert.ToSByte(obj)); break;
                case TypeCode.Byte: writer.WriteByte(Convert.ToByte(obj)); break;
                case TypeCode.Int16: writer.WriteInt16(Convert.ToInt16(obj)); break;
                case TypeCode.UInt16: writer.WriteUInt16(Convert.ToUInt16(obj)); break;
                case TypeCode.UInt32: writer.WriteUInt32(Convert.ToUInt32(obj)); break;
                case TypeCode.Int64: writer.WriteInt64(Convert.ToInt64(obj)); break;
                case TypeCode.UInt64: writer.WriteUInt64(Convert.ToUInt64(obj)); break;
                default: writer.WriteInt32(Convert.ToInt32(obj)); break;
            }
        }

        /// <summary>reads an enum at the size of its underlying type, see <see cref="SerializeEnum{T}"/></summary>
        public static T DeserializeEnum<T>(ref LiteReader reader)
        {
            object value;
            switch (Type.GetTypeCode(typeof(T)))
            {
                case TypeCode.SByte: value = reader.ReadSByte(); break;
                case TypeCode.Byte: value = reader.ReadByte(); break;
                case TypeCode.Int16: value = reader.ReadInt16(); break;
                case TypeCode.UInt16: value = reader.ReadUInt16(); break;
                case TypeCode.UInt32: value = reader.ReadUInt32(); break;
                case TypeCode.Int64: value = reader.ReadInt64(); break;
                case TypeCode.UInt64: value = reader.ReadUInt64(); break;
                default: value = reader.ReadInt32(); break;
            }
            return (T)Enum.ToObject(typeof(T), value);
        }

        //public static void Serialize<T>(ref LiteWriter writer, T obj)
        //{
        //    if (Writers.TryGetValue(typeof(T), out var writeFunction))
        //    {
        //        ((WriteFunctionDelegate<T>)writeFunction)(ref writer, obj);
        //    }
        //    else
        //    {
        //        throw new Exception($"Type {typeof(T)} not marked for serialization");
        //    }
        //}

        //public static T Deserialize<T>(ref LiteReader reader)
        //{
        //    if (Readers.TryGetValue(typeof(T), out var readFunction))
        //    {
        //        return ((ReadFunctionDelegate<T>)readFunction)(ref reader);
        //    }
        //    else
        //    {
        //        throw new Exception($"Type {typeof(T)} not marked for serialization");
        //    }
        //}
    }
}
