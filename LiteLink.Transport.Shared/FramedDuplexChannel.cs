using LiteLink.Shared;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LiteLink.Transport
{
    internal enum FrameKind : byte
    {
        /// <summary>client -> server call, expects a Response frame with the same request id</summary>
        Request = 0,
        /// <summary>server -> client reply to a Request frame</summary>
        Response = 1,
        /// <summary>server -> client one-way message (RpcLink.Invoke / multichannel replies), no id</summary>
        Push = 2
    }

    /// <summary>
    /// Multiplexes LiteLink request/response calls and server pushes over a single duplex byte
    /// stream (a TCP NetworkStream/SslStream or a QUIC bidirectional stream). SignalR provides
    /// invocation correlation natively; raw streams don't, so each frame carries a request id:
    ///
    ///   [i32 LE frameLength][u8 kind][u32 LE requestId][payload (frameLength - 5 bytes)]
    ///
    /// The same class serves both sides: clients use <see cref="InvokeAsync"/> + <see cref="PushHandler"/>,
    /// servers use <see cref="RequestHandler"/> + <see cref="SendPushAsync"/>.
    /// </summary>
    internal sealed class FramedDuplexChannel : IDisposable
    {
        private const int FrameHeaderSize = 5; //kind + request id

        private readonly Stream _stream;
        private readonly int _maxFrameSize;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pendingRequests
            = new ConcurrentDictionary<uint, TaskCompletionSource<byte[]>>();
        private int _nextRequestId;
        private volatile bool _disposed;

        //bounds how many requests one connection may queue while waiting for the request gate;
        //when full, the read loop stops pulling frames and the transport's own flow control
        //throttles the sender instead of buffering payloads unboundedly
        private const int MaxQueuedRequests = 256;
        private SemaphoreSlim _requestGate;
        private SemaphoreSlim _queueSpace;

        /// <summary>server side: invoked for every Request frame, returns the response payload</summary>
        public Func<byte[], Task<byte[]>> RequestHandler { get; set; }

        /// <summary>
        /// server side: how many ordinary requests this connection may execute in parallel; queued
        /// requests run in arrival order. 0 or negative (the default) means unlimited. System traffic
        /// (handshake, invoke responses, multichannel envelopes) bypasses the gate, see
        /// <see cref="BypassesRequestGate"/>. Must be set before <see cref="RunAsync"/>.
        /// </summary>
        public int MaxParallelRequests { get; set; }

        /// <summary>client side: invoked for every Push frame</summary>
        public Action<byte[]> PushHandler { get; set; }

        /// <summary>errors that can't be surfaced to a caller (protocol violations, orphan responses, handler crashes)</summary>
        public Action<Exception> OnError { get; set; }

        public FramedDuplexChannel(Stream stream, int maxFrameSize)
        {
            _stream = stream;
            _maxFrameSize = maxFrameSize;
        }

        /// <summary>sends a request frame and awaits the matching response payload</summary>
        public async Task<byte[]> InvokeAsync(byte[] payload)
        {
            var id = unchecked((uint)Interlocked.Increment(ref _nextRequestId));
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingRequests.TryAdd(id, tcs))
                throw new InvalidOperationException("Request id collision on channel");

            try
            {
                await WriteFrameAsync(FrameKind.Request, id, payload);
            }
            catch
            {
                _pendingRequests.TryRemove(id, out _);
                throw;
            }

            return await tcs.Task;
        }

        public Task SendPushAsync(byte[] payload)
        {
            return WriteFrameAsync(FrameKind.Push, 0, payload);
        }

        private Task SendResponseAsync(uint id, byte[] payload)
        {
            return WriteFrameAsync(FrameKind.Response, id, payload);
        }

        private async Task WriteFrameAsync(FrameKind kind, uint id, byte[] payload)
        {
            payload ??= Array.Empty<byte>();
            //single buffer so the frame goes out in one write and can't interleave with another frame
            var frame = new byte[4 + FrameHeaderSize + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, FrameHeaderSize + payload.Length);
            frame[4] = (byte)kind;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), id);
            payload.CopyTo(frame, 9);

            await _writeLock.WaitAsync();
            try
            {
                await _stream.WriteAsync(frame, 0, frame.Length);
                await _stream.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// reads frames until the stream closes; always fails outstanding requests on exit so
        /// callers awaiting InvokeAsync don't hang after a disconnect
        /// </summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            if (MaxParallelRequests > 0 && _requestGate == null)
            {
                _requestGate = new SemaphoreSlim(MaxParallelRequests, MaxParallelRequests);
                _queueSpace = new SemaphoreSlim(MaxQueuedRequests, MaxQueuedRequests);
            }

            var header = new byte[4];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!await TryReadExactAsync(header, 4, cancellationToken))
                        break; //clean EOF between frames

                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length < FrameHeaderSize || length > _maxFrameSize)
                        throw new InvalidDataException($"Invalid frame length {length} (max {_maxFrameSize})");

                    var frame = new byte[length];
                    if (!await TryReadExactAsync(frame, length, cancellationToken))
                        throw new EndOfStreamException("Connection closed in the middle of a frame");

                    var kind = (FrameKind)frame[0];
                    var id = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(1));
                    var payload = new byte[length - FrameHeaderSize];
                    Array.Copy(frame, FrameHeaderSize, payload, 0, payload.Length);

                    switch (kind)
                    {
                        case FrameKind.Request:
                            await DispatchRequestAsync(id, payload, cancellationToken);
                            break;

                        case FrameKind.Response:
                            if (_pendingRequests.TryRemove(id, out var tcs))
                                tcs.TrySetResult(payload);
                            else
                                OnError?.Invoke(new Exception("Received a response for unknown request id " + id));
                            break;

                        case FrameKind.Push:
                            try
                            {
                                PushHandler?.Invoke(payload);
                            }
                            catch (Exception ex)
                            {
                                OnError?.Invoke(ex);
                            }
                            break;

                        default:
                            throw new InvalidDataException("Unknown frame kind " + frame[0]);
                    }
                }
            }
            catch (Exception) when (_disposed || cancellationToken.IsCancellationRequested)
            {
                //expected teardown noise, the stream was closed under the read loop
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }
            finally
            {
                FailPendingRequests(new Exception("Connection was closed while the request was in flight"));
            }
        }

        private async ValueTask DispatchRequestAsync(uint id, byte[] payload, CancellationToken cancellationToken)
        {
            var handler = RequestHandler;
            if (handler == null)
            {
                OnError?.Invoke(new Exception("Received a request frame but no request handler is registered"));
                return;
            }

            if (_requestGate == null || BypassesRequestGate(payload))
            {
                //dispatched to the pool so a slow procedure doesn't block reading the next frame,
                //responses are matched by id so completion order doesn't matter
                _ = Task.Run(() => RunRequestAsync(handler, id, payload));
                return;
            }

            //queue space is claimed on the read loop: when the queue is full, frame reading pauses
            //and the transport's flow control pushes back on the sender. The gate position is also
            //claimed here (SemaphoreSlim serves async waiters FIFO) so gated requests execute in
            //arrival order; only the wait itself moves off the loop so reading continues - blocking
            //here on the gate would deadlock a procedure that awaits a reply from this same client
            await _queueSpace.WaitAsync(cancellationToken);
            var turn = _requestGate.WaitAsync();
            _ = Task.Run(async () =>
            {
                await turn;
                try
                {
                    await RunRequestAsync(handler, id, payload);
                }
                finally
                {
                    _requestGate.Release();
                    _queueSpace.Release();
                }
            });
        }

        /// <summary>
        /// requests that skip the parallelism gate: they're either cheap by construction or carry
        /// their own concurrency control. [u16 opcode][u8 compressed][u8 type]: non-system opcodes
        /// and batches (type 4, whose items already run sequentially but represent real work) are
        /// gated; handshakes (0), multichannel envelopes (1, the ack is cheap and the inner request
        /// is gated by RpcLink.AllowMultiChannel), api info (2) and invoke responses (3, gating them
        /// would deadlock a procedure awaiting its client's reply at limit 1) bypass. A compressed
        /// system envelope can only be a batch, so it's gated without decompressing.
        /// </summary>
        private static bool BypassesRequestGate(byte[] payload)
        {
            if (payload.Length < 4)
                return false; //legacy 3-byte handshake or malformed, cheap either way
            if (payload[0] != 0 || payload[1] != 0)
                return false; //ordinary procedure call
            if (payload[2] != 0)
                return false; //compressed system envelope: only batch envelopes compress
            var type = payload[3];
            return type == 0 || type == 1 || type == 2 || type == 3;
        }

        private async Task RunRequestAsync(Func<byte[], Task<byte[]>> handler, uint id, byte[] payload)
        {
            byte[] response;
            try
            {
                response = await handler(payload);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
                response = new byte[] { (byte)ErrorCodes.Unhandled, 0 };
            }

            try
            {
                await SendResponseAsync(id, response);
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    OnError?.Invoke(ex);
            }
        }

        private async Task<bool> TryReadExactAsync(byte[] buffer, int count, CancellationToken cancellationToken)
        {
            var total = 0;
            while (total < count)
            {
                var read = await _stream.ReadAsync(buffer, total, count - total, cancellationToken);
                if (read == 0)
                {
                    if (total == 0)
                        return false;
                    throw new EndOfStreamException("Connection closed in the middle of a frame");
                }
                total += read;
            }
            return true;
        }

        private void FailPendingRequests(Exception exception)
        {
            foreach (var id in _pendingRequests.Keys)
            {
                if (_pendingRequests.TryRemove(id, out var tcs))
                    tcs.TrySetException(exception);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _stream.Dispose();
            FailPendingRequests(new Exception("Connection was closed while the request was in flight"));
        }
    }
}
