using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using LiteLink.Shared.Serializer;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace LiteLink.SignalR
{
    internal class LiteLinkHubProtocol : IHubProtocol
    {
        private const byte ErrorResult = 1;
        private const byte VoidResult = 2;
        private const byte NonVoidResult = 3;

        private const string ProtocolName = "litelink";
        private const int ProtocolVersion = 1;

        public string Name => ProtocolName;
        public int Version => ProtocolVersion;

        public TransferFormat TransferFormat => TransferFormat.Binary;

        public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message)
        {
            var writer = new LiteWriter();

            //place holder for message length
            writer.WriteUInt32(0);

            WriteMessageCore(message, ref writer);

            var arr = writer.Close();
            writer = new LiteWriter(arr);
            writer.WriteUInt32((uint)arr.Length - 4); //-4, because that was placeholder for length

            return arr;
        }

        private void WriteMessageCore(HubMessage message, ref LiteWriter writer)
        {
            switch (message)
            {
                case InvocationMessage invocationMessage:
                    WriteInvocationMessage(invocationMessage, ref writer);
                    break;
                case StreamInvocationMessage streamInvocationMessage:
                    WriteStreamInvocationMessage(streamInvocationMessage, ref writer);
                    break;
                case StreamItemMessage streamItemMessage:
                    WriteStreamingItemMessage(streamItemMessage, ref writer);
                    break;
                case CompletionMessage completionMessage:
                    WriteCompletionMessage(completionMessage, ref writer);
                    break;
                case CancelInvocationMessage cancelInvocationMessage:
                    WriteCancelInvocationMessage(cancelInvocationMessage, ref writer);
                    break;
                case PingMessage:
                    WritePingMessage(ref writer);
                    break;
                case CloseMessage closeMessage:
                    WriteCloseMessage(closeMessage, ref writer);
                    break;
                default:
                    throw new InvalidDataException($"Unexpected message type: {message.GetType().Name}");
            }
        }

        private void WriteArgument(object?[] arguments, ref LiteWriter writer)
        {
            if (arguments == null || arguments.Length != 1)
            {
                Console.Error.WriteLine("Invocation in Litelink's SignalR protocol must always have 1 invocation argument");
            }
            else
            {
                var argument = arguments[0];
                if (argument is RawResult result)
                {
                    writer.WriteByte(1);
                    writer.WriteByteArray(result.RawSerializedData.ToArray());
                }
                else if (argument is byte[] arr)
                {
                    writer.WriteByte(1);
                    writer.WriteByteArray(arr);
                }
                else throw new Exception("Unexpected type. SignalR should only be handling raw byte arrays, the rest should be handled by the LiteLink library.");
            }
        }

        private void WriteInvocationMessage(InvocationMessage message, ref LiteWriter writer)
        {
            writer.WriteByte(HubProtocolConstants.InvocationMessageType);
            PackHeaders(message.Headers, ref writer);
            if (string.IsNullOrEmpty(message.InvocationId))
            {
                writer.WriteString(null);
            }
            else
            {
                writer.WriteString(message.InvocationId);
            }

            WriteArgument(message.Arguments, ref writer);
            WriteStreamIds(message.StreamIds, ref writer);
        }

        private void WriteStreamInvocationMessage(StreamInvocationMessage message, ref LiteWriter writer)
        {
            writer.WriteByte(HubProtocolConstants.StreamInvocationMessageType);
            PackHeaders(message.Headers, ref writer);
            writer.WriteString(message.InvocationId);

            WriteArgument(message.Arguments, ref writer);
            WriteStreamIds(message.StreamIds, ref writer);
        }

        private void WriteStreamingItemMessage(StreamItemMessage message, ref LiteWriter writer)
        {
            writer.WriteByte(HubProtocolConstants.StreamItemMessageType);
            PackHeaders(message.Headers, ref writer);
            writer.WriteString(message.InvocationId);
            WriteArgument(message.Item, ref writer);
        }

        private void WriteCompletionMessage(CompletionMessage message, ref LiteWriter writer)
        {
            var resultKind =
                message.Error != null ? ErrorResult :
                message.HasResult ? NonVoidResult :
                VoidResult;

            writer.WriteByte(HubProtocolConstants.CompletionMessageType);
            PackHeaders(message.Headers, ref writer);
            writer.WriteString(message.InvocationId);
            writer.WriteByte(resultKind);
            switch (resultKind)
            {
                case ErrorResult:
                    writer.WriteString(message.Error);
                    break;
                case NonVoidResult:
                    WriteArgument(message.Result, ref writer);
                    break;
            }
        }

        private void WriteCancelInvocationMessage(CancelInvocationMessage message, ref LiteWriter writer)
        {
            writer.WriteByte(HubProtocolConstants.CancelInvocationMessageType);
            PackHeaders(message.Headers, ref writer);
            writer.WriteString(message.InvocationId);
        }

        private void WriteCloseMessage(CloseMessage message, ref LiteWriter writer)
        {
            writer.WriteByte(HubProtocolConstants.CloseMessageType);
            if (string.IsNullOrEmpty(message.Error))
            {
                writer.WriteString(null);
            }
            else
            {
                writer.WriteString(message.Error);
            }

            writer.WriteBoolean(message.AllowReconnect);
        }

        private void WritePingMessage(ref LiteWriter writer)
        {
            writer.WriteByte(HubProtocolConstants.PingMessageType);
        }

        private void WriteArgument(object? argument, ref LiteWriter writer)
        {
            if (argument == null)
            {
                writer.WriteByte(0);
            }
            else if (argument is RawResult result)
            {
                writer.WriteByte(1);
                writer.WriteByteArray(result.RawSerializedData.ToArray());
            }
            else if (argument is byte[] arr)
            {
                writer.WriteByte(1);
                writer.WriteByteArray(arr);
            }
            else throw new Exception("Unexpected type. SignalR should only be handling raw byte arrays, the rest should be handled by the LiteLink library.");
        }

        private void WriteStreamIds(string[]? streamIds, ref LiteWriter writer)
        {
            if (streamIds != null)
            {
                writer.WriteLength((uint)streamIds.Length);
                foreach (var streamId in streamIds)
                {
                    writer.WriteString(streamId);
                }
            }
            else
            {
                writer.WriteLength(0);
            }
        }

        private void PackHeaders(IDictionary<string, string>? headers, ref LiteWriter writer)
        {
            if (headers != null)
            {
                writer.WriteLength((uint)headers.Count);
                if (headers.Count > 0)
                {
                    foreach (var header in headers)
                    {
                        writer.WriteString(header.Key);
                        writer.WriteString(header.Value);
                    }
                }
            }
            else
            {
                writer.WriteLength(0);
            }
        }

        public bool IsVersionSupported(int version)
        {
            return version == Version;
        }

        public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, [NotNullWhen(true)] out HubMessage? message)
        {            
            if(input.Length < 4)
            {
                //no data
                message = null;
                return false;
            }

            var lenarr = input.Slice(0, 4).ToArray();
            uint length = 0;

            unsafe {
                fixed (byte* arr = &lenarr[0])
                {
                    length = Unsafe.ReadUnaligned<uint>(arr);
                }
            }

            if (input.Length < length + 4)
            {
                //no data
                message = null;
                return false;
            }

            // Get the payload
            var res = input.Slice(4, (int)length).ToArray();

            // Skip the payload
            input = input.Slice(4 + (int)length);

            var reader = new LiteReader(res);
            message = ParseMessage(ref reader, binder);
            return message != null;
        }

        private HubMessage? ParseMessage(ref LiteReader reader, IInvocationBinder binder)
        {
            var messageType = reader.ReadByte();

            switch (messageType)
            {
                case HubProtocolConstants.InvocationMessageType:
                    return CreateInvocationMessage(ref reader, binder);
                case HubProtocolConstants.StreamInvocationMessageType:
                    return CreateStreamInvocationMessage(ref reader, binder);
                case HubProtocolConstants.StreamItemMessageType:
                    return CreateStreamItemMessage(ref reader, binder);
                case HubProtocolConstants.CompletionMessageType:
                    return CreateCompletionMessage(ref reader, binder);
                case HubProtocolConstants.CancelInvocationMessageType:
                    return CreateCancelInvocationMessage(ref reader);
                case HubProtocolConstants.PingMessageType:
                    return PingMessage.Instance;
                case HubProtocolConstants.CloseMessageType:
                    return CreateCloseMessage(ref reader);
                default:
                    // Future protocol changes can add message types, old clients can ignore them
                    return null;
            }
        }

        private HubMessage CreateInvocationMessage(ref LiteReader reader, IInvocationBinder binder)
        {
            var headers = ReadHeaders(ref reader);
            var invocationId = reader.ReadString();

            if (string.IsNullOrEmpty(invocationId))
            {
                invocationId = null;
            }

            const string target = "Data";

            object[]? arguments;
            try
            {
                var parameterTypes = new List<Type>() { typeof(byte[]) };
                arguments = BindArguments(ref reader, parameterTypes);
            }
            catch (Exception ex)
            {
                return new InvocationBindingFailureMessage(invocationId, target, ExceptionDispatchInfo.Capture(ex));
            }

            string[]? streams = ReadStreamIds(ref reader);            

            return ApplyHeaders(headers, new InvocationMessage(invocationId, target, arguments, streams));
        }

        private HubMessage CreateStreamInvocationMessage(ref LiteReader reader, IInvocationBinder binder)
        {
            var headers = ReadHeaders(ref reader);
            var invocationId = reader.ReadString();
            const string target = "Data";

            object[] arguments;
            try
            {
                var parameterTypes = new List<Type>() { typeof(byte[]) };
                arguments = BindArguments(ref reader, parameterTypes);
            }
            catch (Exception ex)
            {
                return new InvocationBindingFailureMessage(invocationId, target, ExceptionDispatchInfo.Capture(ex));
            }

            string[]? streams = ReadStreamIds(ref reader);            

            return ApplyHeaders(headers, new StreamInvocationMessage(invocationId, target, arguments, streams));
        }

        private HubMessage CreateStreamItemMessage(ref LiteReader reader, IInvocationBinder binder)
        {
            var headers = ReadHeaders(ref reader);
            var invocationId = reader.ReadString();
            object value;
            try
            {
                var itemType = binder.GetStreamItemType(invocationId);
                value = reader.ReadByteArray();
            }
            catch (Exception ex)
            {
                return new StreamBindingFailureMessage(invocationId, ExceptionDispatchInfo.Capture(ex));
            }

            return ApplyHeaders(headers, new StreamItemMessage(invocationId, value));
        }

        private CompletionMessage CreateCompletionMessage(ref LiteReader reader, IInvocationBinder binder)
        {
            var headers = ReadHeaders(ref reader);
            var invocationId = reader.ReadString();
            var resultKind = reader.ReadByte();

            string? error = null;
            object? result = null;
            var hasResult = false;

            switch (resultKind)
            {
                case ErrorResult:
                    error = reader.ReadString();
                    break;
                case NonVoidResult:
                    hasResult = true;
                    Type itemType = null;
                    try
                    {
                        itemType = binder.GetReturnType(invocationId);
                    }
                    catch //apparently it throws if it fails to get a type... but do we want to continue at this point
                    {

                    }

                    if (itemType is null)
                    {
                        //can this even happen?
                    }
                    else
                    {
                        if (itemType == typeof(RawResult))
                        {
                            result = new RawResult(new ReadOnlySequence<byte>(reader.ReadByteArray()));
                        }
                        else
                        {
                            try
                            {
                                if (reader.ReadByte() == 1)
                                    result = reader.ReadByteArray();
                            }
                            catch (Exception ex)
                            {
                                error = $"Error trying to deserialize result to {itemType.Name}. {ex.Message}";
                                hasResult = false;
                            }
                        }
                    }
                    break;
                case VoidResult:
                    hasResult = false;
                    break;
                default:
                    throw new InvalidDataException("Invalid invocation result kind.");
            }

            return ApplyHeaders(headers, new CompletionMessage(invocationId, error, result, hasResult));
        }

        private static CancelInvocationMessage CreateCancelInvocationMessage(ref LiteReader reader)
        {
            var headers = ReadHeaders(ref reader);
            var invocationId = reader.ReadString();
            return ApplyHeaders(headers, new CancelInvocationMessage(invocationId));
        }

        private static CloseMessage CreateCloseMessage(ref LiteReader reader)
        {
            var error = reader.ReadString();
            var allowReconnect = reader.ReadBoolean();            

            // An empty string is still an error
            if (error == null && !allowReconnect)
            {
                return CloseMessage.Empty;
            }

            return new CloseMessage(error, allowReconnect);
        }

        private static Dictionary<string, string>? ReadHeaders(ref LiteReader reader)
        {
            var headerCount = (int)reader.ReadLength();
            if (headerCount > 0)
            {
                var headers = new Dictionary<string, string>(StringComparer.Ordinal);

                for (var i = 0; i < headerCount; i++)
                {
                    var key = reader.ReadString();
                    var value = reader.ReadString();
                    headers.Add(key, value);
                }
                return headers;
            }
            else
            {
                return null;
            }
        }

        private static string[]? ReadStreamIds(ref LiteReader reader)
        {
            var streamIdCount = (int)reader.ReadLength();
            List<string>? streams = null;

            if (streamIdCount > 0)
            {
                streams = new List<string>();
                for (var i = 0; i < streamIdCount; i++)
                {
                    streams.Add(reader.ReadString());
                }
            }

            return streams?.ToArray();
        }

        private object[] BindArguments(ref LiteReader reader, IReadOnlyList<Type> parameterTypes)
        {
            var argumentCount = 1;

            if (parameterTypes.Count != argumentCount)
            {
                throw new InvalidDataException(
                    $"Invocation provides {argumentCount} argument(s) but target expects {parameterTypes.Count}.");
            }

            try
            {
                var arguments = new object[argumentCount];
                    if(reader.ReadByte() == 1)
                        arguments[0] = reader.ReadByteArray(); //we only support byte arrays
                
                return arguments;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Error binding arguments. Make sure that the types of the provided values match the types of the hub method being invoked.", ex);
            }
        }

        private static T ApplyHeaders<T>(IDictionary<string, string>? source, T destination) where T : HubInvocationMessage
        {
            if (source != null && source.Count > 0)
            {
                destination.Headers = source;
            }

            return destination;
        }

        public void WriteMessage(HubMessage message, IBufferWriter<byte> output)
        {            
            var writer = new LiteWriter();         

            //place holder for message length
            writer.WriteUInt32(0);

            WriteMessageCore(message, ref writer);

            var arr = writer.Close();
            writer = new LiteWriter(arr);
            writer.WriteUInt32((uint)arr.Length - 4); //-4, because that was placeholder for length

            output.Write(arr);  
        }
    }
}
