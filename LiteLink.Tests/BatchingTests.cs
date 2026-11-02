extern alias client;

using System.Diagnostics;
using LiteLink.Shared.Serializer;
using Xunit;
using RpcBatchMode = client::LiteLink.Client.RpcBatchMode;

namespace LiteLink.Tests
{
    /// <summary>
    /// Request batching: the client packs concurrent calls into one transport message
    /// (system opcode 0 type 4), the server fans them back out and returns the responses
    /// in order. Covers both flush strategies and the server-side abuse limits.
    /// </summary>
    [Collection("RpcServer")]
    public class BatchingTests
    {
        private readonly RpcServerFixture _fixture;

        public BatchingTests(RpcServerFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task WindowMode_PacksConcurrentCallsIntoOneTransportMessage()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            rpc.BatchMode = RpcBatchMode.Window;
            connection.InvokeCount = 0;

            //all eight enqueue well within the 50 ms window, so exactly one transport send goes out
            var tasks = Enumerable.Range(0, 8).Select(i => rpc.Send<int?>("Test.EchoNullable", (int?)i)).ToArray();
            var results = await Task.WhenAll(tasks);

            Assert.Equal(Enumerable.Range(0, 8).Select(i => (int?)i).ToArray(), results);
            Assert.Equal(1, connection.InvokeCount);
        }

        [Fact]
        public async Task WindowMode_LoneCallGoesOutUnwrapped()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            rpc.BatchMode = RpcBatchMode.Window;
            connection.InvokeCount = 0;
            byte[] lastRequest = null;
            connection.RequestSpy = bytes => lastRequest = bytes;

            var res = await rpc.Send<int?>("Test.EchoNullable", (int?)7);

            Assert.Equal((int?)7, res);
            Assert.Equal(1, connection.InvokeCount);
            //a lone flush skips the batch envelope: a procedure request starts with its
            //non-zero opcode, a batch would start with the system opcode 0
            Assert.False(lastRequest[0] == 0 && lastRequest[1] == 0);
        }

        [Fact]
        public async Task CoalesceMode_FirstCallImmediate_CallsDuringFlightPackIntoOneBatch()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            rpc.BatchMode = RpcBatchMode.Coalesce;
            connection.InvokeCount = 0;

            //Slow (100 ms server-side) starts the pump and goes out alone with no added latency
            var slow = rpc.Send<int>("Test.Slow", null);
            //these arrive while Slow is in flight and queue behind it
            var echoes = Enumerable.Range(0, 5).Select(i => rpc.Send<int?>("Test.EchoNullable", (int?)i)).ToArray();

            Assert.Equal(42, await slow);
            var results = await Task.WhenAll(echoes);

            Assert.Equal(Enumerable.Range(0, 5).Select(i => (int?)i).ToArray(), results);
            Assert.Equal(2, connection.InvokeCount); //Slow alone, then one batch of five
        }

        [Fact]
        public async Task Batch_OneFailingCallDoesNotFailTheOthers()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            rpc.BatchMode = RpcBatchMode.Window;
            connection.InvokeCount = 0;

            var throwsTask = rpc.Send<int>("Test.Throws", null);
            var echoTask = rpc.Send<int?>("Test.EchoNullable", (int?)3);

            await Assert.ThrowsAsync<Exception>(() => throwsTask);
            Assert.Equal((int?)3, await echoTask);
            Assert.Equal(1, connection.InvokeCount); //both travelled in the same batch
        }

        [Fact]
        public async Task WindowMode_MultichannelCallsBatchAndStillRoundTrip()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            rpc.BatchMode = RpcBatchMode.Window;
            connection.InvokeCount = 0;

            var a = rpc.SendMultiChannelMessage<int?>("Test.EchoNullable", (int?)1);
            var b = rpc.SendMultiChannelMessage<int?>("Test.EchoNullable", (int?)2);

            Assert.Equal((int?)1, await a);
            Assert.Equal((int?)2, await b);
            //one batch carries both envelopes, the replies arrive as pushes outside the request path
            Assert.Equal(1, connection.InvokeCount);
        }

        [Fact]
        public async Task Batch_AsyncProceduresWithMiddleware_AllSucceed()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            Exception serverError = null;
            _fixture.Server.CallMiddleware = async (call, next) => await next(call);
            _fixture.Server.OnError = ex => serverError = ex;
            try
            {
                rpc.BatchMode = RpcBatchMode.Window;
                connection.InvokeCount = 0;

                var tasks = Enumerable.Range(0, 3).Select(_ => rpc.Send<int>("Test.Slow", null)).ToArray();
                var results = await Task.WhenAll(tasks);

                Assert.All(results, r => Assert.Equal(42, r));
                Assert.Equal(1, connection.InvokeCount);
                Assert.Null(serverError);
            }
            finally
            {
                _fixture.Server.CallMiddleware = null;
                _fixture.Server.OnError = null;
            }
        }

        [Fact]
        public async Task Batch_ItemsRunSequentially()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            rpc.BatchMode = RpcBatchMode.Window;
            connection.InvokeCount = 0;

            var timer = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, 3).Select(_ => rpc.Send<int>("Test.Slow", null)).ToArray();
            var results = await Task.WhenAll(tasks);
            timer.Stop();

            Assert.All(results, r => Assert.Equal(42, r));
            Assert.Equal(1, connection.InvokeCount);
            //three 100 ms procedures in one batch: sequential execution takes at least their sum,
            //concurrent execution would finish in roughly a single delay. Batching is a network
            //optimization, not a concurrency primitive - earlier calls must complete before later ones run.
            Assert.True(timer.ElapsedMilliseconds >= 250,
                $"batch items appear to have run concurrently ({timer.ElapsedMilliseconds}ms for 3 x 100ms)");
        }

        [Fact]
        public async Task BatchCompression_CompressibleBatch_CompressesEnvelopeOnce()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            rpc.BatchMode = RpcBatchMode.Window;
            connection.InvokeCount = 0;
            byte[] lastRequest = null, lastResponse = null;
            connection.RequestSpy = b => lastRequest = b;
            connection.ResponseSpy = b => lastResponse = b;
            var payload = new string('x', 5000);

            var tasks = Enumerable.Range(0, 3).Select(i => rpc.Send<string>("Test.BigEcho", payload + i)).ToArray();
            var results = await Task.WhenAll(tasks);

            for (int i = 0; i < 3; i++)
                Assert.Equal(payload + i, results[i]);
            Assert.Equal(1, connection.InvokeCount);

            //request: [u16 0][isCompressed][...] - the whole envelope compressed in one pass,
            //well below three individually-compressed requests would total
            Assert.Equal(0, lastRequest[0]);
            Assert.Equal(0, lastRequest[1]);
            Assert.Equal(1, lastRequest[2]);
            Assert.True(lastRequest.Length < 3 * payload.Length, "batch request was not compressed");

            //response: envelope compressed once, the items inside are raw
            Assert.Equal(1, lastResponse[1]);
            AssertBatchItemFlags(lastResponse, 0, 0, 0);
        }

        [Fact]
        public async Task BatchCompression_MixedBatch_FallsBackToPerItemCompression()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            rpc.BatchMode = RpcBatchMode.Window;
            connection.InvokeCount = 0;
            byte[] lastResponse = null;
            connection.ResponseSpy = b => lastResponse = b;
            var payload = new string('y', 20000);

            //BigEchoRaw is [RpcFunc(Compression = Disabled)]: its response must never be
            //recompressed, so the envelope stays plain and only BigEcho's item compresses
            var big = rpc.Send<string>("Test.BigEcho", payload);
            var raw = rpc.Send<string>("Test.BigEchoRaw", payload);

            Assert.Equal(payload, await big);
            Assert.Equal(payload, await raw);
            Assert.Equal(1, connection.InvokeCount);

            Assert.Equal(0, lastResponse[1]); //envelope not compressed
            AssertBatchItemFlags(lastResponse, 1, 0); //BigEcho compressed, BigEchoRaw untouched
        }

        [Fact]
        public async Task BatchCompression_LoneFlushedCall_IsStillCompressed()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();
            rpc.BatchMode = RpcBatchMode.Window;
            connection.InvokeCount = 0;
            byte[] lastRequest = null;
            connection.RequestSpy = b => lastRequest = b;
            var payload = new string('z', 20000);

            //built raw for potential packing, flushed alone: compression is applied at flush
            //so batching never ships large payloads uncompressed
            var echoed = await rpc.Send<string>("Test.BigEcho", payload);

            Assert.Equal(payload, echoed);
            Assert.Equal(1, connection.InvokeCount);
            Assert.False(lastRequest[0] == 0 && lastRequest[1] == 0); //unwrapped, not a batch
            Assert.Equal(1, lastRequest[2]); //request compression flag
            Assert.True(lastRequest.Length < payload.Length, "lone flushed request was not compressed");
        }

        /// <summary>decodes a batch response envelope and asserts each item's isCompressed flag</summary>
        private static void AssertBatchItemFlags(byte[] response, params byte[] expectedFlags)
        {
            var reader = new LiteReader(response);
            Assert.Equal((byte)ErrorCodes.Success, reader.ReadByte());
            if (reader.ReadBoolean())
                reader.Decompress();
            var count = reader.ReadUInt16();
            Assert.Equal(expectedFlags.Length, count);
            for (int i = 0; i < count; i++)
            {
                var item = reader.ReadByteArray();
                Assert.Equal(expectedFlags[i], item[1]);
            }
        }

        [Fact]
        public async Task Server_RejectsBatchLargerThanMaxBatchSize()
        {
            var (_, connection) = await _fixture.CreateClientAsync();
            var oversized = BuildRawBatch(_fixture.Server.MaxBatchSize + 1);

            var response = await _fixture.Server.Receive(connection.ServerSide, oversized);

            Assert.Equal((byte)ErrorCodes.BadRequest, response[0]);
        }

        [Fact]
        public async Task Server_RejectsNestedBatch()
        {
            var (_, connection) = await _fixture.CreateClientAsync();
            var outer = BuildRawBatchAround(BuildRawBatch(1));

            var response = await _fixture.Server.Receive(connection.ServerSide, outer);

            //the outer batch itself succeeds, the batch nested inside it is rejected in its slot
            var items = ParseBatchResponse(response);
            Assert.Single(items);
            Assert.Equal((byte)ErrorCodes.BadRequest, items[0][0]);
        }

        //sync helpers because LiteWriter/LiteReader are ref structs and can't appear in async methods

        private static byte[] BuildRawBatch(int count)
        {
            var handshake = new byte[] { 0, 0, 0, 0 };
            var writer = new LiteWriter();
            writer.WriteUInt16(0); //system opcode
            writer.WriteByte(0); //not compressed
            writer.WriteByte(4); //type 4: batch
            writer.WriteUInt16((ushort)count);
            for (int i = 0; i < count; i++)
                writer.WriteByteArray(handshake);
            return writer.Close();
        }

        private static byte[] BuildRawBatchAround(byte[] item)
        {
            var writer = new LiteWriter();
            writer.WriteUInt16(0); //system opcode
            writer.WriteByte(0); //not compressed
            writer.WriteByte(4); //type 4: batch
            writer.WriteUInt16(1);
            writer.WriteByteArray(item);
            return writer.Close();
        }

        private static List<byte[]> ParseBatchResponse(byte[] response)
        {
            var reader = new LiteReader(response);
            Assert.Equal((byte)ErrorCodes.Success, reader.ReadByte());
            Assert.False(reader.ReadBoolean()); //the batch envelope is never compressed
            var count = reader.ReadUInt16();
            var items = new List<byte[]>(count);
            for (int i = 0; i < count; i++)
                items.Add(reader.ReadByteArray());
            return items;
        }
    }
}
