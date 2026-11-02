using LiteLink.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace LiteLink.Generator.Tests
{
    /// <summary>
    /// Drives the RpcGenerator through a CSharpGeneratorDriver with step tracking to verify
    /// both directions of the incremental pipeline:
    /// - seed nodes are served from cache when an unrelated file changes (no wasted work), and
    /// - a DTO edit in a file with no seeds still regenerates the output (no stale cache).
    /// The LiteLink attributes/RpcLink are defined in-source as stand-ins; the generator matches
    /// them by metadata name so no reference to the real LiteLink assemblies is needed.
    /// </summary>
    public class RpcGeneratorIncrementalTests
    {
        private const string StubsSource = """
            namespace LiteLink
            {
                public class RpcFuncAttribute : System.Attribute
                {
                    public RpcFuncAttribute() { }
                    public RpcFuncAttribute(string name) { }
                    public string[] PermissionGroups { get; set; }
                }
                public class RpcGroupAttribute : System.Attribute
                {
                    public RpcGroupAttribute() { }
                    public RpcGroupAttribute(string name) { }
                }
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public class RpcServerAttribute<TLink> : System.Attribute
                {
                    public bool UseCompression { get; set; }
                    public System.Type PermissionGroups { get; set; }
                }
                public class RpcLink
                {
                    public void Invoke<T>(string function, T model) { }
                }
            }
            """;

        private const string RpcSurfaceSource = """
            namespace TestRpc
            {
                using LiteLink;

                [RpcServer<RpcLink>]
                public class Server { }

                [RpcGroup("Foo")]
                public static class FooProcedures
                {
                    [RpcFunc("DoThing")]
                    public static FooDto DoThing(RpcLink link, FooDto model)
                    {
                        link.Invoke("Pushed", new PushDto { Value = 1 });
                        return model;
                    }
                }
            }
            """;

        private const string DtoSource = """
            namespace TestRpc
            {
                public class FooDto
                {
                    public int Id { get; set; }
                    public string Name { get; set; }
                }

                public class PushDto
                {
                    public int Value { get; set; }
                }
            }
            """;

        private const string UnrelatedSource = """
            namespace Other
            {
                public class Unrelated
                {
                    public int X { get; set; }
                }
            }
            """;

        private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.CSharp11);

        private static readonly string[] SeedStepNames = { "LiteLinkFuncSeeds", "LiteLinkServerSeeds", "LiteLinkInvokeSeeds" };

        [Fact]
        public void GeneratesSerializersForRpcSurface()
        {
            var (compilation, _) = CreateCompilation();
            var driver = CreateDriver().RunGenerators(compilation);

            var result = Assert.Single(driver.GetRunResult().Results);
            Assert.Null(result.Exception);

            var source = Assert.Single(result.GeneratedSources).SourceText.ToString();

            //procedure argument/return type
            Assert.Contains("WriteTestRpc_FooDto", source);
            //type pushed through RpcLink.Invoke<T>
            Assert.Contains("WriteTestRpc_PushDto", source);
        }

        [Fact]
        public void UnrelatedEdit_ServesSeedsFromCache_AndKeepsOutputStable()
        {
            var (compilation, trees) = CreateCompilation();
            var driver = CreateDriver().RunGenerators(compilation);
            var firstOutput = Assert.Single(driver.GetRunResult().Results[0].GeneratedSources).SourceText.ToString();

            //edit only the file with no RPC surface
            var edited = CSharpSyntaxTree.ParseText(UnrelatedSource + "\n//edited", ParseOptions, path: "unrelated.cs");
            driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(trees["unrelated.cs"], edited));

            var result = driver.GetRunResult().Results[0];
            var secondOutput = Assert.Single(result.GeneratedSources).SourceText.ToString();

            Assert.Equal(firstOutput, secondOutput);

            foreach (var stepName in SeedStepNames)
            {
                Assert.True(result.TrackedSteps.TryGetValue(stepName, out var steps), $"step {stepName} was not tracked");

                foreach (var output in steps.SelectMany(step => step.Outputs))
                {
                    Assert.True(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                        $"step {stepName} re-ran with reason {output.Reason}");
                }
            }
        }

        [Fact]
        public void DtoEditInUnseededFile_IsReflectedInOutput()
        {
            var (compilation, trees) = CreateCompilation();
            var driver = CreateDriver().RunGenerators(compilation);
            var firstOutput = Assert.Single(driver.GetRunResult().Results[0].GeneratedSources).SourceText.ToString();

            Assert.DoesNotContain("Extra", firstOutput);

            //dto.cs contains no attributes and no Invoke calls, so no seed node watches it,
            //the output stage must still pick up the new property from the fresh compilation
            var edited = CSharpSyntaxTree.ParseText(
                DtoSource.Replace("public int Id { get; set; }", "public int Id { get; set; }\npublic bool Extra { get; set; }"),
                ParseOptions, path: "dto.cs");
            driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(trees["dto.cs"], edited));

            var secondOutput = Assert.Single(driver.GetRunResult().Results[0].GeneratedSources).SourceText.ToString();

            Assert.NotEqual(firstOutput, secondOutput);
            Assert.Contains("Extra", secondOutput);
        }

        [Fact]
        public void DuplicateFunction_ReportsRpcgen004_AtTheDuplicateMethod()
        {
            const string duplicateSource = """
                namespace TestRpc
                {
                    using LiteLink;

                    [RpcGroup("Foo")]
                    public static class MoreProcedures
                    {
                        [RpcFunc("DoThing")]
                        public static void DoThingAgain(RpcLink link)
                        {
                        }
                    }
                }
                """;

            //trees process in file path order and the diagnostic lands on the second-seen declaration,
            //so the file must sort after rpc.cs for DoThingAgain to be the reported duplicate
            var (compilation, _) = CreateCompilation(("zz_duplicate.cs", duplicateSource));
            var driver = CreateDriver().RunGenerators(compilation);
            var result = driver.GetRunResult().Results[0];

            var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "RPCGEN004");

            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            //anchored to the duplicate method's identifier, not Location.None
            Assert.Equal("zz_duplicate.cs", diagnostic.Location.SourceTree?.FilePath);
            Assert.Contains("DoThingAgain", diagnostic.Location.SourceTree?.GetText().ToString(diagnostic.Location.SourceSpan));

            //the duplicate is skipped, the rest of the surface still generates without an internal error
            Assert.Null(result.Exception);
            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "RPCGEN016");
            Assert.Single(result.GeneratedSources);
        }

        [Fact]
        public void UndeclaredPermission_ReportsRpcgen011_WithoutAbortingGeneration()
        {
            const string permissionSource = """
                namespace TestRpc
                {
                    using LiteLink;

                    [RpcGroup("Bar")]
                    public static class BarProcedures
                    {
                        [RpcFunc("Guarded", PermissionGroups = new[] { "NotDeclared" })]
                        public static void Guarded(RpcLink link)
                        {
                        }
                    }
                }
                """;

            var (compilation, _) = CreateCompilation(("permission.cs", permissionSource));
            var driver = CreateDriver().RunGenerators(compilation);
            var result = driver.GetRunResult().Results[0];

            var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "RPCGEN011");

            Assert.Equal("permission.cs", diagnostic.Location.SourceTree?.FilePath);
            //the undeclared permission is reported but must not crash the run or suppress the output
            Assert.Contains("NotDeclared", diagnostic.GetMessage());
            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "RPCGEN016");
            Assert.Single(result.GeneratedSources);
        }

        [Fact]
        public void Handshake_LeavesCompressedFlagToClose_EvenWithUseCompression()
        {
            //a small API's handshake stays under LiteWriter's compression threshold and goes out
            //uncompressed; the flag must therefore be set by Close(2) when it actually compresses,
            //never pre-written from UseCompression, or clients try to decompress plain bytes
            var surface = RpcSurfaceSource.Replace("[RpcServer<RpcLink>]", "[RpcServer<RpcLink>(UseCompression = true)]");
            var (compilation, _) = CreateCompilation(("rpc.cs", surface));
            var driver = CreateDriver().RunGenerators(compilation);

            var result = Assert.Single(driver.GetRunResult().Results);
            Assert.Null(result.Exception);
            var source = Assert.Single(result.GeneratedSources).SourceText.ToString();

            Assert.Contains("UseCompression", surface);
            Assert.Matches(@"new LiteWriter\(\);\s*writer\.WriteByte\(0\);\s*writer\.WriteByte\(0\);", source);
            Assert.Contains("HandshakeResponse = writer.Close(2);", source);
        }

        private static (CSharpCompilation Compilation, Dictionary<string, SyntaxTree> Trees) CreateCompilation(params (string Path, string Source)[] extraFiles)
        {
            var trees = new Dictionary<string, SyntaxTree>
            {
                ["stubs.cs"] = CSharpSyntaxTree.ParseText(StubsSource, ParseOptions, path: "stubs.cs"),
                ["rpc.cs"] = CSharpSyntaxTree.ParseText(RpcSurfaceSource, ParseOptions, path: "rpc.cs"),
                ["dto.cs"] = CSharpSyntaxTree.ParseText(DtoSource, ParseOptions, path: "dto.cs"),
                ["unrelated.cs"] = CSharpSyntaxTree.ParseText(UnrelatedSource, ParseOptions, path: "unrelated.cs"),
            };

            foreach (var (path, source) in extraFiles)
            {
                trees[path] = CSharpSyntaxTree.ParseText(source, ParseOptions, path: path);
            }

            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToList();

            var compilation = CSharpCompilation.Create("GeneratorTestAssembly",
                trees.Values,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            return (compilation, trees);
        }

        private static GeneratorDriver CreateDriver()
        {
            return CSharpGeneratorDriver.Create(
                generators: new[] { new RpcGenerator().AsSourceGenerator() },
                parseOptions: ParseOptions,
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        }
    }
}
