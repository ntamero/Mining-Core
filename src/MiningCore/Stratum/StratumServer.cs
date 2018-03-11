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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reactive;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using MiningCore.Banning;
using MiningCore.Buffers;
using MiningCore.Configuration;
using MiningCore.JsonRpc;
using MiningCore.Time;
using MiningCore.Util;
using NetUV.Core.Handles;
using NetUV.Core.Native;
using Newtonsoft.Json;
using NLog;
using Contract = MiningCore.Contracts.Contract;

namespace MiningCore.Stratum
{
    public abstract class StratumServer
    {
        protected StratumServer(IComponentContext ctx, IMasterClock clock)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));
            Contract.RequiresNonNull(clock, nameof(clock));

            this.ctx = ctx;
            this.clock = clock;
        }

        protected readonly Dictionary<string, StratumClient> clients = new Dictionary<string, StratumClient>();

        protected readonly IComponentContext ctx;
        protected readonly IMasterClock clock;
        protected readonly Dictionary<int, Tcp> ports = new Dictionary<int, Tcp>();
        protected ClusterConfig clusterConfig;
        protected IBanManager banManager;
        protected bool disableConnectionLogging = false;
        protected ILogger logger;

        protected abstract string LogCat { get; }

        public void Start(string id, params (IPEndPoint IPEndpoint, PoolEndpoint PoolEndpoint)[] stratumPorts)
        {
            Contract.RequiresNonNull(stratumPorts, nameof(stratumPorts));

            // every port gets serviced by a dedicated loop thread
            foreach(var port in stratumPorts)
            {
                var thread = new Thread(_ =>
                {
                    // TLS cert loading
                    X509Certificate2 tlsCert = null;

                    if (port.PoolEndpoint.Tls)
                        tlsCert = new X509Certificate2(port.PoolEndpoint.TlsPfxFile);

                    var loop = new Loop();

                    try
                    {
                        var listener = loop
                            .CreateTcp()
                            .NoDelay(true)
                            .SimultaneousAccepts(false)
                            .Listen(port.IPEndpoint, (con, ex) =>
                            {
                                if (ex == null)
                                    OnClientConnectedAsync(con, port.IPEndpoint, loop, tlsCert);
                                else
                                    logger.Error(() => $"[{LogCat}] Connection error state: {ex.Message}");
                            });

                        lock (ports)
                        {
                            ports[port.IPEndpoint.Port] = listener;
                        }
                    }

                    catch (Exception ex)
                    {
                        logger.Error(ex, $"[{LogCat}] {ex}");
                        throw;
                    }

                    var portDesc = tlsCert != null ? " [TLS]" : string.Empty;
                    logger.Info(() => $"[{LogCat}] Stratum port {port.IPEndpoint.Address}:{port.IPEndpoint.Port} online{portDesc}");

                    try
                    {
                        loop.RunDefault();
                    }

                    catch(Exception ex)
                    {
                        logger.Error(ex, $"[{LogCat}] {ex}");
                    }
                }) { Name = $"UvLoopThread {id}:{port.IPEndpoint.Port}" };

                thread.Start();
            }
        }

        public void StopListeners()
        {
            lock(ports)
            {
                var portValues = ports.Values.ToArray();

                for(int i = 0; i < portValues.Length; i++)
                {
                    var listener = portValues[i];

                    listener.Shutdown((tcp, ex) =>
                    {
                        if (tcp?.IsValid == true)
                            tcp.Dispose();
                    });
                }
            }
        }

        private async void OnClientConnectedAsync(Tcp con, IPEndPoint endpointConfig, Loop loop, X509Certificate2 tlsCert)
        {
            try
            {
                var remoteEndPoint = con.GetPeerEndPoint();

                // get rid of banned clients as early as possible
                if (banManager?.IsBanned(remoteEndPoint.Address) == true)
                {
                    logger.Debug(() => $"[{LogCat}] Disconnecting banned ip {remoteEndPoint.Address}");
                    con.Dispose();
                    return;
                }

                var connectionId = CorrelationIdGenerator.GetNextId();
                logger.Debug(() => $"[{LogCat}] Accepting connection [{connectionId}] from {remoteEndPoint.Address}:{remoteEndPoint.Port}");

                // setup client connection
                con.KeepAlive(true, 1);

                // create stream
                var stream = (Stream) new UvNetworkStream(con, loop);

                // TLS handshake
                if (tlsCert != null)
                {
                    SslStream sslStream = null;

                    try
                    {
                        sslStream = new SslStream(stream, false);
                        await sslStream.AuthenticateAsServerAsync(tlsCert, false, SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12, false);
                    }

                    catch (Exception ex)
                    {
                        logger.Error(() => $"[{LogCat}] TLS init failed: {ex.Message}: {ex.InnerException.ToString() ?? string.Empty}");
                        (sslStream ?? stream).Close();
                        return;
                    }

                    stream = sslStream;
                }

                // setup client
                var client = new StratumClient(clock, endpointConfig, connectionId);

                OnConnect(client);

                client.Start(stream, endpointConfig, 
                    data => OnReceiveAsync(client, data),
                    () => OnReceiveComplete(client),
                    ex => OnReceiveError(client, ex));

                // register client
                lock(clients)
                {
                    clients[connectionId] = client;
                }
            }

            catch(Exception ex)
            {
                logger.Error(ex, () => nameof(OnClientConnectedAsync));
            }
        }

        protected virtual async Task OnReceiveAsync(StratumClient client, PooledArraySegment<byte> data)
        {
            using (data)
            {
                JsonRpcRequest request = null;

                try
                {
                    // boot pre-connected clients
                    if (banManager?.IsBanned(client.RemoteEndpoint.Address) == true)
                    {
                        logger.Info(() => $"[{LogCat}] [{client.ConnectionId}] Disconnecting banned client @ {client.RemoteEndpoint.Address}");
                        DisconnectClient(client);
                        return;
                    }

                    // de-serialize
                    logger.Trace(() => $"[{LogCat}] [{client.ConnectionId}] Received request data: {StratumConstants.Encoding.GetString(data.Array, 0, data.Size)}");
                    request = client.DeserializeRequest(data);

                    // dispatch
                    if (request != null)
                    {
                        logger.Debug(() => $"[{LogCat}] [{client.ConnectionId}] Dispatching request '{request.Method}' [{request.Id}]");
                        await OnRequestAsync(client, new Timestamped<JsonRpcRequest>(request, clock.Now));
                    }

                    else
                        logger.Trace(() => $"[{LogCat}] [{client.ConnectionId}] Unable to deserialize request");
                }

                catch (JsonReaderException jsonEx)
                {
                    // junk received (no valid json)
                    logger.Error(() => $"[{LogCat}] [{client.ConnectionId}] Connection json error state: {jsonEx.Message}");

                    if (clusterConfig.Banning?.BanOnJunkReceive.HasValue == false || clusterConfig.Banning?.BanOnJunkReceive == true)
                    {
                        logger.Info(() => $"[{LogCat}] [{client.ConnectionId}] Banning client for sending junk");
                        banManager?.Ban(client.RemoteEndpoint.Address, TimeSpan.FromMinutes(30));
                    }
                }

                catch (Exception ex)
                {
                    if (request != null)
                        logger.Error(ex, () => $"[{LogCat}] [{client.ConnectionId}] Error processing request {request.Method} [{request.Id}]");
                }
            }
        }

        protected virtual void OnReceiveError(StratumClient client, Exception ex)
        {
            switch (ex)
            {
                case OperationException opEx:
                    // log everything but ECONNRESET which just indicates the client disconnecting
                    if (opEx.ErrorCode != ErrorCode.ECONNRESET)
                        logger.Error(() => $"[{LogCat}] [{client.ConnectionId}] Connection error state: {ex.Message}");
                    break;

                default:
                    logger.Error(() => $"[{LogCat}] [{client.ConnectionId}] Connection error state: {ex.Message}");
                    break;
            }

            DisconnectClient(client);
        }

        protected virtual void OnReceiveComplete(StratumClient client)
        {
            logger.Debug(() => $"[{LogCat}] [{client.ConnectionId}] Received EOF");

            DisconnectClient(client);
        }

        protected virtual void DisconnectClient(StratumClient client)
        {
            Contract.RequiresNonNull(client, nameof(client));

            var subscriptionId = client.ConnectionId;

            client.Disconnect();

            if (!string.IsNullOrEmpty(subscriptionId))
            {
                // unregister client
                lock(clients)
                {
                    clients.Remove(subscriptionId);
                }
            }

            OnDisconnect(subscriptionId);
        }

        protected void ForEachClient(Action<StratumClient> action)
        {
            StratumClient[] tmp;

            lock(clients)
            {
                tmp = clients.Values.ToArray();
            }

            foreach(var client in tmp)
            {
                try
                {
                    action(client);
                }

                catch(Exception ex)
                {
                    logger.Error(ex);
                }
            }
        }

        protected abstract void OnConnect(StratumClient client);

        protected virtual void OnDisconnect(string subscriptionId)
        {
        }

        protected abstract Task OnRequestAsync(StratumClient client,
            Timestamped<JsonRpcRequest> request);
    }
}
