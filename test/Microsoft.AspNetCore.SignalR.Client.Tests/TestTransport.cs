using System;
using System.IO.Pipelines;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Sockets;
using Microsoft.AspNetCore.Sockets.Client;

namespace Microsoft.AspNetCore.SignalR.Client.Tests
{
    public class TestTransport : ITransport
    {
        private readonly Func<Task> _stopHandler;
        private readonly Func<Task> _startHandler;

        public TransferFormat? Format { get; }
        public IDuplexPipe Application { get; private set; }

        public TestTransport(Func<Task> onTransportStop = null, Func<Task> onTransportStart = null, TransferFormat transferMode = TransferFormat.Text)
        {
            _stopHandler = onTransportStop ?? new Func<Task>(() => Task.CompletedTask);
            _startHandler = onTransportStart ?? new Func<Task>(() => Task.CompletedTask);
            Format = transferMode;
        }

        public Task StartAsync(Uri url, IDuplexPipe application, TransferFormat requestedTransferMode, IConnection connection)
        {
            Application = application;
            return _startHandler();
        }

        public async Task StopAsync()
        {
            await _stopHandler();
            Application.Output.Complete();
        }
    }
}
