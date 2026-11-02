//using LiteLink.Shared.Serializer;
//using System;
//using System.Collections.Generic;
//using System.Net.Sockets;
//using System.Reflection.Emit;
//using System.Text;

//namespace LiteLink.Shared
//{
//    internal class ProcedureHandler
//    {
//        internal static Func<RPCClient, byte[], Task<byte[]>>[] Procedures { get; set; } = new Func<RPCClient, byte[], Task<byte[]>>[ushort.MaxValue];

//        internal delegate Task ProcedureDelegateNoArgNoResultAsync<TClient>(TClient client);
//        internal delegate Task<TResult> ProcedureDelegateNoArgAsync<TClient, TResult>(TClient client);
//        internal delegate void ProcedureDelegateNoArgNoResult<TClient>(TClient client);
//        internal delegate TResult ProcedureDelegateNoArg<TClient, TResult>(TClient client);
//        internal delegate Task<TResult> ProcedureDelegateAsync<TClient, T, TResult>(TClient client, T arg);
//        internal delegate TResult ProcedureDelegate<TClient, T, TResult>(TClient client, T arg);
//        internal delegate void ProcedureDelegateNoResult<TClient, T>(TClient client, T arg);
//        internal delegate Task ProcedureDelegateNoResultAsync<TClient, T>(TClient client, T arg);

//        internal static void RegisterProcedure<TClient>(ushort opcode, ProcedureDelegateNoArgNoResultAsync<TClient> func) where TClient : RPCClient
//        {
//            ConfirmEmpty(opcode);
//            //ModelMappings.CreateFunctionMapping(opcode, null, null);
//            Procedures[(int)opcode] = async (c, data) =>
//            {
//                await func(c as TClient);
//                return null;
//            };
//        }

//        internal static void RegisterProcedure<TClient>(ushort opcode, ProcedureDelegateNoArgNoResult<TClient> func) where TClient : RPCClient
//        {
//            ConfirmEmpty(opcode);
//            //ModelMappings.CreateFunctionMapping(opcode, null, null);
//            Procedures[(int)opcode] = async (c, data) =>
//            {
//                func(c as TClient);
//                return null;
//            };
//        }

//        internal static void RegisterProcedure<TClient, TResult>(ushort opcode, ProcedureDelegateNoArg<TClient, TResult> func) where TClient : RPCClient
//        {
//            ConfirmEmpty(opcode);
//            //ModelMappings.CreateMapping(typeof(TResult));
//            //ModelMappings.CreateFunctionMapping(opcode, null, typeof(TResult));
//            Procedures[(int)opcode] = async (c, data) =>
//            {
//                var res = func(c as TClient);
//                return LiteSerializer.Serialize(res);
//            };
//        }

//        internal static void RegisterProcedure<TClient, TResult>(ushort opcode, ProcedureDelegateNoArgAsync<TClient, TResult> func) where TClient : RPCClient
//        {
//            ConfirmEmpty(opcode);
//            //ModelMappings.CreateMapping(typeof(TResult));
//            //ModelMappings.CreateFunctionMapping(opcode, null, typeof(TResult));
//            Procedures[(int)opcode] = async (c, data) =>
//            {
//                var res = await func(c as TClient);
//                return LiteSerializer.Serialize(res);
//            };
//        }

//        internal static void RegisterProcedure<TClient, T, TResult>(ushort opcode, ProcedureDelegate<TClient, T, TResult> func) where TClient : RPCClient
//        {
//            ConfirmEmpty(opcode);
//           // ModelMappings.CreateMapping(typeof(T));
//            //ModelMappings.CreateMapping(typeof(TResult));
//            //ModelMappings.CreateFunctionMapping(opcode, typeof(T), typeof(TResult));
//            Procedures[(int)opcode] = async (c, data) =>
//            {
//                var msg = LiteSerializer.Deserialize<T>(data);
//                var res = func(c as TClient, msg);
//                return LiteSerializer.Serialize(res);
//            };
//        }

//        internal static void RegisterProcedure<TClient, T>(ushort opcode, ProcedureDelegateNoResult<TClient, T> func) where TClient : RPCClient
//        {
//            ConfirmEmpty(opcode);
//            //ModelMappings.CreateMapping(typeof(T));
//            //ModelMappings.CreateFunctionMapping(opcode, typeof(T), null);
//            Procedures[(int)opcode] = async (c, data) =>
//            {
//                var msg = LiteSerializer.Deserialize<T>(data);
//                func(c as TClient, msg);
//                return null;
//            };
//        }

//        internal static void RegisterProcedure<TClient, T, TResult>(ushort opcode, ProcedureDelegateAsync<TClient, T, TResult> func) where TClient : RPCClient
//        {
//            ConfirmEmpty(opcode);
//            //ModelMappings.CreateMapping(typeof(T));
//            //ModelMappings.CreateMapping(typeof(TResult));
//            //ModelMappings.CreateFunctionMapping(opcode, typeof(T), typeof(TResult));
//            Procedures[(int)opcode] = async (c, data) =>
//            {
//                var msg = LiteSerializer.Deserialize<T>(data);
//                var res = await func(c as TClient, msg);
//                return LiteSerializer.Serialize(res);
//            };
//        }

//        internal static void RegisterProcedure<TClient, T>(ushort opcode, ProcedureDelegateNoResultAsync<TClient, T> func) where TClient : RPCClient
//        {
//            ConfirmEmpty(opcode);
//            //ModelMappings.CreateMapping(typeof(T));
//            //ModelMappings.CreateFunctionMapping(opcode, typeof(T), null);
//            Procedures[(int)opcode] = async (c, data) =>
//            {
//                var msg = LiteSerializer.Deserialize<T>(data);
//                await func(c as TClient, msg);
//                return null;
//            };
//        }

//        private static void ConfirmEmpty(ushort opcode)
//        {
//            if (Procedures[(int)opcode] != null)
//                throw new Exception(string.Format("Opcode {0} already registered", opcode));
//        }
//    }
//}
