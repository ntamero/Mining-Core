/*
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Threading.Tasks.Dataflow;
using Autofac;
using MiningCore.Buffers;
using MiningCore.JsonRpc;
using MiningCore.Mining;
using MiningCore.Time;
using MiningCore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NLog;
using Contract = MiningCore.Contracts.Contract;

namespace MiningCore.Stratum
{
    public class StratumClient
    {
        public StratumClient(IPEndPoint endpointConfig, string connectionId)
        {
            PoolEndpoint = endpointConfig;
            ConnectionId = connectionId;
        }

        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        private const int MaxInboundRequestLength = 8192;
        private const int MaxOutboundRequestLength = 0x4000;

        private BufferBlock<PooledArraySegment<byte>> sendQueue;
        private readonly PooledLineBuffer plb = new PooledLineBuffer(logger, MaxInboundRequestLength);
        private IDisposable subscription;
        private bool isAlive = true;
        private WorkerContextBase context;

        private static readonly JsonSerializer serializer = new JsonSerializer
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        #region API-Surface

        public void Init(Socket socket, IMasterClock clock, Action<PooledArraySegment<byte>> onNext, Action onCompleted, Action<Exception> onError)
        {
            RemoteEndpoint = (IPEndPoint) socket.RemoteEndPoint;

            // initialize send queue
            sendQueue = new BufferBlock<PooledArraySegment<byte>>();

            // cleanup preparation
            subscription = Disposable.Create(() =>
            {
                if (isAlive)
                {
                    logger.Debug(() => $"[{ConnectionId}] Last subscriber disconnected from receiver stream");

                    isAlive = false;
                    socket.Close();
                }
            });

            // go
            DoReceive(socket, clock, onNext, onCompleted, onError);
            DoSend(socket, onError);
        }

        public string ConnectionId { get; private set; }
        public IPEndPoint PoolEndpoint { get; private set; }
        public IPEndPoint RemoteEndpoint { get; private set; }
        public DateTime? LastReceive { get; set; }
        public bool IsAlive { get; set; } = true;

        public void SetContext<T>(T value) where T : WorkerContextBase
        {
            context = value;
        }

        public T GetContextAs<T>() where T: WorkerContextBase
        {
            return (T) context;
        }

        public void Respond<T>(T payload, object id)
        {
            Contract.RequiresNonNull(payload, nameof(payload));
            Contract.RequiresNonNull(id, nameof(id));

            Respond(new JsonRpcResponse<T>(payload, id));
        }

        public void RespondError(StratumError code, string message, object id, object result = null, object data = null)
        {
            Contract.RequiresNonNull(message, nameof(message));

            Respond(new JsonRpcResponse(new JsonRpcException((int) code, message, null), id, result));
        }

        public void Respond<T>(JsonRpcResponse<T> response)
        {
            Contract.RequiresNonNull(response, nameof(response));

            Send(response);
        }

        public void Notify<T>(string method, T payload)
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(method), $"{nameof(method)} must not be empty");

            Notify(new JsonRpcRequest<T>(method, payload, null));
        }

        public void Notify<T>(JsonRpcRequest<T> request)
        {
            Contract.RequiresNonNull(request, nameof(request));

            Send(request);
        }

        public void Send<T>(T payload)
        {
            Contract.RequiresNonNull(payload, nameof(payload));

            if (isAlive)
            {
                var buf = ArrayPool<byte>.Shared.Rent(MaxOutboundRequestLength);

                try
                {
                    using (var stream = new MemoryStream(buf, true))
                    {
                        stream.SetLength(0);
                        int size;

                        using (var writer = new StreamWriter(stream, StratumConstants.Encoding))
                        {
                            serializer.Serialize(writer, payload);
                            writer.Flush();

                            // append newline
                            stream.WriteByte(0xa);
                            size = (int)stream.Position;
                        }

                        logger.Trace(() => $"[{ConnectionId}] Sending: {StratumConstants.Encoding.GetString(buf, 0, size)}");

                        SendInternal(new PooledArraySegment<byte>(buf, 0, size));
                    }
                }

                catch (Exception)
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    throw;
                }
            }
        }

        public void Disconnect()
        {
            subscription?.Dispose();
            subscription = null;

            IsAlive = false;
        }

        public void RespondError(object id, int code, string message)
        {
            Contract.RequiresNonNull(id, nameof(id));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(message), $"{nameof(message)} must not be empty");

            Respond(new JsonRpcResponse(new JsonRpcException(code, message, null), id));
        }

        public void RespondUnsupportedMethod(object id)
        {
            Contract.RequiresNonNull(id, nameof(id));

            RespondError(id, 20, "Unsupported method");
        }

        public void RespondUnauthorized(object id)
        {
            Contract.RequiresNonNull(id, nameof(id));

            RespondError(id, 24, "Unauthorized worker");
        }

        public JsonRpcRequest DeserializeRequest(PooledArraySegment<byte> data)
        {
            using (var stream = new MemoryStream(data.Array, data.Offset, data.Size))
            {
                using (var reader = new StreamReader(stream, StratumConstants.Encoding))
                {
                    using (var jreader = new JsonTextReader(reader))
                    {
                        return serializer.Deserialize<JsonRpcRequest>(jreader);
                    }
                }
            }
        }

        #endregion // API-Surface

        private async void DoReceive(Socket socket, IMasterClock clock,
            Action<PooledArraySegment<byte>> onNext, Action onCompleted, Action<Exception> onError)
        {
            while (isAlive)
            {
                var buf = ArrayPool<byte>.Shared.Rent(0x10000);
                var aseg = new ArraySegment<byte>(buf);

                try
                {
                    var cb = await socket.ReceiveAsync(aseg, SocketFlags.None);

                    if (cb == 0 || !isAlive)
                    {
                        onCompleted();
                        return;
                    }

                    LastReceive = clock.Now;

                    plb.Receive(buf, cb,
                        (src, dst, count) => Array.Copy(src, dst, count),
                        onNext,
                        onError);
                }

                catch(Exception ex)
                {
                    onError(ex);
                    break;
                }

                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
        }

        private void SendInternal(PooledArraySegment<byte> buffer)
        {
            try
            {
                sendQueue.Post(buffer);
            }

            catch (ObjectDisposedException)
            {
                buffer.Dispose();
            }
        }

        private async void DoSend(Socket socket, Action<Exception> onError)
        {
            while(isAlive)
            {
                try
                {
                    while (await sendQueue.OutputAvailableAsync())
                    {
                        var segment = await sendQueue.ReceiveAsync();

                        using (segment)
                        {
                            await socket.SendAsync(new ArraySegment<byte>(segment.Array, segment.Offset, segment.Size), SocketFlags.None);
                        }
                    }
                }

                catch (Exception ex)
                {
                    onError(ex);
                    break;
                }
            }
        }
    }
}
