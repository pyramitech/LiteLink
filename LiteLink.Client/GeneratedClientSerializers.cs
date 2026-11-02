using System;
using System.Collections.Concurrent;

namespace LiteLink.Client
{
    /// <summary>
    /// Registry filled by generated code in client assemblies marked with [assembly: RpcClientModels].
    /// Each entry pairs a model type with the structural fingerprint of the wire layout its generated
    /// serializers implement. At handshake time RpcClient computes the same fingerprint from the
    /// server's declared type table; when they match, the generated serializers are provably
    /// byte-compatible and replace the dynamic path for that type.
    /// </summary>
    public static class GeneratedClientSerializers
    {
        private static readonly ConcurrentDictionary<Type, string> _fingerprints
            = new ConcurrentDictionary<Type, string>();

        /// <summary>called by generated module initializers; the typed reader/writer pair itself is
        /// registered with LiteSerializer under the same type</summary>
        public static void Register(Type modelType, string fingerprint)
        {
            _fingerprints[modelType] = fingerprint;
        }

        public static bool TryGetFingerprint(Type modelType, out string fingerprint)
        {
            return _fingerprints.TryGetValue(modelType, out fingerprint);
        }
    }
}
