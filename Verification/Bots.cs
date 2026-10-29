using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OrangeRush;

static class Bots
{
    static readonly JsonSerializerOptions options=new() { IncludeFields=true };
    public static async Task Crowd(string[] args)
    {
        int port=7777;
        int portArg=Array.IndexOf(args,"--port");if(portArg>=0) port=int.Parse(args[portArg+1]);
        Console.WriteLine("CROWD connecting 19 real TCP clients to Unity host on "+port);
        await Task.WhenAll(Enumerable.Range(1,19).Select(i=>CrowdOne(i,port)));
    }
    static async Task CrowdOne(int index,int port)
    {
        using var socket=new TcpClient();await socket.ConnectAsync("127.0.0.1",port);socket.NoDelay=true;
        using var writer=new StreamWriter(socket.GetStream(),new UTF8Encoding(false)) {AutoFlush=true};
        using var reader=new StreamReader(socket.GetStream());
        await writer.WriteLineAsync(JsonSerializer.Serialize(new Wire{type="hello",name=index==19?"스무번째오렌지친구":index==18?"열두글자닉네임테스트":"친구 "+index.ToString("00"),protocol=GameRules.ProtocolVersion},options));
        int id=-1,seq=0,readyTurn=-1,nominateTurn=-1,lastTurn=-1;string lastPhase="";double lastTap=0;
        bool rosterSeen=false;var deadline=DateTime.UtcNow.AddMinutes(6);
        while(DateTime.UtcNow<deadline)
        {
            string line=await reader.ReadLineAsync();if(line==null)break;
            var w=JsonSerializer.Deserialize<Wire>(line,options);
            if(w.type=="welcome") { id=w.id;Console.WriteLine($"CLIENT {index:00} welcomed as {id}"); }
            if(w.type=="error") throw new Exception(w.error);
            if(w.state==null)continue;
            var s=w.state;
            if(s.players.Length==20 && !rosterSeen) {rosterSeen=true;Console.WriteLine($"CLIENT {index:00} sees all 20 players");}
            if(index==1 && (lastPhase!=s.phase || lastTurn!=s.turn))
                Console.WriteLine($"UNITY mode={s.mode} state={s.phase} turn={s.turn} duration={s.duration:0.0} penalty={s.timePenalty:0.0} active={s.active} taps={s.taps} target={s.target} nomination={s.nominationStartedAt:0.000}");
            lastPhase=s.phase;lastTurn=s.turn;
            string command="ping";int target=-1;
            if(s.active==id && s.turn<=2)
            {
                if(s.phase=="Ready" && readyTurn!=s.turn) {command="ready";readyTurn=s.turn;}
                int goal=s.turn==1?(s.mode=="Hard"?2:12):s.target;
                if(s.phase=="Playing" && s.taps<goal && s.serverTime-lastTap>0.08)
                {command=s.mode=="Hard"?"typeKey":"tap";if(s.mode=="Hard")target="dhfpswl"[s.wordProgress]-'a';lastTap=s.serverTime;}
                double selectionWait=s.turn==1?5.15:0;
                if(s.phase=="Nominate" && nominateTurn!=s.turn && s.serverTime-s.nominationStartedAt>=selectionWait)
                {command="nominate";nominateTurn=s.turn;target=s.turn==2?s.host:s.players.First(p=>p.id!=id && p.id!=s.host && p.alive && p.connected).id;}
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(new Wire{type=command,target=target,sequence=++seq},options));
        }
    }
    public static async Task Run()
    {
        await Task.WhenAll(Enumerable.Range(1,3).Select(RunOne));
    }
    static async Task RunOne(int index)
    {
        using var socket=new TcpClient();await socket.ConnectAsync("127.0.0.1",7777);socket.NoDelay=true;
        using var writer=new StreamWriter(socket.GetStream(),new UTF8Encoding(false)) {AutoFlush=true};
        using var reader=new StreamReader(socket.GetStream());
        await writer.WriteLineAsync(JsonSerializer.Serialize(new Wire{type="hello",name="TEST "+index,protocol=GameRules.ProtocolVersion},options));
        int id=-1,seq=0,readyTurn=-1,nominateTurn=-1,lastTurn=-1;string lastPhase="";double lastTap=0;
        var deadline=DateTime.UtcNow.AddMinutes(4);
        while(DateTime.UtcNow<deadline)
        {
            string line=await reader.ReadLineAsync();if(line==null)break;
            var w=JsonSerializer.Deserialize<Wire>(line,options);
            if(w.type=="welcome")id=w.id;
            if(w.state==null)continue;
            var s=w.state;
            if(index==1 && (lastPhase!=s.phase || lastTurn!=s.turn)) Console.WriteLine($"UNITY state={s.phase} turn={s.turn} duration={s.duration} active={s.active} taps={s.taps} target={s.target}");
            lastPhase=s.phase;lastTurn=s.turn;
            if(s.phase=="Finished") { Console.WriteLine($"BOT {index} observed winner={s.winner}");break; }
            string command="ping";int target=-1;
            if(s.active==id)
            {
                if(s.phase=="Ready" && readyTurn!=s.turn) { command="ready";readyTurn=s.turn; }
                if(s.phase=="Playing" && s.taps<(s.target==0?6:s.turn<3?s.target:Math.Max(0,s.target-1)) && s.serverTime-lastTap>0.12)
                {
                    command=s.mode=="Hard"?"typeKey":"tap";
                    if(s.mode=="Hard") target="dhfpswl"[s.wordProgress]-'a';
                    lastTap=s.serverTime;
                }
                if(s.phase=="Nominate" && nominateTurn!=s.turn)
                { command="nominate";nominateTurn=s.turn;target=s.players.First(p=>p.id!=id && p.alive && p.connected).id; }
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(new Wire{type=command,target=target,sequence=++seq},options));
        }
    }
}
