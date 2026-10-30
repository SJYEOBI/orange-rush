using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Networking.Transport.Utilities;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;

namespace OrangeRush
{
    // Both roles make an outbound encrypted WebSocket connection to Unity Relay.
    // Rules and sender ownership still live on the host, just as in a direct room.
    public sealed class RelayRoom : IRoomConnection
    {
        sealed class Link
        {
            public NetworkConnection connection;
            public NetworkRoom.Peer peer;
            public readonly Queue<byte[]> pending = new Queue<byte[]>();
            public byte[] latestState;
            public bool closing;
            public double closesAt;
        }
        public ConcurrentQueue<NetworkRoom.Incoming> inbox { get; } = new ConcurrentQueue<NetworkRoom.Incoming>();
        public ConcurrentQueue<string> errors { get; } = new ConcurrentQueue<string>();
        public List<NetworkRoom.Peer> peers { get; } = new List<NetworkRoom.Peer>();
        public bool IsHost { get; private set; }
        public string RoomCode { get; private set; } = "";
        readonly List<Link> links = new List<Link>();
        NetworkDriver driver;
        NetworkPipeline pipeline;
        Link server;
        string hello;
        bool disposed;
        static Task servicesReady;
        static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;

        static async Task InitializeServices()
        {
            if(UnityServices.State != ServicesInitializationState.Initialized)
            {
                var options = new InitializationOptions();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                var args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args,"--relay-profile");
                if(index >= 0 && index + 1 < args.Length) options.SetProfile(args[index + 1]);
#endif
                await UnityServices.InitializeAsync(options);
            }
            if(!AuthenticationService.Instance.IsSignedIn) await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }
        async Task Ready()
        {
            if(servicesReady == null || servicesReady.IsFaulted || servicesReady.IsCanceled) servicesReady = InitializeServices();
            await servicesReady;
            if(disposed) throw new OperationCanceledException();
        }
        public static string NormalizeCode(string value) => (value ?? "").Trim().ToUpperInvariant();
        public async Task HostAsync()
        {
            await Ready();
            var allocation = await RelayService.Instance.CreateAllocationAsync(GameRules.MaxPlayers - 1);
            if(disposed) throw new OperationCanceledException();
            StartDriver(AllocationUtils.ToRelayServerData(allocation,"wss"),true);
            RoomCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            if(disposed) throw new OperationCanceledException();
            IsHost = true;
        }
        public async Task JoinAsync(string code,string greeting)
        {
            await Ready();
            var allocation = await RelayService.Instance.JoinAllocationAsync(NormalizeCode(code));
            if(disposed) throw new OperationCanceledException();
            var relay = AllocationUtils.ToRelayServerData(allocation,"wss");
            StartDriver(relay,false);
            hello = greeting;
            server = AddLink(driver.Connect(relay.Endpoint));
            RoomCode = NormalizeCode(code);
        }
        void StartDriver(RelayServerData relay,bool host)
        {
            var settings = new NetworkSettings();
            try
            {
                settings.WithRelayParameters(ref relay);
                settings.WithNetworkConfigParameters(connectTimeoutMS:750,maxConnectAttempts:16,disconnectTimeoutMS:15000,heartbeatTimeoutMS:500,sendQueueCapacity:1024,receiveQueueCapacity:1024);
                settings.WithFragmentationStageParameters(payloadCapacity:RelayPacketCodec.MaxBytes + 1);
                settings.WithReliableStageParameters(windowSize:64);
                driver = NetworkDriver.Create(new WebSocketNetworkInterface(),settings);
            }
            finally { settings.Dispose(); }
            pipeline = driver.CreatePipeline(typeof(FragmentationPipelineStage),typeof(ReliableSequencedPipelineStage));
            if(driver.Bind(NetworkEndpoint.AnyIpv4) != 0 || (host && driver.Listen() != 0)) throw new InvalidOperationException("중계 서버 연결을 시작하지 못했어요.");
        }
        Link AddLink(NetworkConnection connection)
        {
            var link = new Link {connection = connection};
            link.peer = new NetworkRoom.Peer(line => Queue(link,RelayPacketCodec.Encode(line),false),() => RequestClose(link));
            links.Add(link); peers.Add(link.peer);
            return link;
        }
        void Queue(Link link,byte[] packet,bool state)
        {
            if(disposed || link.closing) return;
            if(state) { link.latestState = packet; return; }
            if(link.pending.Count >= 256) { RequestClose(link); return; }
            link.pending.Enqueue(packet);
        }
        void RequestClose(Link link)
        { if(!link.closing) { link.closing = true; link.closesAt = Now + 0.25; } }
        void Closed(Link link)
        {
            if(!links.Remove(link)) return;
            inbox.Enqueue(new NetworkRoom.Incoming {peer=link.peer,closed=true});
        }
        public void AcceptPending()
        {
            if(disposed || !driver.IsCreated) return;
            driver.ScheduleUpdate().Complete();
            if(driver.GetRelayConnectionStatus() == RelayConnectionStatus.AllocationInvalid)
            { errors.Enqueue("중계 서버 연결이 끊어졌어요. 새 방을 만들거나 다시 참가해주세요."); Dispose(); return; }
            if(IsHost)
            {
                NetworkConnection connection;
                while((connection = driver.Accept()).IsCreated)
                {
                    if(peers.Count >= GameRules.MaxPlayers - 1) driver.Disconnect(connection);
                    else AddLink(connection);
                }
            }
            foreach(var link in links.ToArray())
            {
                NetworkEvent.Type kind;
                while((kind = driver.PopEventForConnection(link.connection,out var reader)) != NetworkEvent.Type.Empty)
                {
                    if(kind == NetworkEvent.Type.Connect && link == server) Queue(link,RelayPacketCodec.Encode(hello),false);
                    else if(kind == NetworkEvent.Type.Disconnect) { Closed(link); break; }
                    else if(kind == NetworkEvent.Type.Data)
                    {
                        try
                        {
                            if(reader.Length > RelayPacketCodec.MaxBytes + 1) throw new InvalidOperationException();
                            using(var bytes = new NativeArray<byte>(reader.Length,Allocator.Temp))
                            {
                                reader.ReadBytes(bytes);
                                inbox.Enqueue(new NetworkRoom.Incoming {peer=link.peer,line=RelayPacketCodec.Decode(bytes.ToArray())});
                            }
                        }
                        catch { RequestClose(link); }
                    }
                }
                if(!links.Contains(link)) continue;
                int budget = 64;
                while(link.pending.Count > 0 && budget-- > 0)
                { if(!TrySend(link,link.pending.Peek())) break; link.pending.Dequeue(); }
                if(link.pending.Count == 0 && link.latestState != null && TrySend(link,link.latestState)) link.latestState = null;
                if(link.closing && Now >= link.closesAt)
                { driver.Disconnect(link.connection); Closed(link); }
            }
            driver.ScheduleFlushSend(default).Complete();
        }
        bool TrySend(Link link,byte[] packet)
        {
            if(link.connection.GetState(driver) != NetworkConnection.State.Connected) return false;
            if(driver.BeginSend(pipeline,link.connection,out var writer,packet.Length) != 0) return false;
            using(var bytes = new NativeArray<byte>(packet,Allocator.Temp)) writer.WriteBytes(bytes);
            return driver.EndSend(writer) >= 0;
        }
        public void Send(string line) { if(server != null) Queue(server,RelayPacketCodec.Encode(line),false); }
        public void Broadcast(string line)
        {
            byte[] packet = RelayPacketCodec.Encode(line);
            bool state = line.StartsWith("{\"type\":\"state\"",StringComparison.Ordinal);
            foreach(var link in links) if(link.peer.welcomed) Queue(link,packet,state);
        }
        public void Dispose()
        {
            if(disposed) return;
            disposed = true;
            if(driver.IsCreated) { driver.ScheduleUpdate().Complete(); driver.Dispose(); }
            links.Clear(); peers.Clear();
        }
        public static string FriendlyError(Exception error)
        {
            if(error is OperationCanceledException) return "접속을 취소했어요.";
            if(error is RelayServiceException relay)
            {
                if(relay.Reason == RelayExceptionReason.JoinCodeNotFound || relay.Reason == RelayExceptionReason.AllocationNotFound || relay.Reason == RelayExceptionReason.EntityNotFound)
                    return "방 코드를 찾을 수 없어요. 코드와 방장 접속 상태를 확인해주세요.";
                if(relay.Reason == RelayExceptionReason.InactiveProject) return "온라인 서비스 설정을 확인해야 해요. 최신 실행판으로 업데이트해주세요.";
                if(relay.Reason == RelayExceptionReason.PaymentRequired) return "온라인 서비스의 사용 한도를 확인해야 해요. 잠시 후 다시 시도해주세요.";
                return "방에 연결하지 못했어요. 방 코드·정원·인터넷 연결을 확인해주세요.";
            }
            return "온라인 연결을 준비하지 못했어요. 인터넷 연결을 확인하고 다시 시도해주세요.";
        }
    }
}
