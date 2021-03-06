// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Sockets.Internal;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Sockets
{
    public class ConnectionManager
    {
        // TODO: Consider making this configurable? At least for testing?
        private static readonly TimeSpan _heartbeatTickRate = TimeSpan.FromSeconds(1);

        private readonly ConcurrentDictionary<string, DefaultConnectionContext> _connections = new ConcurrentDictionary<string, DefaultConnectionContext>();
        private Timer _timer;
        private readonly ILogger<ConnectionManager> _logger;
        private object _executionLock = new object();
        private bool _disposed;

        public ConnectionManager(ILogger<ConnectionManager> logger, IApplicationLifetime appLifetime)
        {
            _logger = logger;
            appLifetime.ApplicationStarted.Register(() => Start());
            appLifetime.ApplicationStopping.Register(() => CloseConnections());
        }

        public void Start()
        {
            lock (_executionLock)
            {
                if (_disposed)
                {
                    return;
                }

                if (_timer == null)
                {
                    _timer = new Timer(Scan, this, _heartbeatTickRate, _heartbeatTickRate);
                }
            }
        }

        public bool TryGetConnection(string id, out DefaultConnectionContext connection)
        {
            return _connections.TryGetValue(id, out connection);
        }

        public DefaultConnectionContext CreateConnection(PipeOptions transportPipeOptions, PipeOptions appPipeOptions)
        {
            var id = MakeNewConnectionId();

            _logger.CreatedNewConnection(id);
            var connectionTimer = SocketEventSource.Log.ConnectionStart(id);
            var pair = DuplexPipe.CreateConnectionPair(transportPipeOptions, appPipeOptions);

            var connection = new DefaultConnectionContext(id, pair.Application, pair.Transport);
            connection.ConnectionTimer = connectionTimer;

            _connections.TryAdd(id, connection);
            return connection;
        }

        public DefaultConnectionContext CreateConnection()
        {
            return CreateConnection(PipeOptions.Default, PipeOptions.Default);
        }

        public void RemoveConnection(string id)
        {
            if (_connections.TryRemove(id, out var connection))
            {
                // Remove the connection completely
                SocketEventSource.Log.ConnectionStop(id, connection.ConnectionTimer);
                _logger.RemovedConnection(id);
            }
        }

        private static string MakeNewConnectionId()
        {
            // TODO: We need to sign and encyrpt this
            return Guid.NewGuid().ToString();
        }

        private static void Scan(object state)
        {
            ((ConnectionManager)state).Scan();
        }

        public void Scan()
        {
            // If we couldn't get the lock it means one of 2 things are true:
            // - We're about to dispose so we don't care to run the scan callback anyways.
            // - The previous Scan took long enough that the next scan tried to run in parallel
            // In either case just do nothing and end the timer callback as soon as possible
            if (!Monitor.TryEnter(_executionLock))
            {
                return;
            }

            try
            {
                if (_disposed)
                {
                    return;
                }

                // Pause the timer while we're running
                _timer.Change(Timeout.Infinite, Timeout.Infinite);

                // Time the scan so we know if it gets slower than 1sec
                var timer = ValueStopwatch.StartNew();
                SocketEventSource.Log.ScanningConnections();
                _logger.ScanningConnections();

                // Scan the registered connections looking for ones that have timed out
                foreach (var c in _connections)
                {
                    var status = DefaultConnectionContext.ConnectionStatus.Inactive;
                    var lastSeenUtc = DateTimeOffset.UtcNow;

                    try
                    {
                        c.Value.Lock.Wait();

                        // Capture the connection state
                        status = c.Value.Status;

                        lastSeenUtc = c.Value.LastSeenUtc;
                    }
                    finally
                    {
                        c.Value.Lock.Release();
                    }

                    // Once the decision has been made to dispose we don't check the status again
                    // But don't clean up connections while the debugger is attached.
                    if (!Debugger.IsAttached && status == DefaultConnectionContext.ConnectionStatus.Inactive && (DateTimeOffset.UtcNow - lastSeenUtc).TotalSeconds > 5)
                    {
                        _logger.ConnectionTimedOut(c.Value.ConnectionId);
                        SocketEventSource.Log.ConnectionTimedOut(c.Value.ConnectionId);
                        var ignore = DisposeAndRemoveAsync(c.Value);
                    }
                    else
                    {
                        // Tick the heartbeat, if the connection is still active
                        c.Value.TickHeartbeat();
                    }
                }

                // TODO: We could use this timer to determine if the connection scanner is too slow, but we need an idea of what "too slow" is.
                var elapsed = timer.GetElapsedTime();
                SocketEventSource.Log.ScannedConnections(elapsed);
                _logger.ScannedConnections(elapsed);

                // Resume once we finished processing all connections
                _timer.Change(_heartbeatTickRate, _heartbeatTickRate);
            }
            finally
            {
                // Exit the lock now
                Monitor.Exit(_executionLock);
            }
        }

        public void CloseConnections()
        {
            lock (_executionLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                // Stop firing the timer
                _timer?.Dispose();

                var tasks = new List<Task>();

                foreach (var c in _connections)
                {
                    tasks.Add(DisposeAndRemoveAsync(c.Value));
                }

                Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));
            }
        }

        public async Task DisposeAndRemoveAsync(DefaultConnectionContext connection)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (IOException ex)
            {
                _logger.ConnectionReset(connection.ConnectionId, ex);
            }
            catch (WebSocketException ex) when (ex.InnerException is IOException)
            {
                _logger.ConnectionReset(connection.ConnectionId, ex);
            }
            catch (Exception ex)
            {
                _logger.FailedDispose(connection.ConnectionId, ex);
            }
            finally
            {
                // Remove it from the list after disposal so that's it's easy to see
                // connections that might be in a hung state via the connections list
                RemoveConnection(connection.ConnectionId);
            }
        }
    }
}
