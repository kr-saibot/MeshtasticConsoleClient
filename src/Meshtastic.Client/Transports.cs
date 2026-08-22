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
        bool UseSynchronousWrites { get; }
        void Write(byte[] buffer, int offset, int count);
        void Close();
    }

    internal sealed class SerialTransport : ITransport
    {
        private readonly string _port; private readonly int _baudRate; private SerialPort _serial;
        public SerialTransport(string port, int baudRate) { _port = port; _baudRate = baudRate; }
        public Stream Stream { get { return _serial == null ? null : _serial.BaseStream; } }
        public string Kind { get { return "Serial"; } }
        public string Endpoint { get { return _port + " @ " + _baudRate + " baud"; } }
        public bool UseSynchronousWrites { get { return Environment.OSVersion.Platform != PlatformID.Win32NT; } }
        public void Write(byte[] buffer, int offset, int count) { _serial.Write(buffer, offset, count); _serial.BaseStream.Flush(); }
        public Task OpenAsync(CancellationToken cancellationToken)
        {
            _serial = new SerialPort(_port, _baudRate, Parity.None, 8, StopBits.One);
            // nRF/TinyUSB USB-CDC devices use DTR to determine whether a host is ready
            // to receive data. SerialPort defaults DTR to false, which permits TX but
            // can suppress every response from these devices.
            _serial.Handshake = Handshake.None;
            _serial.DtrEnable = true;
            _serial.RtsEnable = false;
            _serial.ReadTimeout = SerialPort.InfiniteTimeout; _serial.WriteTimeout = SerialPort.InfiniteTimeout;
            _serial.Open();
            // Four START1 bytes wake the device and resynchronize its stream parser
            // before the first normally framed (0x94, 0xC3, length, protobuf) packet.
            var wake = new byte[] { 0x94, 0x94, 0x94, 0x94 };
            _serial.BaseStream.Write(wake, 0, wake.Length);
            _serial.BaseStream.Flush();
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
        public bool UseSynchronousWrites { get { return false; } }
        public void Write(byte[] buffer, int offset, int count) { Stream.Write(buffer, offset, count); Stream.Flush(); }
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
