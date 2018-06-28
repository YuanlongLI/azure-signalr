﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Hosting;
using Microsoft.AspNet.SignalR.Hubs;
using Microsoft.AspNetCore.Connections;
using Microsoft.Azure.SignalR;
using Microsoft.Azure.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Owin;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.AspNet.SignalR
{
    internal class ServiceConnection : IServiceConnection
    {
        private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(15);
        // Service ping rate is 15 sec; this is 2 times that.
        private static readonly TimeSpan DefaultServiceTimeout = TimeSpan.FromSeconds(30);
        private static readonly long DefaultServiceTimeoutTicks = DefaultServiceTimeout.Seconds * Stopwatch.Frequency;
        // App server ping rate is 5 sec. So service can detect an irresponsive server connection in 10 seconds at most.
        private static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(5);
        private static readonly int MaxReconnectBackoffInternalInMilliseconds = 1000;

        private readonly ConcurrentDictionary<string, AzureTransport> _connections = new ConcurrentDictionary<string, AzureTransport>();
        private readonly SemaphoreSlim _serviceConnectionLock = new SemaphoreSlim(1, 1);
        private readonly IServiceProtocol _serviceProtocol;
        private readonly JsonSerializer _serializer;
        private readonly HandshakeRequestMessage _handshakeRequest;
        private readonly HubConfiguration _config;
        private readonly IConnectionFactory _connectionFactory;
        private readonly ILogger _logger;
        private readonly ReadOnlyMemory<byte> _cachedPingBytes;

        private readonly ConcurrentDictionary<string, string> _clientConnectionIds =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private ConnectionContext _connection;
        private bool _isStopped;
        private long _lastReceiveTimestamp;

        // Start reconnect after a random interval less than 1 second
        private static TimeSpan ReconnectInterval =>
            TimeSpan.FromMilliseconds(StaticRandom.Next(MaxReconnectBackoffInternalInMilliseconds));

        public string HubName { get; }

        public string ConnectionId { get; }

        public ServiceConnection(string hubName, string connectionId, IServiceProtocol serviceProtocol, HubConfiguration config, IConnectionFactory connectionFactory, ILogger logger)
        {
            HubName = hubName;
            ConnectionId = connectionId;
            _serviceProtocol = serviceProtocol;
            _config = config;
            _connectionFactory = connectionFactory;

            _logger = logger;
            _handshakeRequest = new HandshakeRequestMessage(_serviceProtocol.Version);

            _serializer = _config.Resolver.Resolve<JsonSerializer>();
            _cachedPingBytes = _serviceProtocol.GetMessageBytes(PingMessage.Instance);
        }

        public async Task StartAsync()
        {
            while (!_isStopped)
            {
                if (!await StartAsyncCore())
                {
                    return;
                }

                await ProcessIncomingAsync();
            }
        }

        public async Task WriteAsync(string connectionId, object value)
        {
            // TODO: use MemoryPoolTextWriter
            var ms = new MemoryStream();
            using (var sw = new StreamWriter(ms))
            {
                _serializer.Serialize(new JsonTextWriter(sw), value);
                sw.Flush();
                ms.TryGetBuffer(out var buffer);
                await WriteAsync(new ConnectionDataMessage(connectionId, buffer.AsMemory()));
            }
        }

        public async Task WriteAsync(ServiceMessage serviceMessage)
        {
            // We have to lock around outgoing sends since the pipe is single writer.
            // The lock is per serviceConnection
            await _serviceConnectionLock.WaitAsync();

            if (_connection == null)
            {
                _serviceConnectionLock.Release();
                throw new InvalidOperationException("The connection is not active, data cannot be sent to the service.");
            }

            try
            {
                // Write the service protocol message
                _serviceProtocol.WriteMessage(serviceMessage, _connection.Transport.Output);
                await _connection.Transport.Output.FlushAsync();
            }
            catch (Exception ex)
            {
                Log.FailedToWrite(_logger, ex);
            }
            finally
            {
                _serviceConnectionLock.Release();
            }
        }

        // For test purpose only
        internal Task StopAsync()
        {
            _isStopped = true;
            _connection?.Transport.Input.CancelPendingRead();
            return Task.CompletedTask;
        }

        private async Task ProcessIncomingAsync()
        {
            var keepAliveTimer = StartKeepAliveTimer();
            try
            {
                while (true)
                {
                    var result = await _connection.Transport.Input.ReadAsync();
                    var buffer = result.Buffer;

                    try
                    {
                        if (result.IsCanceled)
                        {
                            Log.ReadingCancelled(_logger, ConnectionId);
                            break;
                        }

                        if (!buffer.IsEmpty)
                        {
                            Log.ReceivedMessage(_logger, buffer.Length, ConnectionId);

                            UpdateReceiveTimestamp();

                            while (_serviceProtocol.TryParseMessage(ref buffer, out var message))
                            {
                                _ = DispatchMessageAsync(message);
                            }
                        }

                        if (result.IsCompleted)
                        {
                            // The connection is closed (reconnect)
                            Log.ServiceConnectionClosed(_logger, ConnectionId);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Error occurs in handling the message, but the connection between SDK and service still works.
                        // So, just log error instead of breaking the connection
                        Log.ErrorProcessingMessages(_logger, ex);
                    }
                    finally
                    {
                        _connection.Transport.Input.AdvanceTo(buffer.Start, buffer.End);
                    }
                }
            }
            catch (Exception ex)
            {
                // Fatal error: There is something wrong for the connection between SDK and service.
                // Abort all the client connections, close the httpConnection.
                // Only reconnect can recover.
                Log.ConnectionDropped(_logger, ConnectionId, ex);
            }
            finally
            {
                keepAliveTimer.Stop();
                await _connectionFactory.DisposeAsync(_connection);
            }

            await _serviceConnectionLock.WaitAsync();
            try
            {
                _connection = null;
            }
            finally
            {
                _serviceConnectionLock.Release();
            }
            // TODO: Never cleanup connections unless Service asks us to do that
            // Current implementation is based on assumption that Service will drop clients
            // if server connection fails.
            await CleanupConnections();
        }

        private async Task<bool> StartAsyncCore()
        {
            // Always try until connected
            while (true)
            {
                // Lock here in case somebody tries to send before the connection is assigned
                await _serviceConnectionLock.WaitAsync();

                try
                {
                    _connection = await _connectionFactory.ConnectAsync(TransferFormat.Binary, ConnectionId, HubName);

                    if (await HandshakeAsync())
                    {
                        Log.ServiceConnectionConnected(_logger, ConnectionId);
                        return true;
                    }
                    else
                    {
                        // False means we got a HandshakeResponseMessage with error. Will take below actions:
                        // - Dispose the connection
                        // - Stop reconnect
                        await _connectionFactory.DisposeAsync(_connection);
                        _connection = null;

                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Log.FailedToConnect(_logger, ex);

                    if (_connection != null)
                    {
                        await _connectionFactory.DisposeAsync(_connection);
                        _connection = null;
                    }

                    await Task.Delay(ReconnectInterval);
                }
                finally
                {
                    _serviceConnectionLock.Release();
                }
            }
        }

        private Task DispatchMessageAsync(ServiceMessage message)
        {
            switch (message)
            {
                case OpenConnectionMessage openConnectionMessage:
                    return OnConnectedAsync(openConnectionMessage);
                case CloseConnectionMessage closeConnectionMessage:
                    return PerformDisconnectAsync(closeConnectionMessage.ConnectionId);
                case ConnectionDataMessage connectionDataMessage:
                    return OnMessageAsync(connectionDataMessage);
                case PingMessage _:
                    // ignore ping
                    break;
            }
            return Task.CompletedTask;
        }

        private Task OnConnectedAsync(OpenConnectionMessage message)
        {
            // Writing from the application to the service
            _ = ProcessOutgoingMessagesAsync(message.ConnectionId);

            return Task.CompletedTask;
        }

        private Task PerformDisconnectAsync(string connectionId)
        {
            if (_connections.TryRemove(connectionId, out var transport))
            {
                transport.OnDisconnected();
            }

            return Task.CompletedTask;
        }

        private Task OnMessageAsync(ConnectionDataMessage connectionDataMessage)
        {
            if (_connections.TryGetValue(connectionDataMessage.ConnectionId, out var transport))
            {
                MemoryMarshal.TryGetArray(connectionDataMessage.Payload, out var segment);

                transport.OnReceived(Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count));
            }

            return Task.CompletedTask;
        }

        private async Task ProcessOutgoingMessagesAsync(string connectionId)
        {
            var dispatcher = new HubDispatcher(_config);
            dispatcher.Initialize(_config.Resolver);

            var context = new OwinContext();
            var response = context.Response;
            var request = context.Request;
            response.Body = Stream.Null;
            request.Path = new PathString("/");

            // TODO: hub name
            request.QueryString = new QueryString($"connectionToken={connectionId}&connectionData=[%7B%22Name%22:%22{HubName}%22%7D]");

            var hostContext = new HostContext(context.Environment);
            context.Environment[ContextConstants.AzureServiceConnectionKey] = this;

            if (dispatcher.Authorize(hostContext.Request))
            {
                _ = dispatcher.ProcessRequest(hostContext);

                // TODO: check for errors written to the response

                // Assume OnConnected was raised, send the initialize response
                await WriteAsync(connectionId, new { S = 1, M = new object[0] });
                _connections[connectionId] = (AzureTransport)context.Environment[ContextConstants.AzureSignalRTransportKey];
            }
            else
            {
                // TODO: what do we do here?
            }
        }

        private async Task<bool> HandshakeAsync()
        {
            await SendHandshakeRequestAsync(_connection.Transport.Output);

            try
            {
                using (var cts = new CancellationTokenSource())
                {
                    if (!Debugger.IsAttached)
                    {
                        cts.CancelAfter(DefaultHandshakeTimeout);
                    }

                    if (await ReceiveHandshakeResponseAsync(_connection.Transport.Input, cts.Token))
                    {
                        Log.HandshakeComplete(_logger);
                        return true;
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorReceivingHandshakeResponse(_logger, ex);
                throw;
            }
        }

        private async Task SendHandshakeRequestAsync(PipeWriter output)
        {
            Log.SendingHandshakeRequest(_logger);

            _serviceProtocol.WriteMessage(_handshakeRequest, output);
            var sendHandshakeResult = await output.FlushAsync();
            if (sendHandshakeResult.IsCompleted)
            {
                throw new InvalidOperationException("Service disconnected before handshake complete.");
            }
        }

        private async Task<bool> ReceiveHandshakeResponseAsync(PipeReader input, CancellationToken token)
        {

            while (true)
            {
                var result = await input.ReadAsync(token);

                var buffer = result.Buffer;
                var consumed = buffer.Start;
                var examined = buffer.End;

                try
                {
                    if (result.IsCanceled)
                    {
                        throw new InvalidOperationException("Connection cancelled before handshake complete.");
                    }

                    if (!buffer.IsEmpty)
                    {
                        if (_serviceProtocol.TryParseMessage(ref buffer, out var message))
                        {
                            consumed = buffer.Start;
                            examined = consumed;

                            if (!(message is HandshakeResponseMessage handshakeResponse))
                            {
                                throw new InvalidDataException(
                                    $"{message.GetType().Name} received when waiting for handshake response.");
                            }

                            if (string.IsNullOrEmpty(handshakeResponse.ErrorMessage))
                            {
                                return true;
                            }

                            // Handshake error. Will stop reconnect.
                            Log.HandshakeError(_logger, handshakeResponse.ErrorMessage);
                            return false;
                        }
                    }

                    if (result.IsCompleted)
                    {
                        // Not enough data, and we won't be getting any more data.
                        throw new InvalidOperationException("Service disconnected before sending a handshake response.");
                    }
                }
                finally
                {
                    input.AdvanceTo(consumed, examined);
                }
            }
        }

        private TimerAwaitable StartKeepAliveTimer()
        {
            Log.StartingKeepAliveTimer(_logger, DefaultKeepAliveInterval);

            _lastReceiveTimestamp = Stopwatch.GetTimestamp();
            var timer = new TimerAwaitable(DefaultKeepAliveInterval, DefaultKeepAliveInterval);
            _ = KeepAliveAsync(timer);

            return timer;
        }

        private void UpdateReceiveTimestamp()
        {
            Interlocked.Exchange(ref _lastReceiveTimestamp, Stopwatch.GetTimestamp());
        }

        private async Task KeepAliveAsync(TimerAwaitable timer)
        {
            using (timer)
            {
                timer.Start();

                while (await timer)
                {
                    if (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastReceiveTimestamp) > DefaultServiceTimeoutTicks)
                    {
                        AbortConnection();
                        // We shouldn't get here twice.
                        continue;
                    }

                    // Send PingMessage to Service
                    await TrySendPingAsync();
                }
            }
        }

        private void AbortConnection()
        {
            if (!_serviceConnectionLock.Wait(0))
            {
                // Couldn't get the lock so skip the cancellation (we could be in the middle of reconnecting?)
                return;
            }

            try
            {
                // Stop the reading from connection
                if (_connection != null)
                {
                    _connection.Transport.Input.CancelPendingRead();
                    Log.ServiceTimeout(_logger, DefaultServiceTimeout);
                }
            }
            finally
            {
                _serviceConnectionLock.Release();
            }
        }

        private async ValueTask TrySendPingAsync()
        {
            if (!_serviceConnectionLock.Wait(0))
            {
                // Skip sending PingMessage when failed getting lock
                return;
            }

            try
            {
                await _connection.Transport.Output.WriteAsync(_cachedPingBytes);
                Log.SentPing(_logger);
            }
            catch (Exception ex)
            {
                Log.FailedSendingPing(_logger, ex);
            }
            finally
            {
                _serviceConnectionLock.Release();
            }
        }

        private async Task CleanupConnections()
        {
            try
            {
                if (_clientConnectionIds.Count == 0)
                {
                    return;
                }
                var tasks = new List<Task>(_clientConnectionIds.Count);
                foreach (var connectionId in _clientConnectionIds.Keys)
                {
                    tasks.Add(PerformDisconnectAsync(connectionId));
                }
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                Log.FailedToCleanupConnections(_logger, ex);
            }
        }

        private static class Log
        {
            public static void FailedToWrite(ILogger logger, Exception exception)
            {
            }

            public static void FailedToConnect(ILogger logger, Exception exception)
            {
            }

            public static void ErrorProcessingMessages(ILogger logger, Exception exception)
            {
            }

            public static void ConnectionDropped(ILogger logger, string serviceConnectionId, Exception exception)
            {
            }

            public static void FailedToCleanupConnections(ILogger logger, Exception exception)
            {
            }

            public static void ErrorSendingMessage(ILogger logger, Exception exception)
            {
            }

            public static void SendLoopStopped(ILogger logger, string connectionId, Exception exception)
            {
            }

            public static void ApplicaitonTaskFailed(ILogger logger, Exception exception)
            {
            }

            public static void FailToWriteMessageToApplication(ILogger logger, string connectionId, Exception exception)
            {
            }

            public static void ReceivedMessageForNonExistentConnection(ILogger logger, string connectionId)
            {
            }

            public static void ConnectedStarting(ILogger logger, string connectionId)
            {
            }

            public static void ConnectedEnding(ILogger logger, string connectionId)
            {
            }

            public static void CloseConnection(ILogger logger, string connectionId)
            {
            }

            public static void ServiceConnectionClosed(ILogger logger, string serviceConnectionId)
            {
            }

            public static void ServiceConnectionConnected(ILogger logger, string serviceConnectionId)
            {
            }

            public static void ReadingCancelled(ILogger logger, string serviceConnectionId)
            {
            }

            public static void ReceivedMessage(ILogger logger, long bytes, string serviceConnectionId)
            {
            }

            public static void StartingKeepAliveTimer(ILogger logger, TimeSpan keepAliveInterval)
            {
            }

            public static void ServiceTimeout(ILogger logger, TimeSpan serviceTimeout)
            {
            }

            public static void WriteMessageToApplication(ILogger logger, int count, string connectionId)
            {
            }

            public static void SendingHandshakeRequest(ILogger logger)
            {
            }

            public static void HandshakeComplete(ILogger logger)
            {
            }

            public static void ErrorReceivingHandshakeResponse(ILogger logger, Exception exception)
            {
            }

            public static void HandshakeError(ILogger logger, string error)
            {
            }

            public static void SentPing(ILogger logger)
            {
            }

            public static void FailedSendingPing(ILogger logger, Exception exception)
            {
            }
        }
    }
}