// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Client.Tests;
using Microsoft.AspNetCore.Sockets.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.AspNetCore.SignalR.Client.Tests
{
    public partial class HttpConnectionTests
    {
        public class ConnectionLifecycle : LoggedTest
        {
            public ConnectionLifecycle(ITestOutputHelper output) : base(output)
            {
            }

            [Fact]
            public async Task CannotStartRunningConnection()
            {
                using (StartLog(out var loggerFactory))
                {
                    await WithConnectionAsync(CreateConnection(loggerFactory: loggerFactory), async (connection, closed) =>
                    {
                        await connection.StartAsync().OrTimeout();
                        var exception =
                            await Assert.ThrowsAsync<InvalidOperationException>(
                                async () => await connection.StartAsync().OrTimeout());
                        Assert.Equal("Cannot start a connection that is not in the Disconnected state.", exception.Message);
                    });
                }
            }


            [Fact]
            public async Task CannotStartConnectionDisposedAfterStarting()
            {
                using (StartLog(out var loggerFactory))
                {
                    await WithConnectionAsync(
                        CreateConnection(loggerFactory: loggerFactory),
                        async (connection, closed) =>
                        {
                            await connection.StartAsync().OrTimeout();
                            await connection.DisposeAsync();
                            var exception =
                                await Assert.ThrowsAsync<InvalidOperationException>(
                                    async () => await connection.StartAsync().OrTimeout());

                            Assert.Equal("Cannot start a connection that is not in the Disconnected state.", exception.Message);
                        });
                }
            }

            [Fact]
            public async Task CannotStartDisposedConnection()
            {
                using (StartLog(out var loggerFactory))
                {
                    await WithConnectionAsync(
                        CreateConnection(loggerFactory: loggerFactory),
                        async (connection, closed) =>
                        {
                            await connection.DisposeAsync();
                            var exception =
                                await Assert.ThrowsAsync<InvalidOperationException>(
                                    async () => await connection.StartAsync().OrTimeout());

                            Assert.Equal("Cannot start a connection that is not in the Disconnected state.", exception.Message);
                        });
                }
            }

            [Fact]
            public async Task CanDisposeStartingConnection()
            {
                using (StartLog(out var loggerFactory))
                {
                    await WithConnectionAsync(
                        CreateConnection(
                            loggerFactory: loggerFactory,
                            transport: new TestTransport(
                                onTransportStart: SyncPoint.Create(out var transportStart),
                                onTransportStop: SyncPoint.Create(out var transportStop))),
                        async (connection, closed) =>
                    {
                        // Start the connection and wait for the transport to start up.
                        var startTask = connection.StartAsync();
                        await transportStart.WaitForSyncPoint().OrTimeout();

                        // While the transport is starting, dispose the connection
                        var disposeTask = connection.DisposeAsync();
                        transportStart.Continue(); // We need to release StartAsync, because Dispose waits for it.

                        // Wait for start to finish, as that has to finish before the transport will be stopped.
                        await startTask.OrTimeout();

                        // Then release DisposeAsync (via the transport StopAsync call)
                        await transportStop.WaitForSyncPoint().OrTimeout();
                        transportStop.Continue();
                    });
                }
            }

            [Fact]
            public async Task CanStartConnectionThatFailedToStart()
            {
                using (StartLog(out var loggerFactory))
                {
                    var expected = new Exception("Transport failed to start");
                    var shouldFail = true;

                    Task OnTransportStart()
                    {
                        if (shouldFail)
                        {
                            // Succeed next time
                            shouldFail = false;
                            return Task.FromException(expected);
                        }
                        else
                        {
                            return Task.CompletedTask;
                        }
                    }

                    await WithConnectionAsync(
                        CreateConnection(
                            loggerFactory: loggerFactory,
                            transport: new TestTransport(onTransportStart: OnTransportStart)),
                        async (connection, closed) =>
                    {
                        var actual = await Assert.ThrowsAsync<Exception>(() => connection.StartAsync());
                        Assert.Same(expected, actual);

                        // Should succeed this time
                        shouldFail = false;

                        await connection.StartAsync().OrTimeout();
                    });
                }
            }

            [Fact]
            public async Task CanStartStoppedConnection()
            {
                using (StartLog(out var loggerFactory))
                {
                    await WithConnectionAsync(
                        CreateConnection(loggerFactory: loggerFactory),
                        async (connection, closed) =>
                    {
                        await connection.StartAsync().OrTimeout();
                        await connection.StopAsync().OrTimeout();
                        await connection.StartAsync().OrTimeout();
                    });
                }
            }

            [Fact]
            public async Task CanStopStartingConnection()
            {
                using (StartLog(out var loggerFactory))
                {
                    await WithConnectionAsync(
                        CreateConnection(
                            loggerFactory: loggerFactory,
                            transport: new TestTransport(onTransportStart: SyncPoint.Create(out var transportStart))),
                        async (connection, closed) =>
                    {
                        // Start and wait for the transport to start up.
                        var startTask = connection.StartAsync();
                        await transportStart.WaitForSyncPoint().OrTimeout();

                        // Stop the connection while it's starting
                        var stopTask = connection.StopAsync();
                        transportStart.Continue(); // We need to release Start in order for Stop to begin working.

                        // Wait for start to finish, which will allow stop to finish and the connection to close.
                        await startTask.OrTimeout();
                        await stopTask.OrTimeout();
                        await closed.OrTimeout();
                    });
                }
            }

            [Fact]
            public async Task StoppingStoppingConnectionNoOps()
            {
                using (StartLog(out var loggerFactory))
                {
                    await WithConnectionAsync(
                        CreateConnection(loggerFactory: loggerFactory),
                        async (connection, closed) =>
                    {
                        await connection.StartAsync().OrTimeout();
                        await Task.WhenAll(connection.StopAsync(), connection.StopAsync()).OrTimeout();
                        await closed.OrTimeout();
                    });
                }
            }

            [Fact]
            public async Task CanStartConnectionAfterConnectionStoppedWithError()
            {
                using (StartLog(out var loggerFactory))
                {
                    var httpHandler = new TestHttpMessageHandler();

                    var longPollResult = new TaskCompletionSource<HttpResponseMessage>();
                    httpHandler.OnLongPoll(cancellationToken => longPollResult.Task.OrTimeout());

                    httpHandler.OnSocketSend((data, _) =>
                    {
                        Assert.Collection(data, i => Assert.Equal(0x42, i));
                        return Task.FromResult(ResponseUtils.CreateResponse(HttpStatusCode.InternalServerError));
                    });

                    await WithConnectionAsync(
                        CreateConnection(httpHandler, loggerFactory, allowReconnect: false),
                        async (connection, closed) =>
                    {
                        await connection.StartAsync().OrTimeout();
                        await Assert.ThrowsAsync<HttpRequestException>(() => connection.SendAsync(new byte[] { 0x42 }).OrTimeout());

                        longPollResult.TrySetResult(ResponseUtils.CreateResponse(HttpStatusCode.NoContent));

                        // Wait for the connection to close, because the send failed.
                        await Assert.ThrowsAsync<HttpRequestException>(() => closed.OrTimeout());

                        // Start it up again
                        await connection.StartAsync().OrTimeout();
                    });
                }
            }

            [Fact]
            public async Task DisposedStoppingConnectionDisposesConnection()
            {
                using (StartLog(out var loggerFactory))
                {
                    await WithConnectionAsync(
                        CreateConnection(
                            loggerFactory: loggerFactory,
                            transport: new TestTransport(onTransportStop: SyncPoint.Create(out var transportStop))),
                        async (connection, closed) =>
                    {
                        // Start the connection
                        await connection.StartAsync().OrTimeout();

                        // Stop the connection
                        var stopTask = connection.StopAsync().OrTimeout();

                        // Once the transport starts shutting down
                        await transportStop.WaitForSyncPoint().OrTimeout();

                        // Start disposing and allow it to finish shutting down
                        var disposeTask = connection.DisposeAsync().OrTimeout();
                        transportStop.Continue();

                        // Wait for the tasks to complete
                        await stopTask.OrTimeout();
                        await closed.OrTimeout();
                        await disposeTask.OrTimeout();

                        // We should be disposed and thus unable to restart.
                        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.StartAsync().OrTimeout());
                        Assert.Equal("Cannot start a connection that is not in the Disconnected state.", exception.Message);
                    });
                }
            }

            [Fact]
            public async Task CanDisposeStoppedConnection()
            {
                using (StartLog(out var loggerFactory))
                {
                    await WithConnectionAsync(
                        CreateConnection(loggerFactory: loggerFactory),
                        async (connection, closed) =>
                        {
                            await connection.StartAsync().OrTimeout();
                            await connection.StopAsync().OrTimeout();
                            await closed.OrTimeout();
                            await connection.DisposeAsync().OrTimeout();
                        });
                }
            }

            [Fact]
            public Task ClosedEventRaisedWhenTheClientIsDisposed()
            {
                return WithConnectionAsync(
                    CreateConnection(),
                    async (connection, closed) =>
                    {
                        await connection.StartAsync().OrTimeout();
                        await connection.DisposeAsync().OrTimeout();
                        await closed.OrTimeout();
                    });
            }

            [Fact]
            public async Task ConnectionClosedWhenTransportFails()
            {
                var testTransport = new TestTransport();

                var expected = new Exception("Whoops!");

                await WithConnectionAsync(
                    CreateConnection(transport: testTransport, allowReconnect: false),
                async (connection, closed) =>
                {
                    await connection.StartAsync().OrTimeout();
                    testTransport.Application.Writer.TryComplete(expected);
                    var actual = await Assert.ThrowsAsync<Exception>(() => closed.OrTimeout());
                    Assert.Same(expected, actual);

                    var sendException = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendAsync(new byte[0]).OrTimeout());
                    Assert.Equal("Cannot send messages when the connection is not in the Connected state.", sendException.Message);
                });
            }

            [Fact]
            public Task ClosedEventNotRaisedWhenTheClientIsStoppedButWasNeverStarted()
            {
                return WithConnectionAsync(
                    CreateConnection(),
                    async (connection, closed) =>
                {
                    await connection.DisposeAsync().OrTimeout();
                    Assert.False(closed.IsCompleted);
                });
            }

            [Fact]
            public async Task TransportIsStoppedWhenConnectionIsStopped()
            {
                var testHttpHandler = new TestHttpMessageHandler();

                // Just keep returning data when polled
                testHttpHandler.OnLongPoll(_ => ResponseUtils.CreateResponse(HttpStatusCode.OK));

                using (var httpClient = new HttpClient(testHttpHandler))
                {
                    var longPollingTransport = new LongPollingTransport(httpClient);
                    await WithConnectionAsync(
                        CreateConnection(transport: longPollingTransport),
                        async (connection, closed) =>
                        {
                            // Start the transport
                            await connection.StartAsync().OrTimeout();
                            Assert.False(longPollingTransport.Running.IsCompleted, "Expected that the transport would still be running");

                            // Stop the connection, and we should stop the transport
                            await connection.StopAsync().OrTimeout();
                            await longPollingTransport.Running.OrTimeout();
                        });
                }
            }

            [Fact]
            public async Task ConnectionAutomaticallyReconnects()
            {
                using (StartLog(out var loggerFactory))
                {
                    var logger = loggerFactory.CreateLogger<HttpConnectionTests>();

                    var tcs = new TaskCompletionSource<object>();
                    var testTransport = new TestTransport(onTransportStart: () =>
                    {
                        tcs.TrySetResult(null);
                        return Task.CompletedTask;
                    });

                    await WithConnectionAsync(
                        CreateConnection(transport: testTransport, loggerFactory: loggerFactory),
                        async (connection, closed) =>
                        {
                            logger.LogInformation("Starting connection");
                            await connection.StartAsync().OrTimeout();
                            logger.LogInformation("Started connection");

                            // Wait for transport start
                            await tcs.Task.OrTimeout();
                            // Reset tcs to check for restart later
                            tcs = new TaskCompletionSource<object>();
                            // "Kill" connection to cause reconnect
                            logger.LogInformation("Triggering reconnect");
                            testTransport.Application.Writer.Complete();
                            // Check for transport start again
                            await tcs.Task.OrTimeout();
                            logger.LogInformation("Connection reconnected");

                            // Test to see if connection is alive
                            var onReceived = new SyncPoint();
                            connection.OnReceived(_ => onReceived.WaitToContinue().OrTimeout());

                            // This will trigger the received callback
                            testTransport.Application.Writer.TryWrite(Array.Empty<byte>());

                            await onReceived.WaitForSyncPoint().OrTimeout();
                            onReceived.Continue();

                            logger.LogInformation("Disposing connection");
                            await connection.DisposeAsync();
                            logger.LogInformation("Disposed connection");
                        });
                }
            }
        }
    }
}