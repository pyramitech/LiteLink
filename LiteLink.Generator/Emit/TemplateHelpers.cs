using System;
using System.Collections.Generic;

namespace LiteLink.Generators.Emit
{
    /// <summary>String helpers used by the Razor templates to emit read/write calls for encoded type keys.</summary>
    public static class TemplateHelpers
    {
        static readonly Dictionary<string, string> WriterSuffixes = new Dictionary<string, string>
        {
            ["bool"] = "Boolean",
            ["sbyte"] = "SByte",
            ["byte"] = "Byte",
            ["short"] = "Int16",
            ["ushort"] = "UInt16",
            ["int"] = "Int32",
            ["uint"] = "UInt32",
            ["long"] = "Int64",
            ["ulong"] = "UInt64",
            ["float"] = "Single",
            ["double"] = "Double",
            ["string"] = "String",
            ["decimal"] = "Decimal",
            ["date"] = "Date",
            ["guid"] = "Guid",
        };

        static readonly Dictionary<string, string> FullNameAliases = new Dictionary<string, string>
        {
            [typeof(bool).FullName] = "bool",
            [typeof(sbyte).FullName] = "sbyte",
            [typeof(byte).FullName] = "byte",
            [typeof(short).FullName] = "short",
            [typeof(ushort).FullName] = "ushort",
            [typeof(int).FullName] = "int",
            [typeof(uint).FullName] = "uint",
            [typeof(long).FullName] = "long",
            [typeof(ulong).FullName] = "ulong",
            [typeof(float).FullName] = "float",
            [typeof(double).FullName] = "double",
            [typeof(string).FullName] = "string",
            [typeof(decimal).FullName] = "decimal",
            [typeof(DateTime).FullName] = "date",
            [typeof(Guid).FullName] = "guid",
        };

        static string Normalize(string typeKey)
            => FullNameAliases.TryGetValue(typeKey, out var alias) ? alias : typeKey;

        /// <summary>Returns the full statement writing <paramref name="target"/> as <paramref name="typeKey"/>, e.g. "writer.WriteInt32(obj.Id);".</summary>
        public static string WriteCall(string typeKey, string target)
        {
            var key = Normalize(typeKey);

            if (WriterSuffixes.TryGetValue(key, out var suffix))
                return $"writer.Write{suffix}({target});";

            if (key.StartsWith("enum_"))
            {
                //the key encodes the underlying primitive, write the enum at its native size
                var underlying = Generators.RpcGenerator.GetEnumUnderlying(key);
                return $"writer.Write{WriterSuffixes[underlying]}(({underlying}){target});";
            }

            return $"writer.Write{Generators.RpcGenerator.GetTrueName(key).Replace(".", "_")}({target});";
        }

        /// <summary>Returns the expression reading a value of <paramref name="typeKey"/>, e.g. "reader.ReadInt32()" (no semicolon).</summary>
        public static string ReadCall(string typeKey)
        {
            var key = Normalize(typeKey);

            if (WriterSuffixes.TryGetValue(key, out var suffix))
                return $"reader.Read{suffix}()";

            if (key.StartsWith("enum_"))
            {
                //the key encodes the underlying primitive, read the enum at its native size
                var underlying = Generators.RpcGenerator.GetEnumUnderlying(key);
                return $"({Generators.RpcGenerator.GetEnumName(key)})reader.Read{WriterSuffixes[underlying]}()";
            }

            return $"reader.Read{Generators.RpcGenerator.GetTrueName(key).Replace(".", "_")}()";
        }

        /// <summary>C# type name for an encoded key (delegates to RpcGenerator.GetTrueName).</summary>
        public static string TrueName(string typeKey)
            => Generators.RpcGenerator.GetTrueName(typeKey);
    }
}
