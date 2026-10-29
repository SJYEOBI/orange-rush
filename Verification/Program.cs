using OrangeRush;
using System.Net;
using System.Net.Sockets;

if(args.Contains("--bots")) { await Bots.Run(); return; }

int checks=0;
void Check(bool condition,string label) { if(!condition) throw new Exception(label); checks++; Console.WriteLine("PASS "+label); }
var g=new GameRules(7);
int host=g.Add("Host"), b=g.Add("B"), c=g.Add("C"), d=g.Add("D");
Check(g.Add("Full")==-1,"4 player limit");
Check(!g.Command(b,"start",-1,0,0),"only host starts");
Check(g.Command(host,"start",-1,0,0),"start roulette");
Check(g.Add("Late")==-1,"late join rejected");
Check(g.State.players.Any(p=>p.id==g.State.active),"roulette selects roster member");
g.Tick(5); int first=g.State.active;
Check(g.State.phase=="Ready" && g.State.duration==7,"first turn 7 seconds");
Check(!g.Command((first+1)%4,"ready",-1,0,5),"inactive player cannot start turn");
g.Command(first,"ready",-1,0,5);
Check(!g.Command(first,"tap",-1,1,6),"countdown taps ignored");
g.Tick(8);
for(int i=1;i<=12;i++) g.Command(first,"tap",-1,i,8+i*0.1);
Check(!g.Command(first,"tap",-1,12,10),"duplicate tap ignored");
Check(!g.Command((first+1)%4,"tap",-1,13,10),"other players cannot add taps");
g.Tick(15);
Check(g.State.taps==12 && g.State.target==12 && g.State.passed,"score locks at deadline");
Check(!g.Command(first,"tap",-1,20,15.1),"late tap ignored");
g.Tick(18);
Check(!g.Command(first,"nominate",first,0,18),"cannot nominate self");
int second=(first+1)%4;
Check(g.Command(first,"nominate",second,0,18),"active player nominates opponent");
Check(g.State.duration==8 && g.State.turn==2,"second turn 8 seconds");
g.Command(second,"ready",-1,0,20);g.Tick(23);
for(int i=1;i<=12;i++) g.Command(second,"tap",-1,i,23+i*0.1);
g.Tick(31);
Check(g.State.passed,"equal score passes");g.Tick(34);
int third=(second+1)%4;
g.Command(second,"nominate",third,0,34);g.Command(third,"ready",-1,0,35);g.Tick(38);g.Tick(47);
Check(!g.State.players.First(p=>p.id==third).alive,"fewer taps eliminates player");
g.Tick(50);
Check(g.State.duration==10 && g.State.turn==4 && g.State.target==12,"elimination keeps target and adds second");
Check(!g.Command(third,"nominate",first,0,50),"eliminated player has no turn authority");
g.Disconnect(g.State.players.First(p=>p.alive && p.id!=g.State.active).id);
Check(g.State.players.Count(p=>p.alive)==2,"disconnect eliminates participant");
g.Disconnect(g.State.players.First(p=>p.alive && p.id!=g.State.active).id);
Check(g.State.phase=="Finished" && g.State.winner==g.State.active,"last player wins");
Check(!g.Command(b,"rematch",-1,0,51),"only host rematches");
var rematch=new GameRules(4);int rh=rematch.Add("Host");int rb=rematch.Add("B");rematch.Command(rh,"start",-1,0,0);rematch.Disconnect(rb);
rematch.Command(rh,"rematch",-1,0,52);
Check(rematch.State.phase=="Lobby" && rematch.State.duration==7 && rematch.State.target==0 && rematch.State.players.Length==1,"rematch resets rules and removes disconnected players");
var solo=new GameRules(2);int soloId=solo.Add("  <test>  ");
Check(!solo.Command(soloId,"start",-1,0,0),"minimum two players");
Check(solo.State.players[0].name=="test","player names sanitized");
using(var server=new NetworkRoom())
using(var client=new NetworkRoom())
{
    var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();int port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
    server.Host(port);client.Join("127.0.0.1",port,"hello");
    var limit=DateTime.UtcNow.AddSeconds(5);NetworkRoom.Incoming packet=default;bool found=false;
    while(DateTime.UtcNow<limit && !found) { server.AcceptPending();found=server.inbox.TryDequeue(out packet);Thread.Sleep(10); }
    Check(found && packet.line=="hello","real TCP client-to-host transport");
    packet.peer.welcomed=true;server.Broadcast("snapshot");found=false;
    while(DateTime.UtcNow<limit && !found) { found=client.inbox.TryDequeue(out packet);Thread.Sleep(10); }
    Check(found && packet.line=="snapshot","real TCP host-to-client transport");
    client.Dispose();found=false;
    while(DateTime.UtcNow<limit && !found) { if(server.inbox.TryDequeue(out packet)) found=packet.closed;Thread.Sleep(10); }
    Check(found,"TCP disconnect notification");
}
Console.WriteLine($"ALL {checks} CHECKS PASSED");
