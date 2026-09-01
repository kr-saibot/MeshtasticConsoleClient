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
        public Stream Stream
        {
            get { var serial = Volatile.Read(ref _serial); return serial == null ? null : serial.BaseStream; }
        }
        public string Kind { get { return "Serial"; } }
        public string Endpoint { get { return _port + " @ " + _baudRate + " baud"; } }
        public bool UseSynchronousWrites { get { return Environment.OSVersion.Platform != PlatformID.Win32NT; } }
        public void Write(byte[] buffer, int offset, int count)
        {
            var serial = Volatile.Read(ref _serial);
            if (serial == null) throw new IOException("The serial transport is closed.");
            serial.Write(buffer, offset, count);
            serial.BaseStream.Flush();
        }
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
            var serial = Interlocked.Exchange(ref _serial, null);
            if (serial == null) return;
            try { try { serial.BaseStream.Close(); } catch { } serial.Close(); }
            finally { serial.Dispose(); }
        }
        public void Dispose() { Close(); }
    }

    internal sealed class TcpTransport : ITransport
    {
        private readonly string _host; private readonly int _port; private TcpClient _client;
        public TcpTransport(string host, int port) { _host = host; _port = port; }
        public Stream Stream
        {
            get { var client = Volatile.Read(ref _client); return client == null ? null : client.GetStream(); }
        }
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
        public void Close() { var client = Interlocked.Exchange(ref _client, null); if (client != null) client.Close(); }
        public void Dispose() { Close(); }
    }
}
