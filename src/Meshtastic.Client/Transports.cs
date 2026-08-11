using System;
using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Meshtastic.Client
{
    internal interface ITransport : IDisposable
    {
        Stream Stream { get; }
        string Kind { get; }
        string Endpoint { get; }
        Task OpenAsync(CancellationToken cancellationToken);
        void Close();
    }

    internal sealed class SerialTransport : ITransport
    {
        private readonly string _port; private readonly int _baudRate; private SerialPort _serial;
        public SerialTransport(string port, int baudRate) { _port = port; _baudRate = baudRate; }
        public Stream Stream { get { return _serial == null ? null : _serial.BaseStream; } }
        public string Kind { get { return "Serial"; } }
        public string Endpoint { get { return _port + " @ " + _baudRate + " baud"; } }
        public Task OpenAsync(CancellationToken cancellationToken)
        {
            _serial = new SerialPort(_port, _baudRate, Parity.None, 8, StopBits.One);
            _serial.ReadTimeout = SerialPort.InfiniteTimeout; _serial.WriteTimeout = SerialPort.InfiniteTimeout;
            _serial.Open();
            // Native Meshtastic serial clients send this resync preamble before the first protobuf frame.
            // It wakes sleeping devices and resets a partially parsed frame on the device side.
            var resync = new byte[32];
            for (var i = 0; i < resync.Length; i++) resync[i] = 0xC3;
            _serial.BaseStream.Write(resync, 0, resync.Length);
            _serial.BaseStream.Flush(); Thread.Sleep(100);
            return Task.FromResult(0);
        }
        public void Close()
        {
            if (_serial == null) return;
            try { try { _serial.BaseStream.Close(); } catch { } _serial.Close(); }
            finally { _serial.Dispose(); _serial = null; }
        }
        public void Dispose() { Close(); }
    }

    internal sealed class TcpTransport : ITransport
    {
        private readonly string _host; private readonly int _port; private TcpClient _client;
        public TcpTransport(string host, int port) { _host = host; _port = port; }
        public Stream Stream { get { return _client == null ? null : _client.GetStream(); } }
        public string Kind { get { return "TCP"; } }
        public string Endpoint { get { return _host + ":" + _port; } }
        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            _client = new TcpClient();
            using (cancellationToken.Register(delegate { try { _client.Close(); } catch { } }))
                await _client.ConnectAsync(_host, _port).ConfigureAwait(false);
        }
        public void Close() { if (_client != null) { _client.Close(); _client = null; } }
        public void Dispose() { Close(); }
    }
}
