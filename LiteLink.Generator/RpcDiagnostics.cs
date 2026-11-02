using Microsoft.CodeAnalysis;

namespace LiteLink.Generators
{
    /// <summary>
    /// All diagnostics reported by the LiteLink generator.
    /// IDs are stable and part of the public surface (users may suppress them) — never reuse or renumber.
    /// </summary>
    internal static class RpcDiagnostics
    {
        private const string Category = "LiteLink.Generator";

        /// <summary>Info: a type was picked up from an RpcLink.Invoke&lt;T&gt; call and added to the generated serializers.</summary>
        public static readonly DiagnosticDescriptor ExtractedClientParameter = new DiagnosticDescriptor(
            "RPCGEN001",
            "Extracted client RPC parameter",
            "Extracted client RPC parameter: '{0}'",
            Category,
            DiagnosticSeverity.Info,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InvokeTypeExtractionFailed = new DiagnosticDescriptor(
            "RPCGEN002",
            "Failed to extract Invoke type",
            "Failed to extract the pushed type from this RpcLink.Invoke call",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor MultipleRpcServers = new DiagnosticDescriptor(
            "RPCGEN003",
            "Multiple RpcServer declarations",
            "Found multiple [RpcServer] declarations, only a single one is allowed; this one is ignored",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateFunction = new DiagnosticDescriptor(
            "RPCGEN004",
            "Duplicate RPC function",
            "Duplicate RPC function name '{0}' declared by {1}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor FirstParameterMustBeLink = new DiagnosticDescriptor(
            "RPCGEN005",
            "First parameter must be the link type",
            "RPC function {0} must take {1} as its first parameter",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor TooManyParameters = new DiagnosticDescriptor(
            "RPCGEN006",
            "Too many RPC parameters",
            "RPC function {0} has too many parameters, LiteLink currently supports the link plus at most one payload parameter",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PermissionGroupsMustBeEnum = new DiagnosticDescriptor(
            "RPCGEN007",
            "PermissionGroups must be an enum",
            "The PermissionGroups type supplied to [RpcServer] must be an enum",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PermissionIndexOutOfRange = new DiagnosticDescriptor(
            "RPCGEN008",
            "Permission index out of range",
            "PermissionGroups has a hard index limit of 0-127",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PermissionValuesNotSequential = new DiagnosticDescriptor(
            "RPCGEN009",
            "Permission values not sequential",
            "PermissionGroups enum values must start at 0 and proceed sequentially",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PermissionLimitExceeded = new DiagnosticDescriptor(
            "RPCGEN010",
            "Permission limit exceeded",
            "PermissionGroups has a hard limit of 128 groups",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UndeclaredPermission = new DiagnosticDescriptor(
            "RPCGEN011",
            "Undeclared permission",
            "Permission '{0}' used by {1} (or its group) is not declared in the [RpcServer] PermissionGroups",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor FunctionCrcCollision = new DiagnosticDescriptor(
            "RPCGEN012",
            "Function name hash collision",
            "RPC function names collide at every supported hash width, try renaming one of the RPC functions or groups",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PermissionCrcCollision = new DiagnosticDescriptor(
            "RPCGEN013",
            "Permission name hash collision",
            "Permission group names collide at every supported hash width, try renaming one of the permission groups",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor TooManyProperties = new DiagnosticDescriptor(
            "RPCGEN014",
            "Too many serialized properties",
            "The maximum supported number of serialized fields/properties per type is 65534, the type {0} has {1}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnexpectedType = new DiagnosticDescriptor(
            "RPCGEN015",
            "Unexpected type in type table",
            "Type '{0}' is missing from the generated type table; this is a LiteLink generator bug, please file a report",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor InternalError = new DiagnosticDescriptor(
            "RPCGEN016",
            "LiteLink generator internal error",
            "The LiteLink generator failed unexpectedly while {0}: {1}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor IncorrectTypeIndex = new DiagnosticDescriptor(
            "RPCGEN017",
            "Type index mismatch",
            "Internal type index mismatch for '{0}'; this is a LiteLink generator bug, please file a report",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    }
}
