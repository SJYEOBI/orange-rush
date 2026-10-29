using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OrangeRush
{
    // Newline framed JSON over TCP. Worker threads only enqueue; Unity owns game state.
    public sealed class NetworkRoom : IDisposable
    {
        public sealed class Peer : IDisposable
        {
            public readonly TcpClient socket;
            public readonly StreamWriter writer;
            public int id = -1;
            public bool welcomed;
            public Peer(TcpClient client)
            {
                socket = client; socket.NoDelay = true; socket.ReceiveTimeout = 15000; socket.SendTimeout = 500;
                writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            }
            public void Send(string line) { lock (writer) writer.WriteLine(line); }
            public void Dispose() { try { socket.Close(); } catch { } }
        }
        public struct Incoming { public Peer peer; public string line; public bool closed; }
        public readonly ConcurrentQueue<Incoming> inbox = new ConcurrentQueue<Incoming>();
        public readonly ConcurrentQueue<string> errors = new ConcurrentQueue<string>();
        public readonly List<Peer> peers = new List<Peer>();
        readonly ConcurrentQueue<Peer> accepted = new ConcurrentQueue<Peer>();
        TcpListener listener;
        Peer server;
        volatile bool stopped;
        TcpClient connecting;
        public bool IsHost { get; private set; }
        public bool Connected { get { return server != null; } }
        public void Host(int port)
        {
            listener = new TcpListener(IPAddress.Any, port); listener.Start(GameRules.MaxPlayers); IsHost = true;
            Task.Run(async () =>
            {
                while (!stopped)
                {
                    try { var p = new Peer(await listener.AcceptTcpClientAsync()); if(stopped) { p.Dispose(); break; } accepted.Enqueue(p); }
                    catch (Exception e) { if (!stopped) errors.Enqueue(e.Message); break; }
                }
            });
        }
        public void Join(string address, int port, string hello)
        {
            Task.Run(async () =>
            {
                try
                {
                    connecting = new TcpClient();
                    var connect = connecting.ConnectAsync(address, port);
                    if (await Task.WhenAny(connect, Task.Delay(8000)) != connect) throw new TimeoutException("접속 시간이 초과됐어요. 주소와 방화벽을 확인하세요.");
                    await connect;
                    if (stopped) { connecting.Close(); return; }
                    var p = new Peer(connecting); server = p; p.Send(hello); Read(p);
                }
                catch (Exception e) { if (!stopped) errors.Enqueue(e.Message); connecting?.Close(); }
            });
        }
        public void AcceptPending()
        {
            while (accepted.TryDequeue(out var p))
            {
                // The host occupies one of the twenty player slots.
                if (peers.Count >= GameRules.MaxPlayers - 1) { p.Dispose(); continue; }
                peers.Add(p); Read(p);
            }
        }
        void Read(Peer p)
        {
            Task.Run(() =>
            {
                try
                {
                    // Bounded input prevents a malformed peer from allocating unbounded memory.
                    using (var reader = new StreamReader(p.socket.GetStream(), Encoding.UTF8))
                    {
                        var line = new StringBuilder(); int c;
                        while (!stopped && (c = reader.Read()) != -1)
                        {
                            if (c == '\n') { inbox.Enqueue(new Incoming { peer = p, line = line.ToString() }); line.Clear(); }
                            else if (c != '\r') { line.Append((char)c); if (line.Length > 16384) break; }
                        }
                    }
                }
                catch { }
                finally { inbox.Enqueue(new Incoming { peer = p, closed = true }); p.Dispose(); }
            });
        }
        public void Send(string line)
        { try { server?.Send(line); } catch { errors.Enqueue("방장과의 연결이 끊어졌어요."); } }
        public void Broadcast(string line)
        {
            foreach (var p in peers.ToArray()) if (p.welcomed)
                try { p.Send(line); } catch { p.Dispose(); }
        }
        public void Dispose()
        {
            stopped = true; listener?.Stop(); connecting?.Close(); server?.Dispose();
            foreach (var p in peers) p.Dispose();
            while (accepted.TryDequeue(out var p)) p.Dispose();
            peers.Clear();
        }
    }
}
