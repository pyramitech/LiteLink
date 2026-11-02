using System.Collections.Generic;

namespace LiteLink.Generators.Emit
{
    /// <summary>Storage kind of a serialized property. Values match the wire protocol's storage type byte.</summary>
    public enum PropertyKind : ushort
    {
        Normal = 0,
        Array = 1,
        List = 2,
        Dictionary = 3,
        Nullable = 4,
        Tuple = 5
    }

    /// <summary>A single property of a user type, with the encoded type keys needed to emit read/write calls.</summary>
    public class PropertyEmitModel
    {
        public string Name { get; set; }
        public PropertyKind Kind { get; set; }
        /// <summary>Encoded type key of the property (element type for Array/List/Nullable, composite key for Dictionary/Tuple).</summary>
        public string TypeKey { get; set; }
        public string KeyTypeKey { get; set; }
        public string ValueTypeKey { get; set; }
        public string[] TupleTypeKeys { get; set; }
        public bool IsByteArray { get; set; }
    }

    /// <summary>Write/Read function pair for a user-defined object or struct.</summary>
    public class ObjectSerializerModel
    {
        public string TypeDisplay { get; set; }
        public string FuncName { get; set; }
        public bool IsValueType { get; set; }
        public List<PropertyEmitModel> Properties { get; set; } = new List<PropertyEmitModel>();
    }

    /// <summary>Write/Read function pair for a top-level List&lt;T&gt; or T[].</summary>
    public class CollectionSerializerModel
    {
        public string TypeDisplay { get; set; }
        public string FuncName { get; set; }
        public bool IsList { get; set; }
        public string SubTypeKey { get; set; }
        public string SubTypeTrueName { get; set; }
    }

    /// <summary>Write/Read function pair for a top-level Nullable&lt;T&gt;.</summary>
    public class NullableSerializerModel
    {
        public string TypeDisplay { get; set; }
        public string FuncName { get; set; }
        public string SubTypeKey { get; set; }
    }

    /// <summary>Write/Read function pair for a top-level ValueTuple.</summary>
    public class TupleSerializerModel
    {
        public string TypeDisplay { get; set; }
        public string FuncName { get; set; }
        public string[] ElementTypeKeys { get; set; }
    }

    /// <summary>LiteSerializer.AddWriter/AddReader registration for a user type.</summary>
    public class RegistrationModel
    {
        public string TypeDisplay { get; set; }
        public string FuncName { get; set; }
    }

    /// <summary>fingerprint registration emitted into [RpcClientModels] client assemblies so
    /// RpcClient can prove a generated serializer matches the live handshake before using it</summary>
    public class ClientRegistrationModel
    {
        public string TypeDisplay { get; set; }
        /// <summary>fully escaped, quoted C# string literal of the structural fingerprint</summary>
        public string FingerprintLiteral { get; set; }
    }

    public enum ProcedureReturnKind
    {
        Void,
        Task,
        TaskWithValue,
        Sync
    }

    /// <summary>One RPC procedure registered on the server via __addProcedure.</summary>
    public class ProcedureModel
    {
        public int Opcode { get; set; }
        public ulong PermissionMask1 { get; set; }
        public ulong PermissionMask2 { get; set; }
        /// <summary>Fully qualified method to invoke (ContainingType.MethodName).</summary>
        public string MethodName { get; set; }
        /// <summary>The RPC name clients invoke (group.func, e.g. "FooProcedures.ReturnTask"), registered for diagnostics/middleware.</summary>
        public string RpcName { get; set; }
        public bool HasArg { get; set; }
        public string ArgTypeDisplay { get; set; }
        public ProcedureReturnKind ReturnKind { get; set; }
        public string ReturnTypeDisplay { get; set; }
        /// <summary>whether this procedure's responses may be compressed: the server-wide compression
        /// setting adjusted by the [RpcFunc] Compression override. Registered with the procedure and
        /// applied by the server at the exit boundary (not in generated code) so batch envelopes can
        /// be compressed as one block.</summary>
        public bool Compresses { get; set; }
    }

    public class PermissionGroupModel
    {
        public string Name { get; set; }
        public ulong Mask1 { get; set; }
        public ulong Mask2 { get; set; }
    }

    /// <summary>A property row inside a handshake type map entry.</summary>
    public class TypeMapPropertyModel
    {
        public string Name { get; set; }
        public int LocalIndex { get; set; }
        public int PropertyNameIndex { get; set; }
        public int TypeIndex { get; set; }
        /// <summary>Encoded key of the property type, used only for the generated comment.</summary>
        public string TypeIndexKey { get; set; }
        public int StorageType { get; set; }
    }

    public enum TypeEntryKind
    {
        PropertyMap,
        Base,
        Dictionary,
        Collection,
        Nullable,
        Tuple
    }

    /// <summary>One entry of the handshake type table.</summary>
    public class TypeEntryModel
    {
        public int Index { get; set; }
        /// <summary>Effective encoded key (enum_ types substituted by their underlying primitive), used for comments and lookups.</summary>
        public string Key { get; set; }
        /// <summary>C# type name used in the generated typeof().</summary>
        public string DecodedName { get; set; }
        public TypeEntryKind Kind { get; set; }
        public List<TypeMapPropertyModel> Properties { get; set; }
        public int KeyTypeIndex { get; set; }
        public string KeyTypeKey { get; set; }
        public int ValueTypeIndex { get; set; }
        public string ValueTypeKey { get; set; }
        public int SubTypeIndex { get; set; }
        public string SubTypeKey { get; set; }
        public bool IsArray { get; set; }
        public List<TupleElementModel> TupleElements { get; set; }
    }

    public class TupleElementModel
    {
        public int Position { get; set; }
        public string Key { get; set; }
        public int TypeIndex { get; set; }
    }

    /// <summary>One entry of the handshake function table (including the implicit Handshake function).</summary>
    public class FunctionEntryModel
    {
        public int Opcode { get; set; }
        /// <summary>Precomputed CRC of the function name, formatted as a decimal literal.</summary>
        public string CrcValue { get; set; }
        public int? ArgTypeIndex { get; set; }
        public int? RetTypeIndex { get; set; }
        public string Name { get; set; }
        public string ArgName { get; set; }
        public string RetName { get; set; }
    }

    /// <summary>Everything needed to render the server bootstrap + handshake response block.</summary>
    public class ServerInitModel
    {
        public string LinkType { get; set; }
        public bool UseCompression { get; set; }
        public List<PermissionGroupModel> PermissionGroups { get; set; } = new List<PermissionGroupModel>();
        public List<ProcedureModel> Procedures { get; set; } = new List<ProcedureModel>();
        public int PropertyIndexBytes { get; set; }
        /// <summary>LiteWriter method used to write property name indices (WriteByte/WriteUInt16/WriteUInt32).</summary>
        public string PropertyIndexMethod { get; set; }
        public List<string> PropertyNames { get; set; } = new List<string>();
        public int TypeIndexBytes { get; set; }
        public string TypeIndexMethod { get; set; }
        public List<TypeEntryModel> Types { get; set; } = new List<TypeEntryModel>();
        public int FunctionCrcBytes { get; set; }
        /// <summary>LiteWriter method used to write function name hashes (matches FunctionCrcBytes).</summary>
        public string FuncCrcMethod { get; set; }
        public List<FunctionEntryModel> Functions { get; set; } = new List<FunctionEntryModel>();
        public int PermissionCrcBytes { get; set; }
        /// <summary>LiteWriter method used to write permission name hashes (matches PermissionCrcBytes).</summary>
        public string PermCrcMethod { get; set; }
        public List<string> PermissionCrcValues { get; set; } = new List<string>();
        public int PermissionCount { get; set; }

        /// <summary>When true, a pregenerated API description buffer (served on system opcode 0 type 2) is emitted.</summary>
        public bool ShowApi { get; set; }
        /// <summary>The API password as a ready C# string literal (quotes and escaping included), or null when none is set.</summary>
        public string ApiPasswordLiteral { get; set; }
    }

    /// <summary>Root model for the generated source file.</summary>
    public class SerializerFileModel
    {
        public List<string> Typedefs { get; set; } = new List<string>();
        public List<RegistrationModel> Registrations { get; set; } = new List<RegistrationModel>();
        public List<ClientRegistrationModel> ClientRegistrations { get; set; } = new List<ClientRegistrationModel>();
        public List<string> WriterFunctions { get; set; } = new List<string>();
        public List<string> ReaderFunctions { get; set; } = new List<string>();
        public string ServerInitializer { get; set; } = "";
    }
}
