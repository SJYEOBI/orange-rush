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

var hard=new GameRules(7);int hh=hard.Add("Host"), hb=hard.Add("Guest");
Check(!hard.Command(hb,"mode",1,0,0),"only host changes difficulty");
Check(!hard.Command(hh,"mode",99,0,0),"invalid difficulty rejected");
Check(hard.Command(hh,"mode",1,0,0) && hard.State.mode=="Hard","host selects hard mode in lobby");
hard.Command(hh,"start",-1,0,0);hard.Tick(5);int ha=hard.State.active;
Check(hard.State.duration==7,"hard mode also starts at seven seconds");
Check(!hard.Command(hh,"mode",0,0,5) && hard.State.mode=="Hard","difficulty locked during match");
hard.Command(ha,"ready",-1,0,5);
Check(!hard.Command(ha,"typeKey",3,1,6),"hard typing before countdown ignored");
hard.Tick(8);
Check(!hard.Command(ha,"tap",-1,1,8.1),"space taps cannot score in hard mode");
Check(!hard.Command(ha,"word",-1,1,8.1),"completed word payload cannot score in hard mode");
Check(!hard.Command(1-ha,"typeKey",3,1,8.1),"inactive player cannot type for opponent");
Check(!hard.Command(ha,"typeKey",100,1,8.1),"invalid key packet rejected");
int hs=0;
void HardKey(char key,double time) { hard.Command(ha,"typeKey",key-'a',++hs,time); }
HardKey('d',8.2);HardKey('h',8.3);HardKey('f',8.4);
Check(hard.State.taps==0 && hard.State.wordProgress==3,"partial word does not score");
hard.Command(ha,"typeKey",WordTyping.Backspace,++hs,8.5);
Check(hard.State.wordProgress==2 && WordTyping.Preview(2)=="오","backspace edits progress without score");
HardKey('z',8.6);
Check(hard.State.wordProgress==0 && hard.State.taps==0,"wrong letter resets word without score");
foreach(char k in "dhfpswl") HardKey(k,9);
Check(hard.State.taps==1 && hard.State.wordProgress==0,"exact seven keys score one orange automatically");
Check(!hard.Command(ha,"typeKey",3,hs,9.1),"replayed typing packet rejected");
foreach(char k in "dhfpswl") HardKey(k,10);
Check(hard.State.taps==2,"consecutive words score independently");
foreach(char k in "dhfpsw") HardKey(k,14);
Check(!hard.Command(ha,"typeKey",11,++hs,15) && hard.State.taps==2,"unfinished word cannot score at or after deadline");
hard.Tick(18);int hn=1-ha;hard.Command(ha,"nominate",hn,0,18);
Check(hard.State.mode=="Hard" && hard.State.duration==9 && hard.State.target==2 && hard.State.wordProgress==0,"hard second turn adds two seconds and preserves target");
hard.Command(hn,"ready",-1,0,20);hard.Tick(23);hs=0;
foreach(char k in "dhfpswldhfpswl") hard.Command(hn,"typeKey",k-'a',++hs,24);
hard.Tick(32);
Check(hard.State.passed && hard.State.taps==2,"equal word count passes in hard mode");
hard.Tick(35);hard.Command(hn,"nominate",ha,0,35);
Check(hard.State.duration==11,"hard third turn is eleven seconds");
hard.Command(ha,"ready",-1,0,36);hard.Tick(39);hard.Tick(50);hard.Tick(53);
Check(hard.State.phase=="Finished" && hard.State.winner==hn,"fewer words eliminates and awards last survivor");
hard.Command(hh,"rematch",-1,0,54);
Check(hard.State.mode=="Hard" && hard.State.phase=="Lobby" && hard.State.target==0 && hard.State.duration==7,"rematch preserves selected difficulty and resets score");
Check(hard.Command(hh,"mode",0,0,55) && hard.State.mode=="Normal","host can switch back to normal in lobby");
hard.Command(hh,"start",-1,0,56);hard.Tick(61);hard.Command(hard.State.active,"ready",-1,0,61);hard.Tick(64);
Check(!hard.Command(hard.State.active,"typeKey",3,1,65),"typing commands cannot score in normal mode");
var hardFour=new GameRules(3);for(int i=0;i<4;i++) hardFour.Add("P"+i);
hardFour.Command(0,"mode",1,0,0);hardFour.Command(0,"start",-1,0,0);hardFour.Tick(5);
int hfa=hardFour.State.active;hardFour.Command(hfa,"ready",-1,0,5);hardFour.Tick(8);int hfs=0;
foreach(char k in "dhfpswl") hardFour.Command(hfa,"typeKey",k-'a',++hfs,9);
hardFour.Tick(15);hardFour.Tick(18);int hfb=hardFour.State.players.First(p=>p.id!=hfa).id;
hardFour.Command(hfa,"nominate",hfb,0,18);hardFour.Command(hfb,"ready",-1,0,20);hardFour.Tick(23);hardFour.Tick(32);hardFour.Tick(35);
Check(hardFour.State.duration==11 && hardFour.State.target==1,"hard elimination also adds exactly two seconds");
hardFour.Command(hardFour.State.active,"ready",-1,0,36);hardFour.Tick(39);hardFour.Tick(50);hardFour.Tick(53);
Check(hardFour.State.duration==13 && hardFour.State.turn==4,"hard fourth turn is thirteen seconds");
var wireOptions=new System.Text.Json.JsonSerializerOptions { IncludeFields=true };
var serialized=System.Text.Json.JsonSerializer.Serialize(new Wire {type="state",protocol=GameRules.ProtocolVersion,state=new Snapshot {mode="Hard",wordProgress=5}},wireOptions);
var roundTrip=System.Text.Json.JsonSerializer.Deserialize<Wire>(serialized,wireOptions);
Check(roundTrip.protocol==2 && roundTrip.state.mode=="Hard" && roundTrip.state.wordProgress==5,"difficulty progress and protocol survive wire serialization");
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
