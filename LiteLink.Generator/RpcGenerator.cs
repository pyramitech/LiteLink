using LiteLink.Generators.Emit;
using LiteLink.Generators.Templates;
using LiteLink.Shared.Func;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace LiteLink.Generators
{
    [Generator]
    public class RpcGenerator : IIncrementalGenerator
    {
        //per-run symbol caches, cleared at the start of every Execute so symbols from a previous compilation aren't kept alive
        private readonly Dictionary<ISymbol, StorageType> _storageTypeCache = new Dictionary<ISymbol, StorageType>(SymbolEqualityComparer.Default);
        private readonly Dictionary<ISymbol, string> _typeNameCache = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        private readonly Dictionary<ISymbol, List<IPropertySymbol>> _propertiesCache = new Dictionary<ISymbol, List<IPropertySymbol>>(SymbolEqualityComparer.Default);
        //mirrors the filteredSymbols list during type discovery so membership tests are O(1)
        private HashSet<ISymbol> _foundTypes;

        private void Execute(SourceProductionContext context, Compilation compilation,
            ImmutableArray<(string FilePath, int Start, int Length)> funcSeeds,
            ImmutableArray<(string FilePath, int Start, int Length)> serverSeeds,
            ImmutableArray<(string FilePath, int Start, int Length)> invokeSeeds)
        {
            var timer = new Stopwatch();
            timer.Start();

            _storageTypeCache.Clear();
            _typeNameCache.Clear();
            _propertiesCache.Clear();

            var allMethods = new List<(SemanticModel, List<MethodDeclarationSyntax>)>();
            var clientSymbols = new List<ITypeSymbol>();

            //models used by typed RpcClient calls, roots for client-assembly serializer generation
            var clientModelSymbols = new List<ITypeSymbol>();

            AttributeData serverDeclaration = null;

            var funcSeedsByPath = GroupSeeds(funcSeeds);
            var serverSeedsByPath = GroupSeeds(serverSeeds);
            var invokeSeedsByPath = GroupSeeds(invokeSeeds);

            try
            {
                //only trees the seed providers flagged are touched; file path order keeps opcodes deterministic
                foreach (var syntaxTree in compilation.SyntaxTrees.OrderBy(x => x.FilePath))
                {
                    var hasFuncs = funcSeedsByPath.TryGetValue(syntaxTree.FilePath, out var funcSpans);
                    var hasServer = serverSeedsByPath.TryGetValue(syntaxTree.FilePath, out var serverSpans);
                    var hasInvokes = invokeSeedsByPath.TryGetValue(syntaxTree.FilePath, out var invokeSpans);

                    if (!hasFuncs && !hasServer && !hasInvokes)
                        continue;

                    var root = syntaxTree.GetRoot();
                    var semanticModel = compilation.GetSemanticModel(syntaxTree);

                    //client invocations: resolve each seeded call semantically and match RpcLink.Invoke<T>,
                    //this catches any receiver shape (derived link types, implicit `this` inside a derived class,
                    //properties, method results) and takes the pushed type from the generic argument, so it also
                    //works when the model argument is null or a derived instance
                    if (hasInvokes)
                    {
                        var rpcLinkSymbol = compilation.GetTypeByMetadataName("LiteLink.RpcLink");
                        var rpcClientSymbol = compilation.GetTypeByMetadataName("LiteLink.Client.RpcClient");

                        foreach (var span in invokeSpans)
                        {
                            var invocation = FindSeedNode<InvocationExpressionSyntax>(root, span);
                            if (invocation == null)
                                continue;

                            var methodSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                            if (methodSymbol == null)
                                continue;

                            //typed calls on the C# client (Send<T>, SendMultiChannelMessage<T>,
                            //RegisterFunction<T>/<TArg, TResult>): their type arguments and the
                            //compile-time types of object-typed model arguments are the roots for
                            //client-assembly serializer generation
                            if (rpcClientSymbol != null
                                && SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingType, rpcClientSymbol))
                            {
                                try
                                {
                                    foreach (var typeArgument in methodSymbol.TypeArguments)
                                    {
                                        if (typeArgument is IErrorTypeSymbol || ContainsTypeParameter(typeArgument))
                                            continue;
                                        clientModelSymbols.Add(typeArgument);
                                    }

                                    for (int argIndex = 0; argIndex < methodSymbol.Parameters.Length
                                        && argIndex < invocation.ArgumentList.Arguments.Count; argIndex++)
                                    {
                                        if (methodSymbol.Parameters[argIndex].Type.SpecialType != SpecialType.System_Object)
                                            continue;

                                        var argType = semanticModel.GetTypeInfo(invocation.ArgumentList.Arguments[argIndex].Expression).Type;
                                        if (argType != null
                                            && argType.TypeKind != TypeKind.Error
                                            && argType.SpecialType != SpecialType.System_Object
                                            && !ContainsTypeParameter(argType))
                                        {
                                            clientModelSymbols.Add(argType);
                                        }
                                    }
                                }
                                catch
                                {
                                    Report(context, RpcDiagnostics.InvokeTypeExtractionFailed, invocation.GetLocation());
                                }
                                continue;
                            }

                            if ((methodSymbol.Name != "Invoke" && methodSymbol.Name != "Send")
                                || !InheritsFrom(methodSymbol.ContainingType, rpcLinkSymbol))
                                continue;

                            try
                            {
                                //the pushed model type for Send<T>, the response type for Invoke<TResult>;
                                //the deprecated Invoke(string, object) shim is non-generic and has none
                                if (methodSymbol.IsGenericMethod && methodSymbol.TypeArguments.Length == 1)
                                {
                                    var typeArgument = methodSymbol.TypeArguments[0];

                                    if (typeArgument is IErrorTypeSymbol || ContainsTypeParameter(typeArgument))
                                    {
                                        continue;
                                        //TODO: for open generics, follow through the top function until the argument is no longer generic.
                                    }

                                    clientSymbols.Add(typeArgument);

                                    Report(context, RpcDiagnostics.ExtractedClientParameter, invocation.GetLocation(), typeArgument.ToDisplayString());
                                }

                                //Invoke<TResult> and the deprecated Invoke both take their model as object
                                //and serialize it by runtime type, so the compile-time type of the argument
                                //expression is registered as well
                                if (methodSymbol.Parameters.Length >= 2
                                    && methodSymbol.Parameters[1].Type.SpecialType == SpecialType.System_Object
                                    && invocation.ArgumentList.Arguments.Count >= 2)
                                {
                                    var modelType = semanticModel.GetTypeInfo(invocation.ArgumentList.Arguments[1].Expression).Type;
                                    if (modelType != null
                                        && modelType.TypeKind != TypeKind.Error
                                        && modelType.SpecialType != SpecialType.System_Object
                                        && !ContainsTypeParameter(modelType))
                                    {
                                        clientSymbols.Add(modelType);
                                    }
                                }
                            }
                            catch
                            {
                                Report(context, RpcDiagnostics.InvokeTypeExtractionFailed, invocation.GetLocation());
                            }
                        }
                    }

                    //server declaration
                    if (hasServer)
                    {
                        foreach (var span in serverSpans)
                        {
                            var serverAttributeClass = FindSeedNode<ClassDeclarationSyntax>(root, span);
                            if (serverAttributeClass == null)
                                continue;

                            if (serverDeclaration != null)
                            {
                                //report on the extra declaration and keep going with the first one,
                                //aborting here would leave allMethods incomplete
                                Report(context, RpcDiagnostics.MultipleRpcServers, serverAttributeClass.Identifier.GetLocation());
                                continue;
                            }
                            serverDeclaration = semanticModel.GetDeclaredSymbol(serverAttributeClass)?.GetAttributes()
                                    .FirstOrDefault(attributeData => attributeData.AttributeClass?.ToDisplayString().StartsWith(
                                                          "LiteLink.RpcServerAttribute") ?? false);
                        }
                    }

                    //[RpcFunc] methods
                    if (hasFuncs)
                    {
                        var funcMethods = new List<MethodDeclarationSyntax>();

                        foreach (var span in funcSpans)
                        {
                            var method = FindSeedNode<MethodDeclarationSyntax>(root, span);
                            if (method != null)
                                funcMethods.Add(method);
                        }

                        if (funcMethods.Count != 0)
                            allMethods.Add((semanticModel, funcMethods));
                    }
                }
            }
            catch (Exception ex)
            {
                Report(context, RpcDiagnostics.InternalError, Location.None, "collecting the RPC surface", ex.Message + " " + ex.StackTrace);
            }

            //client-assembly serializer generation is opted into with [assembly: RpcClientModels];
            //explicitly listed types (for dynamic-proxy calls the scan can't see) join the scanned roots
            var clientModelsAttribute = compilation.Assembly.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "LiteLink.RpcClientModelsAttribute");
            if (clientModelsAttribute != null
                && clientModelsAttribute.ConstructorArguments.Length == 1
                && clientModelsAttribute.ConstructorArguments[0].Kind == TypedConstantKind.Array)
            {
                foreach (var listed in clientModelsAttribute.ConstructorArguments[0].Values)
                {
                    if (listed.Value is ITypeSymbol listedType
                        && listedType.TypeKind != TypeKind.Error
                        && !ContainsTypeParameter(listedType))
                    {
                        clientModelSymbols.Add(listedType);
                    }
                }
            }

#if !DEBUG
            try
            {
#endif
                if (allMethods.Count != 0)
                    GenerateGenericMethods(allMethods, context, compilation, serverDeclaration, clientSymbols);
                else if (clientModelsAttribute != null && clientModelSymbols.Count != 0)
                    //client mode: same type walk and serializer emission with no server parts, plus
                    //structural fingerprints so RpcClient can prove handshake compatibility per model
                    GenerateGenericMethods(allMethods, context, compilation, null, clientModelSymbols, clientRegistrations: true);
#if !DEBUG
            }
            catch (Exception ex)
            {
                Report(context, RpcDiagnostics.InternalError, Location.None, "generating the serializers", ex.Message + " " + ex.StackTrace);
            }
#endif
            timer.Stop();
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "litelink_generator_log.txt"), $"[{DateTime.Now.ToShortTimeString()}] run time: {timer.ElapsedMilliseconds}ms\n");
            }
            catch (IOException)
            {
                //parallel builds race on the shared log file, losing a log line must not abort generation
            }
        }

        private static void Report(SourceProductionContext context, DiagnosticDescriptor descriptor, Location location, params object[] args)
        {
            context.ReportDiagnostic(Diagnostic.Create(descriptor, location, args));
        }

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
#if DEBUG
            //opt-in debugging: set LITELINK_GENERATOR_DEBUG=1 before building to get a debugger prompt,
            //works in any configuration and doesn't trap ordinary Debug builds
            if (Environment.GetEnvironmentVariable("LITELINK_GENERATOR_DEBUG") == "1" && !Debugger.IsAttached)
            {
                Debugger.Launch();
            }
#endif

            //Incremental pipeline: the three seed providers below answer "where is the RPC surface?" and are
            //cached per node/per tree by Roslyn — after an edit only the changed file is re-scanned.
            //Seeds are plain value tuples (path + span) so they stay equatable; all symbol work happens in
            //Execute against the fresh compilation, because type/property/group information crosses file
            //boundaries and must never be served from a stale cache.

            //methods marked [RpcFunc] (also covers methods inside [RpcGroup] classes)
            var funcSeeds = context.SyntaxProvider.ForAttributeWithMetadataName(
                "LiteLink.RpcFuncAttribute",
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, _) => Seed(ctx.TargetNode)).WithTrackingName("LiteLinkFuncSeeds").Collect();

            //the single [RpcServer<TLink>] class
            var serverSeeds = context.SyntaxProvider.ForAttributeWithMetadataName(
                "LiteLink.RpcServerAttribute`1",
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => Seed(ctx.TargetNode)).WithTrackingName("LiteLinkServerSeeds").Collect();

            //call sites that might be RpcLink.Invoke<T>/Send<T> (server assemblies) or typed
            //RpcClient calls (client assemblies); syntactic prefilter only, binding happens in Execute
            var invokeSeeds = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax invocation
                    && GetInvokeCallee(invocation)?.Identifier.Text
                        is "Invoke" or "Send" or "SendMultiChannelMessage" or "SendCore" or "RegisterFunction",
                static (ctx, _) => Seed(ctx.Node)).WithTrackingName("LiteLinkInvokeSeeds").Collect();

            var input = funcSeeds.Combine(serverSeeds).Combine(invokeSeeds).Combine(context.CompilationProvider);

            context.RegisterImplementationSourceOutput(input, (productionContext, data) =>
            {
                var (((funcs, servers), invokes), compilation) = data;
                Execute(productionContext, compilation, funcs, servers, invokes);
            });
        }

        private static (string FilePath, int Start, int Length) Seed(SyntaxNode node)
            => (node.SyntaxTree.FilePath, node.Span.Start, node.Span.Length);

        /// <summary>syntax-only extraction of the callee name of a call, for the cheap "is it named Invoke" prefilter</summary>
        private static SimpleNameSyntax GetInvokeCallee(InvocationExpressionSyntax invocation)
            => invocation.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
                MemberBindingExpressionSyntax memberBinding => memberBinding.Name, //conditional access: link?.Invoke(...)
                SimpleNameSyntax simpleName => simpleName, //unqualified call from inside a derived link
                _ => null
            };

        /// <summary>groups seed spans by file path, ordered by position so per-tree processing keeps document order</summary>
        private static Dictionary<string, List<(int Start, int Length)>> GroupSeeds(ImmutableArray<(string FilePath, int Start, int Length)> seeds)
        {
            var result = new Dictionary<string, List<(int Start, int Length)>>();
            foreach (var seed in seeds)
            {
                if (!result.TryGetValue(seed.FilePath, out var list))
                {
                    list = new List<(int Start, int Length)>();
                    result[seed.FilePath] = list;
                }
                list.Add((seed.Start, seed.Length));
            }
            foreach (var list in result.Values)
            {
                list.Sort((a, b) => a.Start.CompareTo(b.Start));
            }
            return result;
        }

        /// <summary>resolves a seed span back to its node in the current compilation; null when it doesn't resolve cleanly</summary>
        private static TNode FindSeedNode<TNode>(SyntaxNode root, (int Start, int Length) span) where TNode : SyntaxNode
        {
            var textSpan = new TextSpan(span.Start, span.Length);
            if (textSpan.End > root.FullSpan.End)
                return null;

            var node = root.FindNode(textSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<TNode>();

            //the span must match exactly, otherwise the seed belongs to a different tree with the same path
            if (node == null || node.Span != textSpan)
                return null;

            return node;
        }

        enum StorageType : ushort
        {
            Normal = 0,
            Array = 1,
            List = 2,
            Dictionary = 3,
            Nullable = 4,
            Tuple = 5
        }

        class PropertyMap
        {
            public string Name { get; set; }
            public int TypeIndex { get; set; }
            public StorageType Type { get; set; }
            public bool IsValueType { get; set; }
            public int Index { get; set; }
        }

        class TypeMap
        {
            public int Index { get; set; }
            public bool IsValueType { get; set; }
        }

        private void GenerateGenericMethods(List<(SemanticModel, List<MethodDeclarationSyntax>)> allMethods, SourceProductionContext context, Compilation compilation, AttributeData serverDeclaration, List<ITypeSymbol> clientSymbols, bool clientRegistrations = false)
        {
            var types = new List<ITypeSymbol>(clientSymbols);

            foreach (var (semanticModel, methods) in allMethods)
            {
                foreach (var method in methods)
                {
                    var methodSymbol = semanticModel.GetDeclaredSymbol(method);

                    IEnumerable<ITypeSymbol> parameterTypes = methodSymbol.Parameters
                        .Where(parameter => !IsDerivedFromRpcClient(parameter.Type, compilation))
                        .Select(parameter => parameter.Type);

                    types.AddRange(parameterTypes);

                    // Get the return type, excluding those that are or inherit from "RpcClient"
                    ITypeSymbol returnType = methodSymbol.ReturnType;
                    if (returnType.SpecialType != SpecialType.System_Void)
                    {
                        types.Add(returnType);
                    }
                }
            }

            var filteredSymbols = new List<ITypeSymbol>();
            _foundTypes = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            foreach (var typeParams in types)
            {
                FindTypes(filteredSymbols, typeParams);
            }

            var typeDictionary = GenerateTypeDictionary(filteredSymbols);

            var propertyMaps = GeneratePropertyMaps(filteredSymbols, typeDictionary);

            //first discovered symbol per encoded type key, used to point type diagnostics at the type's declaration
            var typeSymbolsByKey = new Dictionary<string, ITypeSymbol>();
            foreach (var symbol in filteredSymbols)
            {
                var key = GetTypeName(symbol);
                if (!typeSymbolsByKey.ContainsKey(key))
                    typeSymbolsByKey[key] = symbol;
            }

            Location GetTypeLocation(string typeKey) =>
                typeSymbolsByKey.TryGetValue(typeKey, out var typeSymbol) ? (typeSymbol.Locations.FirstOrDefault() ?? Location.None) : Location.None;

            //generate typedefs
            var typedefs = new List<string>();
            var typedefDict = new Dictionary<string, string>();

            foreach (var type in typeDictionary.Keys)
            {
                if (type.StartsWith("list_"))
                {
                    var subType = GetTrueName(type.Substring(5));
                    var trueName = GetTrueName(type).Replace('.', '_');
                    var typeName = "System.Collections.Generic.List<" + (typedefDict.ContainsKey(subType) ? typedefDict[subType] : subType) + ">";
                    typedefs.Add("using " + trueName + " = " + typeName + ";");
                    typedefDict[trueName] = typeName;
                }
                else if (type.StartsWith("null_"))
                {
                    var subType = GetTrueName(type.Substring(5));
                    var trueName = GetTrueName(type).Replace('.', '_');
                    var typeName = "System.Nullable<" + (typedefDict.ContainsKey(subType) ? typedefDict[subType] : subType) + ">";
                    typedefs.Add("using " + trueName + " = " + typeName + ";");
                    typedefDict[trueName] = typeName;
                }
                else if (type.Contains("~") && type.Count(x => x == '[') == 1)
                {
                    var trueName = GetTrueName(type.Substring(1, type.Length - 2)).Replace('.', '_');
                    var split = type.Substring(1, type.Length - 2).Split('~');
                    var tupleType = "System.ValueTuple<" + string.Join(",", split.Select(x => { var trueSubName = GetTrueName(x); return typedefDict.ContainsKey(trueSubName) ? typedefDict[trueSubName] : trueSubName; })) + ">";
                    typedefs.Add("using " + trueName + " = " + tupleType + ";");
                    typedefDict[trueName] = tupleType;
                }
            }

            var fileModel = new SerializerFileModel { Typedefs = typedefs };

            //reverse index -> encoded key map (first key wins, matching the previous First() lookup semantics)
            var keyByIndex = new Dictionary<int, string>();
            foreach (var kv in typeDictionary)
            {
                if (!keyByIndex.ContainsKey(kv.Value.Index))
                    keyByIndex[kv.Value.Index] = kv.Key;
            }

            //writers, readers and registrations for user-defined object types
            foreach (var kvp in propertyMaps)
            {
                var funcName = kvp.Key.Replace('.', '_');
                fileModel.Registrations.Add(new RegistrationModel { TypeDisplay = kvp.Key, FuncName = funcName });

                var objectModel = new ObjectSerializerModel
                {
                    TypeDisplay = kvp.Key,
                    FuncName = funcName,
                    IsValueType = typeDictionary[kvp.Key].IsValueType,
                };

                foreach (var propertyMap in kvp.Value)
                {
                    var typeKey = keyByIndex[propertyMap.TypeIndex];

                    var property = new PropertyEmitModel
                    {
                        Name = propertyMap.Name,
                        Kind = (PropertyKind)(ushort)propertyMap.Type,
                        TypeKey = typeKey,
                    };

                    if (propertyMap.Type == StorageType.Dictionary)
                    {
                        var split = typeKey.Substring(1, typeKey.Length - 2).Split('-');
                        property.KeyTypeKey = split[0];
                        property.ValueTypeKey = split[1];
                    }
                    else if (propertyMap.Type == StorageType.Tuple)
                    {
                        property.TupleTypeKeys = typeKey.Substring(1, typeKey.Length - 2).Split('~');
                    }
                    else if (propertyMap.Type == StorageType.Array)
                    {
                        property.IsByteArray = typeKey == "byte" || typeKey == typeof(byte).FullName;
                    }

                    objectModel.Properties.Add(property);
                }

                fileModel.WriterFunctions.Add(new ObjectWriterTemplate(objectModel).Render());
                fileModel.ReaderFunctions.Add(new ObjectReaderTemplate(objectModel).Render());

                if (clientRegistrations)
                {
                    //the structural fingerprint of the wire layout this model's generated serializers
                    //implement; RpcClient computes the same encoding from the live handshake and only
                    //binds the generated code when they match exactly
                    var fingerprintBuilder = new StringBuilder();
                    AppendFingerprint(fingerprintBuilder, kvp.Key, propertyMaps, keyByIndex, new List<string>());
                    fileModel.ClientRegistrations.Add(new ClientRegistrationModel
                    {
                        TypeDisplay = kvp.Key,
                        FingerprintLiteral = SymbolDisplay.FormatLiteral(fingerprintBuilder.ToString(), quote: true),
                    });
                }
            }

            //generate serializers for nested lists, nullables and tuples
            foreach (var typeKey in typeDictionary.Keys)
            {
                if (typeKey.StartsWith("list_") || typeKey.StartsWith("array_"))
                {
                    bool isList = typeKey[4] == '_';

                    var subType = typeKey.Substring(isList ? 5 : 6);
                    if (subType.Contains("[") || subType.Contains("~") || subType.Contains("-"))
                        subType = GetTrueName(subType);

                    var collectionModel = new CollectionSerializerModel
                    {
                        //GetTrueName resolves enum/date/guid keys to their C# names, everything else passes through
                        TypeDisplay = isList ? "System.Collections.Generic.List<" + GetTrueName(subType) + ">" : GetTrueName(subType) + "[]",
                        FuncName = GetTrueName(typeKey).Replace('.', '_'),
                        IsList = isList,
                        SubTypeKey = subType,
                        SubTypeTrueName = GetTrueName(subType),
                    };

                    fileModel.WriterFunctions.Add(new CollectionWriterTemplate(collectionModel).Render());
                    fileModel.ReaderFunctions.Add(new CollectionReaderTemplate(collectionModel).Render());
                }
                else if (typeKey.StartsWith("null_"))
                {
                    var subType = typeKey.Substring(5);
                    if (subType.Contains("[") || subType.Contains("~") || subType.Contains("-"))
                        subType = GetTrueName(subType);

                    var nullableModel = new NullableSerializerModel
                    {
                        TypeDisplay = DecodeTypeName(typeKey),
                        FuncName = GetTrueName(typeKey).Replace('.', '_'),
                        SubTypeKey = subType,
                    };

                    fileModel.WriterFunctions.Add(new NullableWriterTemplate(nullableModel).Render());
                    fileModel.ReaderFunctions.Add(new NullableReaderTemplate(nullableModel).Render());
                }
                else if (typeKey.Contains("~"))
                {
                    var tildeIndex = typeKey.IndexOf('~');
                    var dictIndex = typeKey.IndexOf('-');

                    //dictionaries whose keys/values contain tuples are handled by the dictionary logic
                    if (dictIndex != -1 && dictIndex < tildeIndex)
                        continue;

                    var innerType = typeKey.Substring(1, typeKey.Length - 2);

                    var groupIndex = innerType.IndexOf('[');
                    tildeIndex = innerType.IndexOf('~');

                    if (groupIndex != -1 && groupIndex < tildeIndex)
                        continue;

                    var split = innerType.Split('~');

                    var tupleModel = new TupleSerializerModel
                    {
                        TypeDisplay = "System.ValueTuple<" + string.Join(",", split.Select(x => GetTrueName(x))) + ">",
                        FuncName = GetTrueName(innerType).Replace('.', '_'),
                        ElementTypeKeys = split,
                    };

                    fileModel.WriterFunctions.Add(new TupleWriterTemplate(tupleModel).Render());
                    fileModel.ReaderFunctions.Add(new TupleReaderTemplate(tupleModel).Render());
                }
            }

            if (serverDeclaration != null)
            {
                var linkType = serverDeclaration.AttributeClass.TypeArguments[0] as INamedTypeSymbol;

                //where the [RpcServer] attribute is written, used for diagnostics that concern the whole server
                var serverLocation = serverDeclaration.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? Location.None;

                var serverModel = new ServerInitModel
                {
                    LinkType = linkType.ToDisplayString(),
                };

                //Get permission groups
                INamedTypeSymbol? enumType = null;

                foreach (var kvp in serverDeclaration.NamedArguments)
                {
                    var name = kvp.Key;
                    var typedConst = kvp.Value;

                    if (name == "PermissionGroups" && typedConst.Kind == TypedConstantKind.Type)
                    {
                        enumType = typedConst.Value as INamedTypeSymbol;
                        break;
                    }
                }

                string[] permissionGroups = null;

                if (enumType is not null)
                {
                    if (enumType.TypeKind != TypeKind.Enum)
                    {
                        Report(context, RpcDiagnostics.PermissionGroupsMustBeEnum, serverLocation);
                        return;
                    }
                    else
                    {
                        var members = enumType.GetMembers()
                            .OfType<IFieldSymbol>()
                            .Where(f => f.HasConstantValue);

                        var pairs = members
                            .Select(m => (m.Name, Value: Convert.ToInt64(m.ConstantValue)))
                            .ToArray();

                        var maxValue = pairs.Max(x => x.Value) + 1;
                        if (maxValue > 128)
                        {
                            Report(context, RpcDiagnostics.PermissionIndexOutOfRange, serverLocation);
                            return;
                        }

                        permissionGroups = new string[maxValue];

                        foreach (var pair in pairs)
                        {
                            permissionGroups[pair.Value] = pair.Name;
                        }

                        if (permissionGroups.Any(x => x == null))
                        {
                            Report(context, RpcDiagnostics.PermissionValuesNotSequential, serverLocation);
                            return;
                        }
                    }
                }

                if (permissionGroups == null)
                {
                    permissionGroups = new string[0];
                }

                if (permissionGroups.Length >= 128)
                {
                    Report(context, RpcDiagnostics.PermissionLimitExceeded, serverLocation);
                }

                var permissionBitmasks = new Dictionary<string, (ulong, ulong)>();

                for (int i = 0; i < permissionGroups.Length; i++)
                {
                    var group = permissionGroups[i];
                    if (group == null)
                        continue;

                    var bitmask = i < 64 ? (bitmasks[i], 0UL) : (0UL, bitmasks[i - 64]);
                    permissionBitmasks.Add(group, bitmask);
                    serverModel.PermissionGroups.Add(new PermissionGroupModel { Name = group, Mask1 = bitmask.Item1, Mask2 = bitmask.Item2 });
                }

                var useCompression = DoesUseCompression(serverDeclaration);
                serverModel.UseCompression = useCompression;

                serverModel.ShowApi = GetNamedArgument(serverDeclaration, "ShowApi")?.Value is bool showApi && showApi;
                if (GetNamedArgument(serverDeclaration, "ApiPassword")?.Value is string apiPassword)
                {
                    //FormatLiteral produces a fully escaped, quoted C# string literal so any password is emitted safely
                    serverModel.ApiPasswordLiteral = SymbolDisplay.FormatLiteral(apiPassword, quote: true);
                }

                var funcNames = new List<string>() { "Handshake" };
                var argNames = new List<string>() { "" };
                var retNames = new List<string>() { "" };

                var funcMappings = new Dictionary<string, FuncMapping> { { "Handshake", new FuncMapping() } };

                foreach (var (semanticModel, methods) in allMethods)
                {
                    foreach (var method in methods)
                    {
                        var methodSymbol = semanticModel.GetDeclaredSymbol(method);
                        var groupName = GetGroupName(methodSymbol);

                        groupName = string.Join(".", groupName, GetFuncName(methodSymbol));

                        if (funcMappings.ContainsKey(groupName)) //funcMappings keys mirror funcNames, O(1) duplicate check
                        {
                            //skip the duplicate so the rest of the surface still generates, funcMappings.Add would throw otherwise
                            Report(context, RpcDiagnostics.DuplicateFunction, method.Identifier.GetLocation(), groupName, methodSymbol.ToDisplayString());
                            continue;
                        }

                        var parameters = methodSymbol.Parameters.ToList();

                        if (parameters.Count == 0 || parameters[0].Type.ToDisplayString() != linkType.ToDisplayString())
                        {
                            Report(context, RpcDiagnostics.FirstParameterMustBeLink, method.ParameterList.GetLocation(), methodSymbol.ToDisplayString(), linkType.ToDisplayString());
                        }

                        if (parameters.Count > 2)
                        {
                            Report(context, RpcDiagnostics.TooManyParameters, method.ParameterList.GetLocation(), methodSymbol.ToDisplayString());
                        }

                        var methodPermission = (0UL, 0UL);

                        //get permission groups for this method and group

                        var attrSyntax = method.AttributeLists
                        .SelectMany(list => list.Attributes)
                        .FirstOrDefault(attribute => attribute.Name.ToString() == "RpcFunc");

                        var permissionGroupStrings = new List<string>();

                        permissionGroupStrings.AddRange(ExtractPermissionGroupsFromAttribute(attrSyntax));

                        //check for RpcGroup
                        var classDeclarationSyntax = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                        if (classDeclarationSyntax != null)
                        {
                            var classAttributeSyntax = classDeclarationSyntax.AttributeLists
                                .SelectMany(list => list.Attributes)
                                .FirstOrDefault(attribute => attribute.Name.ToString() == "RpcGroup");

                            if (classAttributeSyntax != null)
                            {
                                permissionGroupStrings.AddRange(ExtractPermissionGroupsFromAttribute(classAttributeSyntax));
                            }
                        }

                        foreach (var perm in permissionGroupStrings)
                        {
                            if (!permissionBitmasks.TryGetValue(perm, out var curPerm))
                            {
                                Report(context, RpcDiagnostics.UndeclaredPermission,
                                    attrSyntax?.GetLocation() ?? method.Identifier.GetLocation(),
                                    perm, methodSymbol.ToDisplayString());
                                continue;
                            }
                            methodPermission = (methodPermission.Item1 | curPerm.Item1, methodPermission.Item2 | curPerm.Item2);
                        }

                        var funcMapping = new FuncMapping();

                        //per-function compression override: RpcCompression Inherit=0 / Enabled=1 / Disabled=2
                        var compressionOverride = 0;
                        var funcAttrData = methodSymbol.GetAttributes()
                            .FirstOrDefault(a => a.AttributeClass?.Name == "RpcFuncAttribute");
                        if (funcAttrData != null && GetNamedArgument(funcAttrData, "Compression")?.Value is int compressionValue)
                            compressionOverride = compressionValue;
                        var procedureCompresses = compressionOverride == 1 || (compressionOverride == 0 && useCompression);

                        var procedure = new ProcedureModel
                        {
                            Opcode = funcNames.Count,
                            PermissionMask1 = methodPermission.Item1,
                            PermissionMask2 = methodPermission.Item2,
                            MethodName = string.Join(".", methodSymbol.ContainingType.ToDisplayString(), methodSymbol.Name),
                            RpcName = groupName,
                            HasArg = parameters.Count == 2,
                            Compresses = procedureCompresses,
                        };

                        if (parameters.Count == 2)
                        {
                            var argTypeName = GetTypeName(parameters[1].Type);
                            funcMapping.ArgType = typeDictionary[argTypeName].Index;
                            procedure.ArgTypeDisplay = methodSymbol.Parameters[1].Type.ToDisplayString();
                        }

                        if (methodSymbol.ReturnsVoid)
                        {
                            procedure.ReturnKind = ProcedureReturnKind.Void;
                        }
                        else if (methodSymbol.ReturnType.ToDisplayString().StartsWith("System.Threading.Tasks.Task"))
                        {
                            var namedSymbol = (methodSymbol.ReturnType as INamedTypeSymbol);
                            if (namedSymbol.TypeArguments.Length == 0)
                            {
                                //task awaitable
                                procedure.ReturnKind = ProcedureReturnKind.Task;
                            }
                            else
                            {
                                var returnType = namedSymbol.TypeArguments[0];
                                var retTypeName = GetTypeName(returnType);
                                funcMapping.RetType = typeDictionary[retTypeName].Index;

                                procedure.ReturnKind = ProcedureReturnKind.TaskWithValue;
                                procedure.ReturnTypeDisplay = returnType.ToDisplayString();
                            }
                        }
                        else
                        {
                            var retTypeName = GetTypeName(methodSymbol.ReturnType);
                            funcMapping.RetType = typeDictionary[retTypeName].Index;

                            procedure.ReturnKind = ProcedureReturnKind.Sync;
                        }

                        serverModel.Procedures.Add(procedure);

                        funcNames.Add(groupName);
                        if (parameters.Count == 2)
                        {
                            var storageType = GetTypeStorageType(parameters[1].Type);
                            if (storageType == StorageType.List)
                            {
                                argNames.Add((parameters[1].Type as INamedTypeSymbol).TypeArguments[0].Name + "[] " + parameters[1].Name);
                            }
                            else if (storageType == StorageType.Array)
                            {
                                argNames.Add((parameters[1].Type as IArrayTypeSymbol).ElementType.Name + "[] " + parameters[1].Name);
                            }
                            else if (storageType == StorageType.Nullable)
                            {
                                argNames.Add((parameters[1].Type as INamedTypeSymbol).TypeArguments[0].Name + "? " + parameters[1].Name);
                            }
                            else argNames.Add(parameters[1].Type.Name + " " + parameters[1].Name);
                        }
                        else
                        {
                            argNames.Add("");
                        }

                        if (methodSymbol.ReturnsVoid)
                        {
                            retNames.Add("");
                        }
                        else
                        {
                            if (methodSymbol.ReturnType.ToDisplayString().StartsWith("System.Threading.Tasks.Task"))
                            {
                                var namedSymbol = (methodSymbol.ReturnType as INamedTypeSymbol);
                                if (namedSymbol.TypeArguments.Length == 0)
                                {
                                    retNames.Add("");
                                }
                                else
                                {
                                    var storageType = GetTypeStorageType(namedSymbol.TypeArguments[0]);
                                    if (storageType == StorageType.List)
                                    {
                                        retNames.Add((namedSymbol.TypeArguments[0] as INamedTypeSymbol).TypeArguments[0].Name + "[] ");
                                    }
                                    else if (storageType == StorageType.Array)
                                    {
                                        retNames.Add((namedSymbol.TypeArguments[0] as IArrayTypeSymbol).ElementType.Name + "[]");
                                    }
                                    else if (storageType == StorageType.Nullable)
                                    {
                                        retNames.Add(namedSymbol.TypeArguments[0].Name + "?");
                                    }
                                    else retNames.Add(namedSymbol.Name);
                                }
                            }
                            else
                            {
                                var storageType = GetTypeStorageType(methodSymbol.ReturnType);
                                if (storageType == StorageType.List)
                                {
                                    retNames.Add((methodSymbol.ReturnType as INamedTypeSymbol).TypeArguments[0].Name + "[]");
                                }
                                else if (storageType == StorageType.Array)
                                {
                                    retNames.Add((methodSymbol.ReturnType as IArrayTypeSymbol).ElementType.Name + "[]");
                                }
                                else if (storageType == StorageType.Nullable)
                                {
                                    retNames.Add((methodSymbol.ReturnType as INamedTypeSymbol).TypeArguments[0].Name + "?");
                                }
                                else retNames.Add(methodSymbol.ReturnType.Name);
                            }
                        }
                        funcMappings.Add(groupName, funcMapping);
                    }
                }

                //determine what crc to use
                int functionCRC = 1;

                if (HasCRCCollision(functionCRC, funcNames))
                {
                    functionCRC = 2;
                    if (HasCRCCollision(functionCRC, funcNames))
                    {
                        functionCRC = 4;
                        if (HasCRCCollision(functionCRC, funcNames))
                        {
                            functionCRC = 8;
                            if (HasCRCCollision(functionCRC, funcNames))
                            {
                                Report(context, RpcDiagnostics.FunctionCrcCollision, serverLocation);
                            }
                        }
                    }
                }

                serverModel.FunctionCrcBytes = functionCRC;
                serverModel.FuncCrcMethod = CrcWriteMethod(functionCRC);

                //property maps
                var propertyNames = new HashSet<string>();

                foreach (var kvp in propertyMaps)
                {
                    kvp.Value.ForEach(x => { propertyNames.Add(x.Name); });
                }

                var propertyNamesList = propertyNames.ToList();

                var propertyNameIndexes = new Dictionary<string, int>();
                for (int i = 0; i < propertyNamesList.Count; i++)
                {
                    propertyNameIndexes[propertyNamesList[i]] = i;
                }

                int propertyBytes = 1;

                if (propertyNamesList.Count > 0xff)
                {
                    propertyBytes = 2;

                    if (propertyNamesList.Count > 0xffff)
                    {
                        propertyBytes = 4;
                    }
                }

                serverModel.PropertyIndexBytes = propertyBytes;
                serverModel.PropertyIndexMethod = IndexWriteMethod(propertyBytes);
                serverModel.PropertyNames = propertyNamesList;

                int typeBytes = typeDictionary.Count <= 0xff ? 1 : typeDictionary.Count <= 0xffff ? 2 : 4;

                serverModel.TypeIndexBytes = typeBytes;
                serverModel.TypeIndexMethod = IndexWriteMethod(typeBytes);

                var typeList = typeDictionary.OrderBy(x => x.Value.Index).ToList();

                for (int i = 0; i < typeList.Count; i++)
                {
                    var typemap = typeList[i];
                    var typeKey = typemap.Key;
                    var typeLocation = GetTypeLocation(typemap.Key);

                    var entry = new TypeEntryModel
                    {
                        Index = i,
                        DecodedName = DecodeTypeName(typeKey),
                    };

                    if (typeKey.StartsWith("enum_"))
                        typeKey = GetEnumUnderlying(typeKey); //clients read enums as their underlying primitive

                    entry.Key = typeKey;

                    if (typemap.Value.Index != i)
                    {
                        Report(context, RpcDiagnostics.IncorrectTypeIndex, typeLocation, typeKey);
                    }

                    if (!propertyMaps.ContainsKey(typeKey))
                    {
                        if (typemap.Key.StartsWith("["))
                        {
                            //it's either dict or tuple
                            var dictIndex = typemap.Key.IndexOf('-');
                            var tupleIndex = typemap.Key.IndexOf('~');

                            if (dictIndex != -1 && (tupleIndex == -1 || dictIndex < tupleIndex))
                            {
                                entry.Kind = TypeEntryKind.Dictionary;

                                //enum keys resolve to the enum's own entry, whose handshake row already
                                //declares the underlying primitive (same as tuple elements)
                                var split = typeKey.Substring(1, typeKey.Length - 2).Split('-');

                                entry.KeyTypeKey = split[0];
                                entry.ValueTypeKey = split[1];
                                entry.KeyTypeIndex = GetHandshakeTypeIndex(context, typeDictionary, split[0], typeLocation);
                                entry.ValueTypeIndex = GetHandshakeTypeIndex(context, typeDictionary, split[1], typeLocation);
                            }
                            else
                            {
                                entry.Kind = TypeEntryKind.Tuple;

                                var split = typemap.Key.Substring(1, typeKey.Length - 2).Split('~');
                                entry.TupleElements = new List<TupleElementModel>();

                                for (int j = 0; j < split.Length; j++)
                                {
                                    entry.TupleElements.Add(new TupleElementModel
                                    {
                                        Position = j,
                                        Key = split[j],
                                        TypeIndex = GetHandshakeTypeIndex(context, typeDictionary, split[j], typeLocation),
                                    });
                                }
                            }
                        }
                        else if (typemap.Key.StartsWith("list_"))
                        {
                            entry.Kind = TypeEntryKind.Collection;
                            entry.IsArray = false;
                            entry.SubTypeKey = typemap.Key.Substring(5);
                            entry.SubTypeIndex = GetHandshakeTypeIndex(context, typeDictionary, entry.SubTypeKey, typeLocation);
                        }
                        else if (typemap.Key.StartsWith("array_"))
                        {
                            entry.Kind = TypeEntryKind.Collection;
                            entry.IsArray = true;
                            entry.SubTypeKey = typemap.Key.Substring(6);
                            entry.SubTypeIndex = GetHandshakeTypeIndex(context, typeDictionary, entry.SubTypeKey, typeLocation);
                        }
                        else if (typemap.Key.StartsWith("null_"))
                        {
                            entry.Kind = TypeEntryKind.Nullable;
                            entry.SubTypeKey = typemap.Key.Substring(5);
                            entry.SubTypeIndex = GetHandshakeTypeIndex(context, typeDictionary, entry.SubTypeKey, typeLocation);
                        }
                        else
                        {
                            entry.Kind = TypeEntryKind.Base;
                        }
                    }
                    else
                    {
                        var propertyMap = propertyMaps[typeKey];

                        if (propertyMap.Count >= 0xffff)
                            Report(context, RpcDiagnostics.TooManyProperties, typeLocation, typemap.Key, propertyMap.Count);

                        entry.Kind = TypeEntryKind.PropertyMap;
                        entry.Properties = new List<TypeMapPropertyModel>();

                        for (int j = 0; j < propertyMap.Count; j++)
                        {
                            var curmap = propertyMap[j];

                            entry.Properties.Add(new TypeMapPropertyModel
                            {
                                Name = curmap.Name,
                                LocalIndex = j,
                                PropertyNameIndex = propertyNameIndexes[curmap.Name],
                                TypeIndex = curmap.TypeIndex,
                                TypeIndexKey = typeList[curmap.TypeIndex].Key,
                                StorageType = (int)curmap.Type,
                            });
                        }
                    }

                    serverModel.Types.Add(entry);
                }

                for (int i = 0; i < funcNames.Count; i++)
                {
                    var mapping = funcMappings[funcNames[i]];

                    serverModel.Functions.Add(new FunctionEntryModel
                    {
                        Opcode = i,
                        CrcValue = CrcValue(functionCRC, funcNames[i]),
                        ArgTypeIndex = mapping.ArgType,
                        RetTypeIndex = mapping.RetType,
                        Name = funcNames[i],
                        ArgName = argNames[i],
                        RetName = retNames[i],
                    });
                }

                //determine what crc to use
                var filteredPermissions = permissionGroups.Where(x => x != null).ToList();
                int permissionCRC = 1;

                if (HasCRCCollision(permissionCRC, filteredPermissions))
                {
                    permissionCRC = 2;
                    if (HasCRCCollision(permissionCRC, filteredPermissions))
                    {
                        permissionCRC = 4;
                        if (HasCRCCollision(permissionCRC, filteredPermissions))
                        {
                            permissionCRC = 8;
                            if (HasCRCCollision(permissionCRC, filteredPermissions))
                            {
                                Report(context, RpcDiagnostics.PermissionCrcCollision, serverLocation);
                            }
                        }
                    }
                }

                serverModel.PermissionCrcBytes = permissionCRC;
                serverModel.PermCrcMethod = CrcWriteMethod(permissionCRC);

                for (int i = 0; i < permissionGroups.Length; i++)
                {
                    serverModel.PermissionCrcValues.Add(CrcValue(permissionCRC, permissionGroups[i]));
                }

                serverModel.PermissionCount = permissionGroups.Length;

                fileModel.ServerInitializer = new ServerInitializerTemplate(serverModel).Render();
            }

            SourceText sourceText = SourceText.From(new SerializerFileTemplate(fileModel).Render(), Encoding.UTF8);
            context.AddSource("LiteSerializerExtension.Generated.cs", sourceText);
        }

        //(int)BasicTypes codes as the C# client declares them; part of the fingerprint wire contract
        private static readonly Dictionary<string, int> _fingerprintBasicCodes = new Dictionary<string, int>
        {
            { "bool", 0 }, { "sbyte", 1 }, { "byte", 2 }, { "short", 3 }, { "ushort", 4 },
            { "int", 5 }, { "uint", 6 }, { "long", 7 }, { "ulong", 8 }, { "float", 9 },
            { "double", 10 }, { "decimal", 11 }, { "string", 12 }, { "date", 13 }, { "guid", 14 },
        };

        /// <summary>
        /// Canonical structural fingerprint of an encoded type key, matching what RpcClient computes
        /// from a live handshake (RpcClient.AppendFingerprint) - both sides must stay in step:
        /// basics are "b" + BasicTypes code (enums as their underlying primitive), collections
        /// "c[..]", nullables "n[..]", dictionaries "d[k,v]", tuples "t[..]", objects
        /// "o{name=..;..}" in wire property order, cycles "#" + path position. A shape this encoder
        /// doesn't understand emits "?" + key, which no handshake ever produces, so unknown shapes
        /// can only ever fall back to the dynamic path - never falsely match.
        /// </summary>
        private void AppendFingerprint(StringBuilder sb, string typeKey,
            Dictionary<string, List<PropertyMap>> propertyMaps, Dictionary<int, string> keyByIndex, List<string> path)
        {
            if (typeKey.StartsWith("enum_"))
            {
                var rest = typeKey.Substring(5);
                var underscoreIdx = rest.IndexOf('_');
                var underlying = underscoreIdx > 0 ? rest.Substring(0, underscoreIdx) : "int";
                sb.Append('b').Append(_fingerprintBasicCodes.TryGetValue(underlying, out var enumCode) ? enumCode : 5);
                return;
            }

            if (_fingerprintBasicCodes.TryGetValue(typeKey, out var code))
            {
                sb.Append('b').Append(code);
                return;
            }

            var backRef = path.IndexOf(typeKey);
            if (backRef >= 0)
            {
                sb.Append('#').Append(backRef);
                return;
            }

            path.Add(typeKey);
            try
            {
                if (typeKey.StartsWith("list_"))
                {
                    sb.Append("c[");
                    AppendFingerprint(sb, typeKey.Substring(5), propertyMaps, keyByIndex, path);
                    sb.Append(']');
                }
                else if (typeKey.StartsWith("array_"))
                {
                    sb.Append("c[");
                    AppendFingerprint(sb, typeKey.Substring(6), propertyMaps, keyByIndex, path);
                    sb.Append(']');
                }
                else if (typeKey.StartsWith("null_"))
                {
                    sb.Append("n[");
                    AppendFingerprint(sb, typeKey.Substring(5), propertyMaps, keyByIndex, path);
                    sb.Append(']');
                }
                else if (typeKey.StartsWith("["))
                {
                    //same naive key decomposition the emit code and the handshake builder use, so
                    //the fingerprint mirrors exactly what actually lands on the wire
                    var inner = typeKey.Substring(1, typeKey.Length - 2);
                    var dashIndex = inner.IndexOf('-');
                    var tildeIndex = inner.IndexOf('~');
                    if (dashIndex != -1 && (tildeIndex == -1 || dashIndex < tildeIndex))
                    {
                        var split = inner.Split('-');
                        if (split.Length != 2)
                        {
                            sb.Append('?').Append(typeKey);
                            return;
                        }
                        sb.Append("d[");
                        AppendFingerprint(sb, split[0], propertyMaps, keyByIndex, path);
                        sb.Append(',');
                        AppendFingerprint(sb, split[1], propertyMaps, keyByIndex, path);
                        sb.Append(']');
                    }
                    else
                    {
                        var elements = inner.Split('~');
                        sb.Append("t[");
                        for (int i = 0; i < elements.Length; i++)
                        {
                            if (i > 0)
                                sb.Append(',');
                            AppendFingerprint(sb, elements[i], propertyMaps, keyByIndex, path);
                        }
                        sb.Append(']');
                    }
                }
                else if (propertyMaps.TryGetValue(typeKey, out var properties))
                {
                    sb.Append("o{");
                    for (int i = 0; i < properties.Count; i++)
                    {
                        var property = properties[i];
                        if (i > 0)
                            sb.Append(';');
                        sb.Append(property.Name).Append('=');

                        var childKey = keyByIndex[property.TypeIndex];
                        if (property.Type == StorageType.Array || property.Type == StorageType.List)
                        {
                            sb.Append("c[");
                            AppendFingerprint(sb, childKey, propertyMaps, keyByIndex, path);
                            sb.Append(']');
                        }
                        else if (property.Type == StorageType.Nullable)
                        {
                            sb.Append("n[");
                            AppendFingerprint(sb, childKey, propertyMaps, keyByIndex, path);
                            sb.Append(']');
                        }
                        else
                        {
                            AppendFingerprint(sb, childKey, propertyMaps, keyByIndex, path);
                        }
                    }
                    sb.Append('}');
                }
                else
                {
                    sb.Append('?').Append(typeKey);
                }
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        /// <summary>LiteWriter method matching an index width in bytes.</summary>
        static string IndexWriteMethod(int bytes)
            => bytes == 1 ? "WriteByte" : bytes == 2 ? "WriteUInt16" : "WriteUInt32";

        /// <summary>LiteWriter method matching a CRC width in bytes.</summary>
        static string CrcWriteMethod(int bytes)
            => bytes == 1 ? "WriteByte" : bytes == 2 ? "WriteUInt16" : bytes == 4 ? "WriteUInt32" : "WriteUInt64";

        /// <summary>CRC of a name at the given width, formatted as a decimal literal.</summary>
        static string CrcValue(int bytes, string name)
        {
            switch (bytes)
            {
                case 1: return CRCFunctions.CalculateCRC8(name).ToString();
                case 2: return CRCFunctions.CalculateCRC16(name).ToString();
                case 4: return CRCFunctions.CalculateCRC32(name).ToString();
                default: return CRCFunctions.CalculateCRC64(name).ToString();
            }
        }

        static int GetHandshakeTypeIndex(SourceProductionContext context, Dictionary<string, TypeMap> typeDictionary, string typeKey, Location location)
        {
            if (typeDictionary.TryGetValue(typeKey, out var typeMap))
                return typeMap.Index;

            Report(context, RpcDiagnostics.UnexpectedType, location, typeKey);
            //internal invariant failure, abort so no broken source is emitted (surfaces as RPCGEN016 alongside the diagnostic above)
            throw new Exception("Type '" + typeKey + "' is missing from the generated type table");
        }

        private class FuncMapping
        {
            public int? ArgType;
            public int? RetType;
        }

        private static TypedConstant? GetNamedArgument(AttributeData attributeData, string name)
        {
            foreach (var namedArgument in attributeData.NamedArguments)
            {
                if (namedArgument.Key == name)
                    return namedArgument.Value;
            }
            return null;
        }

        public static bool DoesUseCompression(AttributeData attributeData)
        {
            foreach (var namedArgument in attributeData.NamedArguments)
            {
                if (namedArgument.Key == "UseCompression")
                {
                    if (namedArgument.Value.Value is bool value)
                    {
                        return value;
                    }
                }
            }

            return false;
        }

        private bool HasCRCCollision(int bytes, List<string> names)
        {
            if (bytes == 1)
            {
                var hashset = new HashSet<byte>();
                foreach (var name in names)
                {
                    var crc = CRCFunctions.CalculateCRC8(name);
                    if (hashset.Contains(crc))
                    {
                        return true;
                    }
                    hashset.Add(crc);
                }
                return false;
            }
            else if (bytes == 2)
            {
                var hashset = new HashSet<ushort>();
                foreach (var name in names)
                {
                    var crc = CRCFunctions.CalculateCRC16(name);
                    if (hashset.Contains(crc))
                    {
                        return true;
                    }
                    hashset.Add(crc);
                }
                return false;
            }
            else if (bytes == 4)
            {
                var hashset = new HashSet<uint>();
                foreach (var name in names)
                {
                    var crc = CRCFunctions.CalculateCRC32(name);
                    if (hashset.Contains(crc))
                    {
                        return true;
                    }
                    hashset.Add(crc);
                }
                return false;
            }
            else if (bytes == 8)
            {
                var hashset = new HashSet<ulong>();
                foreach (var name in names)
                {
                    var crc = CRCFunctions.CalculateCRC64(name);
                    if (hashset.Contains(crc))
                    {
                        return true;
                    }
                    hashset.Add(crc);
                }
                return false;
            }
            else throw new Exception("unknown CRC bytes " + bytes);
        }

        string GetGroupName(IMethodSymbol methodSymbol)
        {
            var parentClasses = new List<string>();

            // Traverse through the parent classes
            var containingType = methodSymbol.ContainingType;
            while (containingType != null)
            {
                // Check if the parent class has the RpcGroupAttribute
                var attr = containingType.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.Name == "RpcGroupAttribute");

                if (attr != null)
                {
                    // Get the Name property value from the attribute
                    var attributeName = attr.NamedArguments
                        .FirstOrDefault(arg => arg.Key == "Name")
                        .Value.Value?.ToString();

                    if (!string.IsNullOrEmpty(attributeName))
                        parentClasses.Add(attributeName);
                    else
                    {
                        attributeName = attr.ConstructorArguments
                            .FirstOrDefault()
                            .Value.ToString();

                        if (!string.IsNullOrEmpty(attributeName))
                            parentClasses.Add(attributeName);
                        else
                        {
                            parentClasses.Add(containingType.Name);
                        }
                    }
                }

                containingType = containingType.BaseType;
            }

            // Reverse the order to start from the topmost parent class
            parentClasses.Reverse();

            return string.Join(".", parentClasses);
        }

        /// <summary>adds to the discovery list while keeping the O(1) membership set in sync</summary>
        private void AddFoundType(List<ITypeSymbol> filteredSymbols, ITypeSymbol type)
        {
            filteredSymbols.Add(type);
            _foundTypes.Add(type);
        }

        private bool IsKnownType(ITypeSymbol type) => _foundTypes.Contains(type);

        private void FindTypes(List<ITypeSymbol> filteredSymbols, ITypeSymbol typeParam)
        {
            // Add non-generic types directly
            {
                var isTask = typeParam.ToDisplayString().StartsWith("System.Threading.Tasks.Task");

                if (isTask)
                {
                    var namedSymbol = (typeParam as INamedTypeSymbol);
                    if (namedSymbol.TypeArguments.Length == 0)
                        return;

                    typeParam = namedSymbol.TypeArguments[0];
                }

                var storageType = GetTypeStorageType(typeParam);

                if (storageType == StorageType.Normal)
                {
                    if (!IsKnownType(typeParam))
                    {
                        AddFoundType(filteredSymbols, typeParam);
                        GetTargetTypes(filteredSymbols, typeParam);
                    }
                }
                else if (storageType == StorageType.Array)
                {
                    var arrayType = typeParam as IArrayTypeSymbol;
                    var type = arrayType.ElementType;
                    AddFoundType(filteredSymbols, typeParam);
                    if (GetTypeStorageType(type) != StorageType.Normal)
                    {
                        FindTypes(filteredSymbols, type);
                    }
                    else if (!IsKnownType(type))
                    {
                        AddFoundType(filteredSymbols, type);
                        GetTargetTypes(filteredSymbols, type);
                    }
                }
                else if (storageType == StorageType.List)
                {
                    var namedType = typeParam as INamedTypeSymbol;
                    var type = namedType.TypeArguments[0];
                    AddFoundType(filteredSymbols, typeParam);
                    if (GetTypeStorageType(type) != StorageType.Normal)
                    {
                        FindTypes(filteredSymbols, type);
                    }
                    else if (!IsKnownType(type))
                    {
                        AddFoundType(filteredSymbols, type);
                        GetTargetTypes(filteredSymbols, type);
                    }
                }
                else if (storageType == StorageType.Nullable)
                {
                    var namedType = typeParam as INamedTypeSymbol;
                    var type = namedType.TypeArguments[0];
                    AddFoundType(filteredSymbols, typeParam);
                    if (GetTypeStorageType(type) != StorageType.Normal)
                    {
                        AddFoundType(filteredSymbols, typeParam);
                        FindTypes(filteredSymbols, type);
                    }
                    else if (!IsKnownType(type))
                    {
                        AddFoundType(filteredSymbols, type);
                        GetTargetTypes(filteredSymbols, type);
                    }
                }
                else if (storageType == StorageType.Tuple)
                {
                    if (!IsKnownType(typeParam))
                    {
                        AddFoundType(filteredSymbols, typeParam);
                        GetTargetTypes(filteredSymbols, typeParam);
                    }
                    var namedType = typeParam as INamedTypeSymbol;
                    foreach (var arg in namedType.TypeArguments)
                    {
                        FindTypes(filteredSymbols, arg);
                    }
                }
                else //dictionary
                {
                    AddFoundType(filteredSymbols, typeParam);

                    var namedType = typeParam as INamedTypeSymbol;
                    var type = namedType.TypeArguments[0];
                    if (!IsKnownType(type))
                    {
                        AddFoundType(filteredSymbols, type);
                        GetTargetTypes(filteredSymbols, type);
                    }
                    type = namedType.TypeArguments[1];
                    if (!IsKnownType(type))
                    {
                        AddFoundType(filteredSymbols, type);
                        GetTargetTypes(filteredSymbols, type);
                    }
                }

            }
        }

        string GetFuncName(IMethodSymbol methodSymbol)
        {

            var attr = methodSymbol.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "RpcFuncAttribute");

            if (attr != null)
            {
                // Get the Name property value from the attribute
                var attributeName = attr.NamedArguments
                    .FirstOrDefault(arg => arg.Key == "Name")
                    .Value.Value?.ToString();

                if (!string.IsNullOrEmpty(attributeName))
                    return attributeName;

                attributeName = attr.ConstructorArguments
                    .FirstOrDefault()
                    .Value?.ToString();

                if (!string.IsNullOrEmpty(attributeName))
                    return attributeName;
            }

            return methodSymbol.Name;
        }

        List<string> ExtractPermissionGroupsFromAttribute(AttributeSyntax attributeSyntax)
        {
            var argumentSyntax = attributeSyntax.ArgumentList?.Arguments
                .FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == "PermissionGroups");

            List<string> permissionGroups = argumentSyntax?.Expression switch
            {
                ImplicitArrayCreationExpressionSyntax implicitArrayCreationSyntax => implicitArrayCreationSyntax
                    .Initializer?.Expressions.Select(expr => expr.ToString().Trim('"')).ToList(),
                ArrayCreationExpressionSyntax arrayCreationSyntax => arrayCreationSyntax
                    .Initializer?.Expressions.Select(expr => expr.ToString().Trim('"')).ToList(),
                LiteralExpressionSyntax literalSyntax => new List<string> { literalSyntax.ToString().Trim('"') },
                _ => null
            };

            return permissionGroups == null ? new List<string>() : permissionGroups;
        }

        private void GetTargetTypes(List<ITypeSymbol> symbols, ITypeSymbol typeSymbol)
        {
            if (GetFullTypeName(typeSymbol).StartsWith("System.")) //ignore system types
                return;

            var publicProperties = GetAllPropertiesWithBaseTypes(typeSymbol);
            foreach (var propertySymbol in publicProperties)
            {
                var storageType = GetTypeStorageType(propertySymbol.Type);

                if (storageType == StorageType.Normal)
                {
                    if (!IsKnownType(propertySymbol.Type))
                    {
                        FindTypes(symbols, propertySymbol.Type);
                    }
                }
                else if (storageType == StorageType.Nullable)
                {
                    var namedType = propertySymbol.Type as INamedTypeSymbol;
                    var type = namedType.TypeArguments[0];
                    if (!IsKnownType(type))
                    {
                        FindTypes(symbols, type);
                    }
                }
                else if (storageType == StorageType.Array)
                {
                    var arrayType = propertySymbol.Type as IArrayTypeSymbol;
                    var type = arrayType.ElementType;
                    if (!IsKnownType(type))
                    {
                        FindTypes(symbols, type);
                    }
                }
                else if (storageType == StorageType.List)
                {
                    var namedType = propertySymbol.Type as INamedTypeSymbol;
                    var type = namedType.TypeArguments[0];
                    if (!IsKnownType(type))
                    {
                        FindTypes(symbols, type);
                    }
                }
                else if (storageType == StorageType.Tuple)
                {
                    var namedType = propertySymbol.Type as INamedTypeSymbol;
                    foreach (var field in namedType.TupleElements)
                    {
                        var type = field.Type;
                        if (!IsKnownType(type))
                        {
                            FindTypes(symbols, type);
                        }
                    }
                }
                else //dictionary
                {
                    var namedType = propertySymbol.Type as INamedTypeSymbol;
                    var type = namedType.TypeArguments[0];
                    if (!IsKnownType(type))
                    {
                        FindTypes(symbols, type);
                    }
                    type = namedType.TypeArguments[1];
                    if (!IsKnownType(type))
                    {
                        FindTypes(symbols, type);
                    }
                }
            }
        }

        static List<string> ExtractNestedTypes(string input)
        {
            input = input.Replace("list_", "").Replace("array_", "");
            List<string> types = new List<string>();
            Stack<int> stack = new Stack<int>();
            int startIndex = -1;

            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == '[')
                {
                    stack.Push(i);
                    if (startIndex == -1)
                    {
                        startIndex = i;
                    }
                }
                else if (input[i] == ']')
                {
                    int openingBracketIndex = stack.Pop();
                    if (stack.Count == 0)
                    {
                        types.Add(input.Substring(openingBracketIndex + 1, i - openingBracketIndex - 1));
                        startIndex = -1;
                    }
                }
            }

            return types;
        }

        Dictionary<string, TypeMap> GenerateTypeDictionary(List<ITypeSymbol> symbols)
        {
            Dictionary<string, TypeMap> typeDictionary = new Dictionary<string, TypeMap>();

            int index = 0;

            foreach (var symbol in symbols)
            {
                string typeName = GetTypeName(symbol);
                if (!typeDictionary.ContainsKey(typeName))
                {
                    if (symbol is INamedTypeSymbol)
                    {
                        var nested = GenerateTypeDictionary((symbol as INamedTypeSymbol).TypeArguments.ToList());
                        foreach (var nest in nested)
                        {
                            if (!typeDictionary.ContainsKey(nest.Key))
                            {
                                nest.Value.Index = index++;
                                typeDictionary.Add(nest.Key, nest.Value);
                            }
                        }
                    }
                    else if (symbol is IArrayTypeSymbol)
                    {
                        var elementType = (symbol as IArrayTypeSymbol).ElementType;
                        var elementTypeName = GetTypeName(elementType);
                        if (!typeDictionary.ContainsKey(elementTypeName))
                        {
                            typeDictionary.Add(elementTypeName, new TypeMap() { Index = index++, IsValueType = !symbol.IsReferenceType });
                        }
                    }
                    typeDictionary.Add(typeName, new TypeMap() { Index = index, IsValueType = !symbol.IsReferenceType });
                    index++;
                }
            }

            return typeDictionary;
        }

        private Dictionary<string, List<PropertyMap>> GeneratePropertyMaps(List<ITypeSymbol> filteredSymbols, Dictionary<string, TypeMap> typeDictionary)
        {
            var typeMaps = new Dictionary<string, List<PropertyMap>>();

            foreach (var typeSymbol in filteredSymbols)
            {
                var typeSymbolName = GetTypeName(typeSymbol);
                if (typeMaps.ContainsKey(typeSymbol.ToDisplayString()) || typeMaps.ContainsKey(typeSymbolName))
                    continue;

                var isSystemType = (typeSymbol.ContainingNamespace?.ToDisplayString().StartsWith("System")).GetValueOrDefault(); //&& !IsListType(typeSymbol) && !IsDictionaryType(typeSymbol) && !typeSymbol.IsTupleType;

                if (!isSystemType)
                {
                    if (typeSymbol is IArrayTypeSymbol arraySymbol)
                    {
                        isSystemType = (arraySymbol.ElementType.ContainingNamespace?.ToDisplayString().StartsWith("System")).GetValueOrDefault();
                    }
                }

                if (typeSymbol.TypeKind == TypeKind.Enum || typeSymbol.SpecialType == SpecialType.System_DateTime || typeSymbol.ToDisplayString() == "System.Guid" || (typeSymbol.SpecialType != SpecialType.None && typeSymbol.SpecialType != SpecialType.System_Object) || isSystemType)
                    continue;

                var propertyMaps = new List<PropertyMap>();

                var publicProperties = GetAllPropertiesWithBaseTypes(typeSymbol); //typeSymbol.GetMembers().OfType<IPropertySymbol>().Where(p => p.SetMethod != null && !p.IsIndexer);

                foreach (var propertySymbol in publicProperties)
                {
                    var storage = GetTypeStorageType(propertySymbol.Type);

                    PropertyMap propertyMap;

                    if (storage == StorageType.Normal || storage == StorageType.Dictionary || storage == StorageType.Tuple)
                    {
                        propertyMap = new PropertyMap
                        {
                            Name = propertySymbol.Name,
                            TypeIndex = GetTypeIndex(typeDictionary, propertySymbol.Type),
                            Type = storage,
                            IsValueType = propertySymbol.Type.IsValueType,
                            Index = propertyMaps.Count
                        };
                    }
                    else if (storage == StorageType.Nullable)
                    {
                        var namedType = propertySymbol.Type as INamedTypeSymbol;
                        var type = namedType.TypeArguments[0];
                        propertyMap = new PropertyMap
                        {
                            Name = propertySymbol.Name,
                            TypeIndex = GetTypeIndex(typeDictionary, type),
                            Type = storage,
                            IsValueType = type.IsValueType,
                            Index = propertyMaps.Count
                        };
                    }
                    else if (storage == StorageType.Array)
                    {
                        var arrayType = propertySymbol.Type as IArrayTypeSymbol;
                        var type = arrayType.ElementType;
                        propertyMap = new PropertyMap
                        {
                            Name = propertySymbol.Name,
                            TypeIndex = GetTypeIndex(typeDictionary, type),
                            Type = storage,
                            IsValueType = type.IsValueType,
                            Index = propertyMaps.Count
                        };
                    }
                    else //if (storage == StorageType.List)
                    {
                        var namedType = propertySymbol.Type as INamedTypeSymbol;
                        var type = namedType.TypeArguments[0];
                        propertyMap = new PropertyMap
                        {
                            Name = propertySymbol.Name,
                            TypeIndex = GetTypeIndex(typeDictionary, type),
                            Type = storage,
                            IsValueType = type.IsValueType,
                            Index = propertyMaps.Count
                        };
                    }
                    propertyMaps.Add(propertyMap);
                }
                typeMaps.Add(typeSymbolName, propertyMaps);
            }

            return typeMaps;
        }

        private int GetTypeIndex(Dictionary<string, TypeMap> typeDictionary, ITypeSymbol typeSymbol)
        {
            string typeName = GetTypeName(typeSymbol);

            if (typeDictionary.ContainsKey(typeName))
            {
                return typeDictionary[typeName].Index;
            }

            int index = typeDictionary.Count;
            typeDictionary.Add(typeName, new TypeMap { Index = index, IsValueType = !typeSymbol.IsReferenceType });

            return index;
        }

        private StorageType GetTypeStorageType(ITypeSymbol typeSymbol)
        {
            if (_storageTypeCache.TryGetValue(typeSymbol, out var cached))
                return cached;

            var result = ComputeStorageType(typeSymbol);
            _storageTypeCache[typeSymbol] = result;
            return result;
        }

        private static StorageType ComputeStorageType(ITypeSymbol typeSymbol)
        {
            if (typeSymbol is IArrayTypeSymbol)
            {
                return StorageType.Array;
            }
            else if (IsListType(typeSymbol))
            {
                return StorageType.List;
            }
            else if (IsDictionaryType(typeSymbol))
            {
                return StorageType.Dictionary;
            }
            else if (typeSymbol is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                return StorageType.Nullable;
            }
            else if (typeSymbol.IsTupleType)
            {
                return StorageType.Tuple;
            }
            else
            {
                return StorageType.Normal;
            }
        }

        private static bool IsListType(ITypeSymbol typeSymbol)
        {
            if (typeSymbol is INamedTypeSymbol namedTypeSymbol)
            {
                return namedTypeSymbol.AllInterfaces.Any(x => x.ConstructedFrom.SpecialType == SpecialType.System_Collections_Generic_IList_T);
            }

            return false;
        }

        private static bool IsDictionaryType(ITypeSymbol typeSymbol)
        {
            if (typeSymbol is INamedTypeSymbol namedTypeSymbol)
            {
                const string expectedNamespace = "System.Collections.Generic";
                return namedTypeSymbol.Name == "Dictionary" && namedTypeSymbol.ContainingNamespace?.ToString() == expectedNamespace;
            }

            return false;
        }

        private static bool IsDictionaryType(ITypeSymbol typeSymbol, out ITypeSymbol keyTypeSymbol, out ITypeSymbol valueTypeSymbol)
        {
            keyTypeSymbol = null;
            valueTypeSymbol = null;

            if (typeSymbol is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.Name == "Dictionary" && namedTypeSymbol.ContainingNamespace?.ToString() == "System.Collections.Generic")
            {
                // Retrieve the type arguments of the Dictionary
                var typeArguments = namedTypeSymbol.TypeArguments;

                if (typeArguments.Length == 2)
                {
                    keyTypeSymbol = typeArguments[0];
                    valueTypeSymbol = typeArguments[1];

                    return true;
                }
            }

            return false;
        }

        private static string GetFullTypeName(ITypeSymbol typeSymbol)
        {
            return QualifyTypeName(typeSymbol);
        }

        /// <summary>Fully qualifies a type name, including containing types for nested classes.</summary>
        private static string QualifyTypeName(ITypeSymbol typeSymbol)
        {
            string typeName = typeSymbol.Name;

            INamedTypeSymbol containingType = typeSymbol.ContainingType;
            while (containingType != null)
            {
                typeName = $"{containingType.Name}.{typeName}";
                containingType = containingType.ContainingType;
            }

            INamespaceSymbol namespaceSymbol = typeSymbol.ContainingNamespace;
            while (namespaceSymbol != null && !namespaceSymbol.IsGlobalNamespace)
            {
                typeName = $"{namespaceSymbol.Name}.{typeName}";
                namespaceSymbol = namespaceSymbol.ContainingNamespace;
            }

            return typeName;
        }

        public static string DecodeTypeName(string encodedType)
        {
            if (encodedType.StartsWith("list_"))
            {
                var elementType = DecodeTypeName(encodedType.Substring("list_".Length));
                return $"List<{elementType}>";
            }

            if (encodedType.StartsWith("array_"))
            {
                var elementType = DecodeTypeName(encodedType.Substring("array_".Length));
                return $"{elementType}[]";
            }

            if (encodedType.StartsWith("null_"))
            {
                var elementType = DecodeTypeName(encodedType.Substring("null_".Length));
                return $"System.Nullable<{elementType}>";
            }

            if (encodedType.StartsWith("enum_"))
            {
                return GetEnumName(encodedType);
            }

            if (encodedType.StartsWith("[") && encodedType.EndsWith("]"))
            {
                var parsed = ParseBracketLevels(encodedType);

                if (parsed.Item2 == '-')
                {
                    var keyType = DecodeTypeName(parsed.Item1[0]);
                    var valueType = DecodeTypeName(parsed.Item1[1]);
                    return $"Dictionary<{keyType}, {valueType}>";
                }
                else if (parsed.Item2 == '~')
                {
                    return $"ValueTuple<{string.Join(", ", parsed.Item1.Select(DecodeTypeName))}>";
                }
                else
                {
                    throw new Exception("Unexpected split \"" + parsed.Item2 + "\" for type: " + encodedType);
                }
            }

            if (encodedType.StartsWith("[") && encodedType.EndsWith("]") && encodedType.Contains("~"))
            {
                var typeNames = encodedType.Substring(1, encodedType.Length - 2).Split('~');
                var tupleType = $"ValueTuple<{string.Join(", ", typeNames.Select(DecodeTypeName))}>";
                return tupleType;
            }

            if (encodedType == "date")
            {
                return typeof(DateTime).FullName;
            }
            else if (encodedType == "guid")
            {
                return typeof(Guid).FullName;
            }

            return encodedType;
        }

        public static (List<string>, char) ParseBracketLevels(string input)
        {
            int topLevelStart = 0;
            int openBrackets = 0;

            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == '[')
                {
                    openBrackets++;
                    if (openBrackets == 1)
                    {
                        topLevelStart = i + 1;
                    }
                }
                else if (input[i] == ']')
                {
                    openBrackets--;
                }
                else if ((input[i] == '-' || input[i] == '~') && openBrackets == 1)
                {
                    if (input[i] == '-')
                    {
                        return (new List<string>() { input.Substring(topLevelStart, i - topLevelStart), input.Substring(i + 1, input.Length - (i + 2)) }, '-');
                    }
                    else //tuple
                    {
                        var res = new List<string>();

                        res.Add(input.Substring(topLevelStart, i - topLevelStart));
                        topLevelStart = i + 1;

                        i++;

                        for (; i < input.Length; i++)
                        {
                            if (input[i] == ']' && openBrackets == 1)
                            {
                                res.Add(input.Substring(topLevelStart, i - topLevelStart));
                                topLevelStart = i + 1;
                                break;
                            }
                            else if (input[i] == '[')
                            {
                                openBrackets++;
                            }
                            else if (input[i] == ']')
                            {
                                openBrackets--;
                            }
                            else if (input[i] == '~' && openBrackets == 1)
                            {
                                res.Add(input.Substring(topLevelStart, i - topLevelStart));
                                topLevelStart = i + 1;
                            }
                        }
                        return (res, '~');
                    }
                }
            }

            return (null, ' ');
        }


        private string GetTypeName(ITypeSymbol typeSymbol)
        {
            if (typeSymbol == null)
                return string.Empty;

            if (_typeNameCache.TryGetValue(typeSymbol, out var cached))
                return cached;

            var name = ComputeTypeName(typeSymbol);
            _typeNameCache[typeSymbol] = name;
            return name;
        }

        private string ComputeTypeName(ITypeSymbol typeSymbol)
        {
            var storageType = GetTypeStorageType(typeSymbol);

            //if (typeSymbol is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsGenericType &&
            //    namedTypeSymbol.TypeArguments.Length == 2 && namedTypeSymbol.Name == "Dictionary")
            if (storageType == StorageType.Dictionary)
            {
                var namedTypeSymbol = typeSymbol as INamedTypeSymbol;
                string keyTypeName = GetTypeName(namedTypeSymbol.TypeArguments[0]);
                string valueTypeName = GetTypeName(namedTypeSymbol.TypeArguments[1]);

                return $"[{keyTypeName}-{valueTypeName}]";
            }
            if (storageType == StorageType.Tuple)
            {
                return "[" + string.Join("~", (typeSymbol as INamedTypeSymbol).TupleElements.Select(x => GetTypeName(x.Type))) + "]";
            }
            if (storageType == StorageType.List)
            {
                typeSymbol = (typeSymbol as INamedTypeSymbol).TypeArguments[0];
                return "list_" + GetTypeName(typeSymbol);
            }
            if (storageType == StorageType.Array)
            {
                typeSymbol = (typeSymbol as IArrayTypeSymbol).ElementType;
                return "array_" + GetTypeName(typeSymbol);
            }
            if (storageType == StorageType.Nullable)
            {
                typeSymbol = (typeSymbol as INamedTypeSymbol).TypeArguments[0];
                return "null_" + GetTypeName(typeSymbol);
            }

            return GetShortName(typeSymbol, QualifyTypeName(typeSymbol));
        }

        public static string GetShortName(ITypeSymbol typeSymbol, string assumed)
        {
            switch (typeSymbol.SpecialType)
            {
                case SpecialType.System_Boolean:
                    return "bool";
                case SpecialType.System_Char:
                    return "char";
                case SpecialType.System_SByte:
                    return "sbyte";
                case SpecialType.System_Byte:
                    return "byte";
                case SpecialType.System_Int16:
                    return "short";
                case SpecialType.System_UInt16:
                    return "ushort";
                case SpecialType.System_Int32:
                    return "int";
                case SpecialType.System_UInt32:
                    return "uint";
                case SpecialType.System_Int64:
                    return "long";
                case SpecialType.System_UInt64:
                    return "ulong";
                case SpecialType.System_Single:
                    return "float";
                case SpecialType.System_Double:
                    return "double";
                case SpecialType.System_Decimal:
                    return "decimal";
                case SpecialType.System_String:
                    return "string";
                case SpecialType.System_Object:
                    return "object";
                case SpecialType.System_DateTime:
                    return "date";
                default:
                    if (typeSymbol.TypeKind == TypeKind.Enum)
                    {
                        //the key carries the underlying primitive ("enum_byte_Ns.Color") so serializers
                        //and the handshake encode the enum at its native size instead of int32
                        var underlying = (typeSymbol as INamedTypeSymbol)?.EnumUnderlyingType;
                        var underlyingName = underlying == null ? "int" : GetShortName(underlying, "int");
                        return "enum_" + underlyingName + "_" + assumed;
                    }
                    if (typeSymbol.ToDisplayString() == "System.Guid")
                        return "guid";
                    return assumed;
            }
        }

        public static bool IsTypeSymbolValid(ITypeSymbol typeSymbol)
        {
            // Conditions for filtering
            return !typeSymbol.IsValueType &&
                   typeSymbol.ToDisplayString() != "System.Guid" &&
                   !typeSymbol.SpecialType.Equals(SpecialType.System_DateTime) &&
                   !IsByteArray(typeSymbol);
        }

        public static bool IsByteArray(ITypeSymbol typeSymbol)
        {
            // Check if the type symbol is an array with byte element type
            if (typeSymbol is IArrayTypeSymbol arrayTypeSymbol)
            {
                return arrayTypeSymbol.ElementType.SpecialType.Equals(SpecialType.System_Byte);
            }

            return false;
        }

        bool IsDerivedFromRpcClient(ITypeSymbol typeSymbol, Compilation compilation)
        {
            INamedTypeSymbol rpcClientSymbol = compilation.GetTypeByMetadataName("LiteLink.Shared.RpcClient");
            return typeSymbol != null && InheritsFrom(typeSymbol, rpcClientSymbol);
        }

        public bool IsTypeEqual(ITypeSymbol first, ITypeSymbol second)
        {
            var comparer = SymbolEqualityComparer.Default;
            return comparer.Equals(first, second);
        }

        //true when the type is, or anywhere contains, an open generic parameter (T, List<T>, T[], ...)
        static bool ContainsTypeParameter(ITypeSymbol type)
        {
            if (type is ITypeParameterSymbol)
                return true;
            if (type is IArrayTypeSymbol array)
                return ContainsTypeParameter(array.ElementType);
            if (type is INamedTypeSymbol named)
                return named.TypeArguments.Any(ContainsTypeParameter);
            return false;
        }

        // Extension method to check if a type symbol inherits from another type symbol
        public bool InheritsFrom(ITypeSymbol derivedType, ITypeSymbol baseType)
        {
            var comparer = SymbolEqualityComparer.Default;

            while (derivedType != null)
            {
                if (comparer.Equals(derivedType, baseType))
                {
                    return true;
                }

                derivedType = derivedType.BaseType;
            }

            return false;
        }

        public IEnumerable<IPropertySymbol> GetAllPropertiesWithBaseTypes(ITypeSymbol typeSymbol)
        {
            if (_propertiesCache.TryGetValue(typeSymbol, out var cachedProperties))
                return cachedProperties;

            var properties = new List<IPropertySymbol>();
            _propertiesCache[typeSymbol] = properties;
            var currentType = typeSymbol;

            while (currentType != null)
            {
                properties.AddRange(currentType.GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(p => p.SetMethod != null && !p.IsIndexer));

                currentType = currentType.BaseType;
                // Skip system types or other unwanted types in the inheritance chain
                if (currentType != null && currentType.ContainingNamespace != null) {
                    if (currentType.ContainingNamespace.ToDisplayString().StartsWith("System"))
                    {
                        break;
                    }
                }
            }

            return properties;
        }

        /// <summary>underlying primitive short name of an "enum_&lt;underlying&gt;_&lt;name&gt;" key, e.g. "byte"</summary>
        public static string GetEnumUnderlying(string encodedType)
        {
            var rest = encodedType.Substring("enum_".Length);
            return rest.Substring(0, rest.IndexOf('_'));
        }

        /// <summary>C# type name of an "enum_&lt;underlying&gt;_&lt;name&gt;" key</summary>
        public static string GetEnumName(string encodedType)
        {
            var rest = encodedType.Substring("enum_".Length);
            return rest.Substring(rest.IndexOf('_') + 1);
        }

        public static string GetTrueName(string parameterType)
        {
            if (parameterType.StartsWith("["))
                parameterType = parameterType.Substring(1, parameterType.Length - 2);
            if (parameterType == "date")
            {
                return typeof(DateTime).FullName;
            }
            else if (parameterType == "guid")
            {
                return typeof(Guid).FullName;
            }
            else if (parameterType.Contains("~"))
            {
                //tuples first: a tuple whose first element is an enum also starts with "enum_"
                return parameterType.Replace("~", "__").Replace(".", "_").Replace("[", "___").Replace("]", "___");
            }
            else if (parameterType.StartsWith("enum_"))
            {
                return GetEnumName(parameterType);
            }
            return parameterType.Replace("[", "___").Replace("]", "___");
        }

        static readonly ulong[] bitmasks = new ulong[]
       {
            0x0000000000000001,
            0x0000000000000002,
            0x0000000000000004,
            0x0000000000000008,
            0x0000000000000010,
            0x0000000000000020,
            0x0000000000000040,
            0x0000000000000080,
            0x0000000000000100,
            0x0000000000000200,
            0x0000000000000400,
            0x0000000000000800,
            0x0000000000001000,
            0x0000000000002000,
            0x0000000000004000,
            0x0000000000008000,
            0x0000000000010000,
            0x0000000000020000,
            0x0000000000040000,
            0x0000000000080000,
            0x0000000000100000,
            0x0000000000200000,
            0x0000000000400000,
            0x0000000000800000,
            0x0000000001000000,
            0x0000000002000000,
            0x0000000004000000,
            0x0000000008000000,
            0x0000000010000000,
            0x0000000020000000,
            0x0000000040000000,
            0x0000000080000000,
            0x0000000100000000,
            0x0000000200000000,
            0x0000000400000000,
            0x0000000800000000,
            0x0000001000000000,
            0x0000002000000000,
            0x0000004000000000,
            0x0000008000000000,
            0x0000010000000000,
            0x0000020000000000,
            0x0000040000000000,
            0x0000080000000000,
            0x0000100000000000,
            0x0000200000000000,
            0x0000400000000000,
            0x0000800000000000,
            0x0001000000000000,
            0x0002000000000000,
            0x0004000000000000,
            0x0008000000000000,
            0x0010000000000000,
            0x0020000000000000,
            0x0040000000000000,
            0x0080000000000000,
            0x0100000000000000,
            0x0200000000000000,
            0x0400000000000000,
            0x0800000000000000,
            0x1000000000000000,
            0x2000000000000000,
            0x4000000000000000,
            0x8000000000000000
       };
    }
}
