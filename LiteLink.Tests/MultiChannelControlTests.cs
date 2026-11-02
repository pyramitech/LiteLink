extern alias client;

using client::LiteLink.Client;
using Xunit;

namespace LiteLink.Tests
{
    /// <summary>
    /// The multichannel control surface: client-side MultiChannelByDefault, the server-side
    /// AllowMultiChannel grant, the option baseline reset on reconnect and the ResponseTimeout
    /// coverage of plain sends.
    /// </summary>
    [Collection("RpcServer")]
    public class MultiChannelControlTests
    {
        private readonly RpcServerFixture _fixture;

        public MultiChannelControlTests(RpcServerFixture fixture)
        {
            _fixture = fixture;
        }

        /// <summary>[u16 opcode 0][u8 compressed][u8 type 1] marks a multichannel envelope</summary>
        private static bool IsMultiChannelEnvelope(byte[] request)
            => request.Length > 3 && request[0] == 0 && request[1] == 0 && request[3] == 1;

        [Fact]
        public async Task MultiChannelByDefault_WrapsSendsInMultichannelEnvelopes()
        {
            var (rpc, connection) = await _fixture.CreateClientAsync();

            byte[] lastRequest = null;
            connection.RequestSpy = data => lastRequest = data;

            //off: plain envelope, opcode first
            Assert.Equal("plain", await rpc.Send<string>("Test.BigEcho", "plain"));
            Assert.False(IsMultiChannelEnvelope(lastRequest));

            //toggled on mid-connection: the same Send goes out as a multichannel request
            rpc.MultiChannelByDefault = true;
            Assert.Equal("wrapped", await rpc.Send<string>("Test.BigEcho", "wrapped"));
            Assert.True(IsMultiChannelEnvelope(lastRequest));

            //and off again
            rpc.MultiChannelByDefault = false;
            Assert.Equal("plain again", await rpc.Send<string>("Test.BigEcho", "plain again"));
            Assert.False(IsMultiChannelEnvelope(lastRequest));
        }

        [Fact]
        public async Task MultiChannelByDefault_ConcurrentSends_PairResponsesCorrectly()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();
            rpc.MultiChannelByDefault = true;

            var results = await Task.WhenAll(Enumerable.Range(0, 30)
                .Select(i => rpc.Send<string>("Test.BigEcho", "payload-" + i)));

            for (int i = 0; i < results.Length; i++)
                Assert.Equal("payload-" + i, results[i]);
        }

        [Fact]
        public async Task AllowMultiChannel_Denied_FailsWithUnauthorized_PlainSendsStillWork()
        {
            var server = _fixture.Server;
            var previousDefault = server.AllowMultiChannelByDefault;
            server.AllowMultiChannelByDefault = false;
            try
            {
                var (rpc, connection) = await _fixture.CreateClientAsync();

                //denied: the multichannel ack carries Unauthorized (2)
                var ex = await Assert.ThrowsAsync<Exception>(
                    () => rpc.SendMultiChannelMessage<string>("Test.BigEcho", "nope"));
                Assert.Contains("Error code: 2", ex.Message);

                //MultiChannelByDefault routes into the same gate
                rpc.MultiChannelByDefault = true;
                var ex2 = await Assert.ThrowsAsync<Exception>(() => rpc.Send<string>("Test.BigEcho", "still nope"));
                Assert.Contains("Error code: 2", ex2.Message);
                rpc.MultiChannelByDefault = false;

                //plain requests are unaffected by the multichannel gate
                Assert.Equal("plain ok", await rpc.Send<string>("Test.BigEcho", "plain ok"));

                //granted mid-connection (the after-login flow): multichannel starts working
                _fixture.GetLink(connection).AllowMultiChannel = true;
                Assert.Equal("granted", await rpc.SendMultiChannelMessage<string>("Test.BigEcho", "granted"));
            }
            finally
            {
                server.AllowMultiChannelByDefault = previousDefault;
            }
        }

        [Fact]
        public async Task Reconnect_ResetsSessionOptions_KeepsConfiguredOptions()
        {
            var connection = new InMemoryClientConnection(_fixture.Server);
            var rpc = new RpcClient(connection);
            connection.Client = rpc;

            //set before the first Start: configuration, survives reconnects
            rpc.MaxBatchSize = 32;

            await rpc.Start();

            //set mid-connection: session state (e.g. unlocked by a login), dies with the connection
            rpc.MultiChannelByDefault = true;
            rpc.BatchMode = RpcBatchMode.Window;
            rpc.ResponseTimeout = TimeSpan.FromSeconds(5);

            await rpc.Start(); //reconnect

            Assert.False(rpc.MultiChannelByDefault);
            Assert.Equal(RpcBatchMode.Disabled, rpc.BatchMode);
            Assert.Equal(TimeSpan.FromSeconds(60), rpc.ResponseTimeout);
            Assert.Equal(32, rpc.MaxBatchSize); //configured before Start, kept

            Assert.Equal("after reconnect", await rpc.Send<string>("Test.BigEcho", "after reconnect"));
        }

        [Fact]
        public async Task Reconnect_FailsRequestsQueuedForBatching()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();

            //a window long enough that the queued request is still unflushed when we reconnect
            rpc.BatchMode = RpcBatchMode.Window;
            rpc.BatchWindow = TimeSpan.FromSeconds(30);
            var queued = rpc.Send<string>("Test.BigEcho", "never sent");

            await rpc.Start();

            var ex = await Assert.ThrowsAsync<Exception>(() => queued);
            Assert.Contains("queued for batching", ex.Message);
        }

        [Fact]
        public async Task ConnectionLost_FailsPendingMultichannelAndBatchedRequestsInstantly()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();

            //a multichannel call awaiting its response push (Slow answers in ~100ms) and a request
            //still held by the batching window: neither may wait out ResponseTimeout (60s here)
            var pendingMultichannel = rpc.SendMultiChannelMessage<int>("Test.Slow", null);
            await Task.Delay(20); //let the ack land before enabling batching, so it isn't queued itself

            rpc.BatchMode = RpcBatchMode.Window;
            rpc.BatchWindow = TimeSpan.FromSeconds(30);
            var pendingBatched = rpc.Send<string>("Test.BigEcho", "never sent");

            var timer = System.Diagnostics.Stopwatch.StartNew();
            rpc.OnConnectionLost();

            var mcEx = await Assert.ThrowsAsync<Exception>(() => pendingMultichannel);
            Assert.Contains("in flight", mcEx.Message);
            var batchEx = await Assert.ThrowsAsync<Exception>(() => pendingBatched);
            Assert.Contains("queued for batching", batchEx.Message);
            timer.Stop();
            Assert.True(timer.ElapsedMilliseconds < 5000, $"pending requests waited instead of failing instantly: {timer.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task ResponseTimeout_PlainSend_ThrowsTimeoutException()
        {
            var (rpc, _) = await _fixture.CreateClientAsync();

            rpc.ResponseTimeout = TimeSpan.FromMilliseconds(30);
            await Assert.ThrowsAsync<TimeoutException>(() => rpc.Send<int>("Test.Slow", null)); //Slow takes 100ms

            //a timeout must not poison the client for later calls
            rpc.ResponseTimeout = TimeSpan.FromSeconds(60);
            Assert.Equal(42, await rpc.Send<int>("Test.Slow", null));
        }
    }
}
