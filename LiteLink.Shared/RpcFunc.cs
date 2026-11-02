namespace LiteLink
{
    /// <summary>per-function response compression, overriding the server-wide UseCompression setting</summary>
    public enum RpcCompression
    {
        /// <summary>use the server-wide UseCompression setting (the default)</summary>
        Inherit = 0,
        /// <summary>compress this function's responses even when the server-wide setting is off</summary>
        Enabled = 1,
        /// <summary>never compress this function's responses, e.g. when they carry already-compressed data</summary>
        Disabled = 2
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class RpcFuncAttribute : Attribute
    {
        public string Name { get; set; }
        public string[] PermissionGroups { get; set; }

        /// <summary>overrides the server-wide compression setting for this function's responses</summary>
        public RpcCompression Compression { get; set; }

        public RpcFuncAttribute() { }
        public RpcFuncAttribute(string name) => Name = name;
    }

    /// <summary>
    /// Marks a client assembly for generation of typed model serializers. The generator collects
    /// models from typed RpcClient calls (Send&lt;T&gt;, SendMultiChannelMessage&lt;T&gt;,
    /// RegisterFunction&lt;T&gt;) plus any types listed here (needed for dynamic-proxy calls the
    /// generator can't see), and emits serializers with structural fingerprints. At handshake time
    /// the client uses the generated code for every type whose fingerprint matches the server's
    /// declared structure, and falls back to the dynamic path for the rest.
    /// Ignored in assemblies that declare an [RpcServer] (the server generates its own serializers).
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public class RpcClientModelsAttribute : Attribute
    {
        public Type[] Types { get; }

        public RpcClientModelsAttribute(params Type[] types) => Types = types ?? Type.EmptyTypes;
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class RpcGroupAttribute : Attribute
    {
        public string Name { get; set; }
        public string[] PermissionGroups { get; set; }

        public RpcGroupAttribute() { }
        public RpcGroupAttribute(string name) => Name = name;
    }
}
