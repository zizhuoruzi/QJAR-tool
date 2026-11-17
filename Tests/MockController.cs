using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

/* 进程内模拟控制器：与现场控制器同一套 28 字节头协议，用于回环测试，
   不接触真实设备。注册后按 Delay 指定的时间生效。 */
internal sealed class MockController : IDisposable
{
    internal const string Serial = "TEST-CONTROLLER-0000000001";
    internal const string Key = "TEST-PRIVATE-LICENSE-000001";

    private readonly TcpListener listener;
    private readonly List<Socket> clients = new List<Socket>();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly double delaySeconds;
    private readonly object gate = new object();
    private double activatedAt = -1;
    private volatile bool running = true;

    internal MockController(double delaySeconds)
    {
        this.delaySeconds = delaySeconds;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        new Thread(Accept) { IsBackground = true }.Start();
    }

    internal int Port { get; private set; }

    private int connections;
    internal int Connections { get { return connections; } }

    private void Accept()
    {
        while (running)
        {
            Socket socket;
            try { socket = listener.AcceptSocket(); }
            catch { return; }
            lock (gate) { clients.Add(socket); }
            Interlocked.Increment(ref connections);
            new Thread(() => Serve(socket)) { IsBackground = true }.Start();
        }
    }

    private void Serve(Socket socket)
    {
        var prefix = new byte[4];
        try
        {
            while (true)
            {
                Read(socket, prefix, 0);
                int length = BitConverter.ToInt32(prefix, 0);
                if (length < 30 || length > 2000028) { return; }
                var packet = new byte[length];
                Buffer.BlockCopy(prefix, 0, packet, 0, 4);
                Read(socket, packet, 4);
                int commandLength = BitConverter.ToInt32(packet, 20);
                int dataLength = BitConverter.ToInt32(packet, 24);
                if (28 + commandLength + dataLength != length) { return; }
                string command = Encoding.ASCII.GetString(packet, 28, commandLength - 1);
                string data = Encoding.ASCII.GetString(packet, 28 + commandLength, dataLength - 1);
                socket.Send(Build(command, Reply(command, data)));
            }
        }
        catch (Exception) { }
        finally
        {
            lock (gate) { clients.Remove(socket); }
            try { socket.Close(); } catch { }
        }
    }

    private string Reply(string command, string data)
    {
        switch (command)
        {
            case "getlicstate":
                return Active() ? "1,365" : "0,0";
            case "getsn":
                return Serial;
            case "getlic":
                return Key;
            case "actlicense":
                if (data != Key) { return "error"; }
                lock (gate) { activatedAt = clock.Elapsed.TotalSeconds; }
                return "ok";
            default:
                return "error";
        }
    }

    private bool Active()
    {
        lock (gate)
        {
            return activatedAt >= 0 && clock.Elapsed.TotalSeconds - activatedAt >= delaySeconds;
        }
    }

    private static void Read(Socket socket, byte[] buffer, int offset)
    {
        while (offset < buffer.Length)
        {
            int count = socket.Receive(buffer, offset, buffer.Length - offset, SocketFlags.None);
            if (count == 0) { throw new SocketException((int)SocketError.ConnectionReset); }
            offset += count;
        }
    }

    internal static byte[] Build(string command, string data)
    {
        byte[] payload = Encoding.ASCII.GetBytes(command);
        byte[] body = Encoding.ASCII.GetBytes(data);
        int length = 28 + payload.Length + 1 + body.Length + 1;
        var packet = new byte[length];
        Put(packet, 0, length);
        Put(packet, 20, payload.Length + 1);
        Put(packet, 24, body.Length + 1);
        Buffer.BlockCopy(payload, 0, packet, 28, payload.Length);
        Buffer.BlockCopy(body, 0, packet, 29 + payload.Length, body.Length);
        uint sum = 0;
        for (int i = 0; i < 4; ++i) { sum += packet[i]; }
        for (int i = 8; i < length; ++i) { sum += packet[i]; }
        Put(packet, 4, (int)sum);
        return packet;
    }

    private static void Put(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    public void Dispose()
    {
        running = false;
        try { listener.Stop(); } catch { }
        lock (gate)
        {
            foreach (Socket socket in clients) { try { socket.Close(); } catch { } }
            clients.Clear();
        }
    }
}