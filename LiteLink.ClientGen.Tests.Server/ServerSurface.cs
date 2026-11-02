using LiteLink;
using LiteLink.ClientGen.Tests.Models;

namespace LiteLink.ClientGen.Tests.Server
{
    public class CgLink : RpcLink
    {
    }

    /// <summary>
    /// The server half of the client-generation tests: a real generated server over the shared
    /// models, hosted in its own assembly so the client test assembly can stay a pure
    /// [RpcClientModels] client (one assembly can't be both).
    /// </summary>
    [RpcServer<CgLink>]
    [RpcGroup("Cg")]
    public static class CgProcedures
    {
        [RpcFunc("EchoModel")]
        public static CgModel EchoModel(CgLink link, CgModel model) => model;

        [RpcFunc("EchoChild")]
        public static CgChild EchoChild(CgLink link, CgChild child) => child;

        [RpcFunc("EchoListed")]
        public static CgListedOnly EchoListed(CgLink link, CgListedOnly model) => model;

        [RpcFunc("EchoInts")]
        public static List<int> EchoInts(CgLink link, List<int> numbers) => numbers;
    }

    public static class ServerHost
    {
        /// <summary>touching this forces the assembly to load, which runs the generated module
        /// initializer and creates the RpcServer singleton</summary>
        public static void EnsureLoaded()
        {
        }
    }
}
