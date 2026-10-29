using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace OrangeRush
{
    public sealed class OrangeGame : MonoBehaviour
    {
        static readonly Color Bg = Hex("10151E"), Panel = Hex("1B2230"), Muted = Hex("909BAC"), Orange = Hex("FF9038"), Cream = Hex("FFF3DB"), Green = Hex("B9EE89");
        readonly Color[] colors = { Hex("FF9A4A"), Hex("B4C3FF"), Hex("B9EE89"), Hex("FFB2CB") };
        NetworkRoom network;
        GameRules rules;
        Snapshot state;
        int myId = -1, sequence;
        int localWordProgress, inputTurn = -1;
        string inputPhase = "", typingHint = "오렌지를 직접 타이핑하세요";
        string nickname = "", address = "127.0.0.1", portText = "7777", error = "", localIP = "";
        double clockOffset, nextBroadcast, nextHeartbeat;
        float lastPulse = -10;
        bool connecting, mute;
        Font font;
        Texture2D rounded, disc, orangeArt;
        GUIStyle text, field;
        AudioSource audioSource;
        AudioClip click;
        double Now { get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0; } }
        bool Mine { get { return state != null && state.active == myId; } }
        bool Hosting { get { return network != null && network.IsHost; } }
        bool Hard { get { return state != null && state.mode == "Hard"; } }
        string ScoreUnit { get { return Hard ? "회" : "타"; } }
        string ScoreLabel { get { return Hard ? "WORDS" : "TAPS"; } }
        int TimeStep { get { return GameRules.TurnStep(state?.mode); } }
        double ServerNow { get { return Now+(Hosting?0:clockOffset); } }
        double NextDuration { get { return GameRules.NextDuration(state,ServerNow); } }
        static string Seconds(double value) { return value.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture); }
        string PlayerName(int id) { return state?.players.FirstOrDefault(p => p.id == id)?.name ?? "PLAYER"; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() { if (FindFirstObjectByType<OrangeGame>() == null) new GameObject("Orange Rush").AddComponent<OrangeGame>(); }
        void Awake()
        {
            Application.runInBackground = true; Application.targetFrameRate = 120;
            nickname = "오렌지" + UnityEngine.Random.Range(10,99);
            font = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "Apple SD Gothic Neo", "Noto Sans CJK KR", "Arial" }, 24);
            rounded = MakeTexture(48, (x,y) => { float dx = Mathf.Max(Mathf.Abs(x-23.5f)-12,0), dy = Mathf.Max(Mathf.Abs(y-23.5f)-12,0); return new Color(1,1,1,Mathf.Clamp01(12-Mathf.Sqrt(dx*dx+dy*dy))); });
            disc = MakeTexture(128, (x,y) => new Color(1,1,1,Mathf.Clamp01(63-Mathf.Sqrt((x-63.5f)*(x-63.5f)+(y-63.5f)*(y-63.5f)))));
            orangeArt = MakeTexture(512, OrangePixel);
            audioSource = gameObject.AddComponent<AudioSource>();
            var samples = new float[2205]; for(int i=0;i<samples.Length;i++) samples[i] = Mathf.Sin(i*0.19f)*0.12f*Mathf.Pow(1-i/(float)samples.Length,3);
            click = AudioClip.Create("Juice pop", samples.Length,1,44100,false); click.SetData(samples,0);
            try { localIP = string.Join(" / ", Dns.GetHostAddresses(Dns.GetHostName()).Where(a=>a.AddressFamily==AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Select(a=>a.ToString())); } catch { localIP = "네트워크 주소 확인 필요"; }
        }
        static Color Hex(string h) { ColorUtility.TryParseHtmlString("#"+h,out var c); return c; }
        static Texture2D MakeTexture(int size, Func<float,float,Color> pixel)
        { var t = new Texture2D(size,size,TextureFormat.RGBA32,false); var a=new Color[size*size]; for(int y=0;y<size;y++) for(int x=0;x<size;x++) a[y*size+x]=pixel(x,y); t.SetPixels(a); t.Apply(); return t; }
        static Color OrangePixel(float x,float y)
        {
            float dx=x-255.5f,dy=y-255.5f,r=Mathf.Sqrt(dx*dx+dy*dy);
            if(r>250) return Color.clear;
            if(r>231) return Hex("FF9A38");
            if(r>216) return Hex("FFF0C4");
            float a=Mathf.Atan2(dy,dx)+0.16f, slice=Mathf.Abs(Mathf.Sin(a*5));
            if(slice*r<8 || r<15) return Hex("FFF0C4");
            float grain = Mathf.Sin(x*0.73f+y*1.23f)*Mathf.Sin(y*0.33f-x*0.71f)*0.035f;
            return Color.Lerp(Hex("FFB34C"),Hex("F57526"), r/300+grain);
        }
        void Update()
        {
            if (network != null)
            {
                network.AcceptPending();
                int budget=1000;
                while(network!=null && budget-->0 && network.inbox.TryDequeue(out var incoming)) Receive(incoming);
                if(network==null) return;
                if(network.errors.TryDequeue(out var problem)) { error=problem; if(connecting) Leave(false); }
                if(Hosting)
                {
                    rules.Tick(Now); state=rules.State;
                    if(Now>=nextBroadcast) { nextBroadcast=Now+0.05; network.Broadcast(JsonUtility.ToJson(new Wire { type="state", state=state })); }
                }
                else if(state!=null && Now>=nextHeartbeat) { nextHeartbeat=Now+1; Command("ping"); }
            }
            if (state == null) { Input.imeCompositionMode = IMECompositionMode.Auto; return; }
            if (inputTurn != state.turn || inputPhase != state.phase)
            {
                inputTurn = state.turn; inputPhase = state.phase; localWordProgress = 0;
                typingHint = "오렌지를 직접 타이핑하세요";
            }
            // Hard mode has no editable text control and never reads text/clipboard/IME strings.
            // Only physical key-down edges can become scoring messages.
            Input.imeCompositionMode = Hard && state.phase != "Lobby" ? IMECompositionMode.Off : IMECompositionMode.Auto;
            if (!Mine || state.phase != "Playing" || Remaining() <= 0) return;
            if (!Hard && Input.GetKeyDown(KeyCode.Space))
            { Command("tap",-1,++sequence); Pulse(); }
            if (Hard) ReadTypingKeys();
        }
        void Pulse() { lastPulse=Time.unscaledTime; if(!mute) audioSource.PlayOneShot(click); }
        void ReadTypingKeys()
        {
            // Ignore complete shortcut chords, including Ctrl+V and Shift+Insert.
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) ||
                Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand) ||
                Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return;
            int key = -1, count = 0;
            for (int i=0; i<26; i++) if(Input.GetKeyDown((KeyCode)((int)KeyCode.A+i))) { key=i; count++; }
            if(Input.GetKeyDown(KeyCode.Backspace)) { key=WordTyping.Backspace; count++; }
            if(Input.GetKeyDown(KeyCode.Space)) { key=25; count++; }
            if(count==0) return;
            // A burst of simultaneous letters isn't sequential typing. Reset it consistently.
            if(count>1) key=25;
            var result = WordTyping.Advance(ref localWordProgress,key);
            Command("typeKey",key,++sequence);
            typingHint = result == WordKeyResult.Completed ? "좋아! 오렌지 +1회" : result == WordKeyResult.Mistake ? "앗, 오타! 다시 오렌지를 입력해요" : "좋아요, 끝까지 입력하세요";
            if(result == WordKeyResult.Completed) Pulse();
        }
        void Receive(NetworkRoom.Incoming incoming)
        {
            if(incoming.closed)
            {
                if(Hosting) { rules.Disconnect(incoming.peer.id); network.peers.Remove(incoming.peer); }
                else { error="방장과의 연결이 종료됐습니다. 새 방으로 다시 접속하세요."; Leave(false); }
                return;
            }
            Wire w;
            try { w=JsonUtility.FromJson<Wire>(incoming.line); } catch { incoming.peer.Dispose(); return; }
            if(w==null) return;
            if(Hosting)
            {
                if(!incoming.peer.welcomed)
                {
                    if(w.type!="hello") { incoming.peer.Dispose(); return; }
                    if(w.protocol!=GameRules.ProtocolVersion)
                    {
                        try { incoming.peer.Send(JsonUtility.ToJson(new Wire { type="error",error="게임 버전이 다릅니다. 모두 v"+GameRules.Version+" 실행판으로 업데이트하세요." })); } catch {}
                        incoming.peer.Dispose(); return;
                    }
                    int id=rules.Add(w.name);
                    if(id<0) { try { incoming.peer.Send(JsonUtility.ToJson(new Wire { type="error",error="방이 가득 찼거나 이미 게임이 진행 중입니다." })); } catch {} incoming.peer.Dispose(); return; }
                    incoming.peer.id=id; incoming.peer.welcomed=true;
                    incoming.peer.Send(JsonUtility.ToJson(new Wire { type="welcome", id=id,protocol=GameRules.ProtocolVersion }));
                }
                else rules.Command(incoming.peer.id,w.type,w.target,w.sequence,Now);
            }
            else
            {
                if(w.type=="welcome")
                {
                    if(w.protocol!=GameRules.ProtocolVersion) { error="방장과 게임 버전이 다릅니다. 모두 v"+GameRules.Version+"으로 업데이트하세요."; Leave(false); return; }
                    myId=w.id; connecting=false;
                }
                if(w.type=="error") { error=w.error; Leave(false); }
                if(w.type=="state" && w.state!=null) { state=w.state; clockOffset=state.serverTime-Now; connecting=false; }
            }
        }
        void Command(string command,int target=-1,int seq=0)
        {
            if(Hosting) rules.Command(myId,command,target,seq,Now);
            else network?.Send(JsonUtility.ToJson(new Wire { type=command,target=target,sequence=seq }));
        }
        void Connect(bool host)
        {
            if(!int.TryParse(portText,out int port) || port<1024 || port>65535) { error="포트는 1024~65535 사이의 숫자를 입력하세요."; return; }
            error=""; sequence=0; network=new NetworkRoom();
            try
            {
                if(host) { network.Host(port); rules=new GameRules(); myId=rules.Add(nickname); state=rules.State; }
                else { connecting=true; network.Join(address.Trim(),port,JsonUtility.ToJson(new Wire { type="hello",name=nickname,protocol=GameRules.ProtocolVersion })); }
            }
            catch(Exception e) { error="방을 열지 못했습니다: "+e.Message; Leave(false); }
        }
        void Leave(bool clearError=true)
        { network?.Dispose(); network=null; state=null; rules=null; myId=-1; connecting=false; clockOffset=0; if(clearError) error=""; }
        void OnDestroy() { network?.Dispose(); Input.imeCompositionMode=IMECompositionMode.Auto; }
        double Remaining() { return Math.Max(0,state.endsAt-(Now+(Hosting?0:clockOffset))); }
        void OnGUI()
        {
            if(font==null) return;
            GUI.matrix=Matrix4x4.TRS(new Vector3((Screen.width-1440*Mathf.Min(Screen.width/1440f,Screen.height/900f))/2,(Screen.height-900*Mathf.Min(Screen.width/1440f,Screen.height/900f))/2),Quaternion.identity,Vector3.one*Mathf.Min(Screen.width/1440f,Screen.height/900f));
            text=new GUIStyle(GUI.skin.label) { font=font,richText=false,alignment=TextAnchor.MiddleLeft,padding=new RectOffset(0,0,0,0) };
            field=new GUIStyle(GUI.skin.textField) { font=font,fontSize=22,padding=new RectOffset(16,16,10,10),alignment=TextAnchor.MiddleLeft };
            Box(new Rect(-1000,-1000,4000,3000),Bg);
            Label(new Rect(48,28,310,45),"●  ORANGE RUSH",25,Cream,true);
            Label(new Rect(48,70,400,22),"오렌지 게임  /  ONLINE PARTY",12,Muted);
            if(Button(new Rect(1150,37,90,38),mute?"소리 OFF":"소리 ON",Panel,Cream,13)) mute=!mute;
            if(state!=null && Button(new Rect(1250,37,140,38),"방 나가기  ↗",Panel,Muted,13)) Leave();
            if(state==null) ConnectionUI(); else RoomUI();
            Label(new Rect(48,853,1050,25),(Hard?"HARD · 오렌지 직접 타이핑 · 복붙 불가":"NORMAL · SPACE를 눌렀다 떼며 연타")+"  ·  동점 통과  ·  매 턴 +"+TimeStep+"초  ·  마지막 1명 우승",13,Muted);
            Label(new Rect(1150,853,240,25),"2–20 PLAYERS  /  v"+GameRules.Version,12,Muted,false,TextAnchor.MiddleRight);
            GUI.matrix=Matrix4x4.identity;
        }
        void ConnectionUI()
        {
            Label(new Rect(80,168,680,35),"작은 오렌지. 엄청난 승부욕.",18,Green,true);
            Label(new Rect(76,211,750,170),"터질 때까지,\n두드려.",76,Cream,true);
            Label(new Rect(82,408,650,80),"스페이스바 연타? 오렌지 타이핑? 난이도를 고르고\n친구와 겨뤄요. 너의 기록을 못 넘으면 탈락!",21,Muted);
            DrawOrange(new Rect(145,529,260,260),-12);
            Label(new Rect(442,560,310,40),"01   랜덤 룰렛으로 시작",16,Cream,true);
            Label(new Rect(442,610,310,40),"02   기본 +1초 · 하드 +2초",16,Cream,true);
            Label(new Rect(442,660,310,40),"03   마지막까지 살아남기",16,Cream,true);
            Box(new Rect(850,145,510,651),Panel);
            Label(new Rect(890,179,420,46),"오늘의 스피드왕은?",28,Cream,true);
            Label(new Rect(890,239,420,28),"플레이어 이름",14,Muted);
            nickname=GUI.TextField(new Rect(890,275,430,54),nickname,12,field);
            Label(new Rect(890,347,315,26),"방장 IP / 도메인",14,Muted);
            Label(new Rect(1210,347,115,26),"포트",14,Muted);
            address=GUI.TextField(new Rect(890,382,300,54),address,128,field);
            portText=GUI.TextField(new Rect(1205,382,115,54),portText,5,field);
            GUI.enabled=!connecting;
            if(Button(new Rect(890,465,430,60),"새 방 만들기   +",Orange,Bg,19)) Connect(true);
            if(Button(new Rect(890,540,430,60),"친구 방 참가하기   →",Hex("2B3545"),Cream,19)) Connect(false);
            GUI.enabled=true;
            Label(new Rect(890,619,430,64),connecting?"친구의 방에 연결 중…":"다른 네트워크에서는 방장의 공인 주소와\nTCP 포트 개방이 필요합니다. 안내서를 확인하세요.",13,Muted);
            if(connecting && Button(new Rect(890,695,120,36),"접속 취소",Bg,Cream,13)) Leave();
            Label(new Rect(890,736,430,48),error,12,Hex("FFA7A7"));
        }
        void RoomUI()
        {
            bool lobby=state.phase=="Lobby";
            Label(new Rect(50,116,900,30),lobby?"THE LOBBY  /  친구를 기다리는 중":"LIVE MATCH  /  ROUND "+state.turn.ToString("00")+"  /  "+(Hard?"HARD · 오렌지 타이핑":"NORMAL · 스페이스 연타"),16,Hard?Hex("FFB2CB"):Green,true);
            Label(new Rect(1040,116,350,30),(Hosting?"HOST":"CONNECTED")+"  ●  "+state.players.Length+" / "+GameRules.MaxPlayers,13,Green,false,TextAnchor.MiddleRight);
            Box(new Rect(48,166,950,636),Panel);
            Box(new Rect(1020,166,372,636),Panel);
            Label(new Rect(1048,189,310,35),"PLAYERS   ·   "+state.players.Count(p=>p.alive && p.connected)+"명 생존",15,Muted,true);
            bool compact=state.players.Length>4;
            for(int i=0;i<state.players.Length;i++)
                PlayerCard(state.players[i],i,compact?new Rect(1034+(i%2)*176,244+(i/2)*53,168,47):new Rect(1040,245+i*119,332,100),compact);
            if(compact) Label(new Rect(1040,776,332,20),"이름에 마우스를 올리면 전체 이름 표시",10,Muted,false,TextAnchor.MiddleCenter);
            if(lobby) Lobby(); else Match();
        }
        void Lobby()
        {
            DrawOrange(new Rect(450,193,145,145),Mathf.Sin(Time.unscaledTime)*8);
            Label(new Rect(100,352,850,54),"오늘은 어떤 오렌지?",33,Cream,true,TextAnchor.MiddleCenter);
            Label(new Rect(100,410,850,35),Hosting?"2~20명 온라인 대결! 난이도를 고르고 시작하세요.":"방장이 난이도를 고르고 있어요. 선택은 모두에게 적용됩니다.",16,Muted,false,TextAnchor.MiddleCenter);
            ModeCard(new Rect(90,475,421,112),false,"NORMAL · 기본","스페이스 연타 = 1타 · 매 턴 +1초",Orange);
            ModeCard(new Rect(534,475,421,112),true,"HARD · 하드","오렌지 완성 = 1회 · 매 턴 +2초",Hex("FFB2CB"));
            Label(new Rect(100,598,850,34),Hard?"7 → 9 → 11초 · 두벌식 오렌지 타이핑 · Enter 불필요 · 복붙 불가":"7 → 8 → 9초 · 스페이스 연타 · 룰렛으로 첫 주자 선정",15,Hard?Hex("FFB2CB"):Muted,false,TextAnchor.MiddleCenter);
            if(Hosting)
            {
                GUI.enabled=state.players.Length>=2;
                if(Button(new Rect(290,661,466,65),"룰렛 돌리고 시작하기   ↻",Hard?Hex("FFB2CB"):Orange,Bg,22)) Command("start");
                GUI.enabled=true;
                Label(new Rect(100,744,850,37),"내부 IP: "+localIP+"   /   포트 "+portText,13,Muted,false,TextAnchor.MiddleCenter);
            }
            else Label(new Rect(170,669,700,55),"방장이 시작하면 룰렛이 돌아갑니다.",20,Hard?Hex("FFB2CB"):Orange,false,TextAnchor.MiddleCenter);
        }
        void ModeCard(Rect r,bool hard,string title,string description,Color accent)
        {
            bool selected=Hard==hard;
            Box(r,selected?Color.Lerp(Panel,accent,0.17f):Hex("242D3D"));
            Box(new Rect(r.x,r.y,5,r.height),selected?accent:Hex("344157"));
            Label(new Rect(r.x+21,r.y+15,300,34),title,21,selected?accent:Cream,true);
            Label(new Rect(r.x+21,r.y+64,r.width-30,30),description,14,Muted);
            Label(new Rect(r.x+r.width-82,r.y+20,65,25),selected?"● 선택":"",12,accent,false,TextAnchor.MiddleRight);
            if(Hosting && GUI.Button(r,GUIContent.none,GUIStyle.none)) Command("mode",hard?1:0);
        }
        void Match()
        {
            string phase=state.phase;
            if(phase=="Roulette") { Roulette(); return; }
            if(phase=="Finished")
            {
                Label(new Rect(100,210,850,40),"THE LAST ORANGE STANDING",17,Green,true,TextAnchor.MiddleCenter);
                DrawOrange(new Rect(395,282,250,250),Mathf.Sin(Time.unscaledTime*2)*12);
                Label(new Rect(100,540,850,65),state.message,42,Cream,true,TextAnchor.MiddleCenter);
                if(Hosting && Button(new Rect(310,666,425,64),"한 판 더!   ↻",Orange,Bg,23)) Command("rematch");
                else if(!Hosting) Label(new Rect(100,680,850,44),"방장의 다음 게임을 기다리고 있어요.",18,Muted,false,TextAnchor.MiddleCenter);
                return;
            }
            Label(new Rect(82,193,570,36),PlayerName(state.active)+(Mine?"  ·  MY TURN":"  ·  도전 중"),23,Cream,true);
            Label(new Rect(730,193,230,36),"이번 턴  "+Seconds(state.duration)+"초",20,Orange,true,TextAnchor.MiddleRight);
            Box(new Rect(82,247,880,88),Hex("111925"));
            Label(new Rect(108,260,400,26),"넘어야 할 기록",14,Muted);
            Label(new Rect(108,283,400,37),state.target+" "+ScoreLabel,28,Green,true);
            Label(new Rect(580,257,355,36),"다음 턴은 "+Seconds(NextDuration)+"초  ↗",18,Muted,false,TextAnchor.MiddleRight);
            Label(new Rect(525,296,410,26),state.timePenalty>0?"이전 지목 지연  −"+Seconds(state.timePenalty)+"초 적용":"지목 3초 초과 시 다음 주자 시간 감소",12,state.timePenalty>0?Hex("FFA7A7"):Muted,false,TextAnchor.MiddleRight);
            if(phase=="Ready")
            {
                DrawOrange(new Rect(433,368,180,180),-8);
                Label(new Rect(100,565,850,60),Mine?(Hard?"오렌지, 빠르고 정확하게!":"준비됐어? 손가락 풀고!"):"지금은 "+PlayerName(state.active)+"의 차례",29,Cream,true,TextAnchor.MiddleCenter);
                if(Hard) Label(new Rect(100,623,850,30),"두벌식 자판 D H F P S W L · 한/영 전환 불필요 · 완성하면 자동 +1회",15,Hex("FFB2CB"),false,TextAnchor.MiddleCenter);
                if(Mine && Button(new Rect(310,665,425,65),"준비 완료   →",Orange,Bg,23)) Command("ready");
                else if(!Mine) Label(new Rect(100,678,850,40),"도전자의 준비를 기다리고 있어요.",18,Muted,false,TextAnchor.MiddleCenter);
                return;
            }
            if(phase=="Countdown")
            {
                Label(new Rect(100,347,850,220),Math.Max(1,Math.Ceiling(Remaining())).ToString(),145,Orange,true,TextAnchor.MiddleCenter);
                Label(new Rect(100,619,850,50),Mine?(Hard?"잠시 후 오렌지를 계속 입력하세요!":"잠시 후 SPACE를 연타하세요!"):"곧 도전이 시작됩니다!",25,Cream,true,TextAnchor.MiddleCenter); return;
            }
            if(phase=="Playing")
            {
                float remaining=(float)Remaining();
                Label(new Rect(450,347,490,60),remaining.ToString("0.0")+"s",38,remaining<3?Hex("FFAD99"):Orange,true,TextAnchor.MiddleCenter);
                Box(new Rect(480,421,430,8),Hex("30394A"));
                Box(new Rect(480,421,430*Mathf.Clamp01(remaining/(float)state.duration),8),Orange);
                float pulse=Mathf.Clamp01(1-(Time.unscaledTime-lastPulse)*9);
                // Orange-only viewport: growth/rotation cannot cover score or typing text.
                GUI.BeginGroup(new Rect(98,345,335,310));
                float size=224+Mathf.Min(36,state.taps*(Hard?7f:1.5f))+pulse*18;
                DrawOrange(new Rect((335-size)/2,(310-size)/2,size,size),pulse*5);
                GUI.EndGroup();
                Label(new Rect(450,441,490,150),state.taps.ToString(),112,Cream,true,TextAnchor.MiddleCenter);
                Label(new Rect(450,609,490,35),ScoreLabel+"  /  "+(state.taps>=state.target?"기록 통과!":"앞으로 "+(state.target-state.taps)+ScoreUnit),18,Green,true,TextAnchor.MiddleCenter);
                if(Hard) { TypingUI(); return; }
                Box(new Rect(300,694,445,60),Mine?(pulse>0?Orange:Hex("344157")):Hex("253041"));
                Label(new Rect(300,694,445,60),Mine?"S P A C E   연타!":"친구를 응원해주세요!",22,Mine?Cream:Muted,true,TextAnchor.MiddleCenter); return;
            }
            if(phase=="Result")
            {
                Label(new Rect(100,362,850,70),state.passed?"NICE SQUEEZE!":"OUT OF JUICE…",40,state.passed?Green:Hex("FFA7A7"),true,TextAnchor.MiddleCenter);
                Label(new Rect(100,432,850,140),state.taps+" "+ScoreLabel,75,Cream,true,TextAnchor.MiddleCenter);
                Label(new Rect(100,614,850,55),state.message,22,Muted,false,TextAnchor.MiddleCenter); return;
            }
            if(phase=="Nominate")
            {
                double elapsed=Math.Max(0,ServerNow-state.nominationStartedAt);
                double penalty=GameRules.NominationPenalty(state,ServerNow);
                Label(new Rect(100,347,850,37),elapsed<=3?"PICK THE NEXT ORANGE":"선택 지연! 다음 주자의 시간이 줄어듭니다",20,elapsed<=3?Green:Hex("FFA7A7"),true,TextAnchor.MiddleCenter);
                DrawOrange(new Rect(200,417,145,145),-10);
                Label(new Rect(405,401,410,98),elapsed<=3?Seconds(3-elapsed)+"초":Seconds(elapsed-3)+"초 초과",49,elapsed<=3?Cream:Hex("FFA7A7"),true,TextAnchor.MiddleCenter);
                Label(new Rect(405,509,410,44),"다음 도전 시간  "+Seconds(NextDuration)+"초",23,Orange,true,TextAnchor.MiddleCenter);
                Label(new Rect(100,592,850,47),Mine?"다음은 누구? 오른쪽 친구를 클릭!":"다음 도전자를 지목하고 있어요.",28,Cream,true,TextAnchor.MiddleCenter);
                Label(new Rect(100,660,850,36),"목표 "+state.target+ScoreUnit+"  ·  지목 지연 −"+Seconds(penalty)+"초",20,Orange,false,TextAnchor.MiddleCenter);
                Label(new Rect(100,718,850,48),"3초 안에 선택 · 초과 1초마다 다음 주자 −0.1초\n최소 1초 보장 · 감소는 다음 주자 한 턴에만 적용",15,Muted,false,TextAnchor.MiddleCenter);
            }
        }
        void TypingUI()
        {
            int progress=Mine?localWordProgress:state.wordProgress;
            string preview=WordTyping.Preview(progress);
            if(preview.Length==0) preview=Time.unscaledTime-lastPulse<0.35f && Mine?"오렌지 ✓":"오렌지";
            Box(new Rect(105,665,325,89),Hex("111925"));
            Label(new Rect(120,672,295,45),preview,31,progress>0?Cream:Hex("8490A6"),true,TextAnchor.MiddleCenter);
            Label(new Rect(112,719,310,24),Mine?"입력판 · 복붙 불가":"상대의 입력 진행",12,Muted,false,TextAnchor.MiddleCenter);
            string[] keys={"D\nㅇ","H\nㅗ","F\nㄹ","P\nㅔ","S\nㄴ","W\nㅈ","L\nㅣ"};
            for(int i=0;i<keys.Length;i++)
            {
                var r=new Rect(451+i*71,672,59,68);
                Box(r,i==progress?Hex("FFB2CB"):i<progress?Hex("42573B"):Hex("2A3546"));
                Label(r,keys[i],17,i==progress?Bg:i<progress?Green:Muted,true,TextAnchor.MiddleCenter);
            }
            Label(new Rect(105,762,850,27),Mine?typingHint:"친구가 오렌지를 입력하는 중…",14,Hex("FFB2CB"),false,TextAnchor.MiddleCenter);
        }
        void Roulette()
        {
            float remain=(float)Remaining();
            int chosen=remain<0.8f?Array.FindIndex(state.players,p=>p.id==state.active):Mathf.FloorToInt(Time.unscaledTime*11)%state.players.Length;
            Label(new Rect(100,202,850,45),"WHO'S THE FIRST ORANGE?",20,Green,true,TextAnchor.MiddleCenter);
            DrawOrange(new Rect(370,280,304,304),(float)(Time.unscaledTime*190*Mathf.Clamp(remain,0,2)));
            Label(new Rect(100,582,850,70),state.players[Math.Max(0,chosen)].name,45,colors[Math.Max(0,chosen)%4],true,TextAnchor.MiddleCenter);
            Label(new Rect(100,689,850,42),remain<0.8f?"첫 번째 플레이어 당첨!":"두근두근… 첫 번째 도전자를 뽑는 중",19,Muted,false,TextAnchor.MiddleCenter);
        }
        void PlayerCard(Player p,int i,Rect r,bool compact)
        {
            bool eligible=Mine && state.phase=="Nominate" && p.alive && p.connected && p.id!=myId;
            bool active=p.id==state.active && state.phase!="Lobby" && state.phase!="Roulette";
            Box(r,active?Hex("3A302A"):Hex("242D3D"));
            if(eligible && Button(r,"",Hex("34442F"),Cream,14)) Command("nominate",p.id);
            if(compact)
            {
                Circle(new Rect(r.x+7,r.y+11,25,25),p.alive?colors[i%4]:Muted);
                Label(new Rect(r.x+7,r.y+11,25,25),(i+1).ToString(),12,Bg,true,TextAnchor.MiddleCenter);
                string name=p.name+(p.id==myId?" · 나":"");
                if(name.Length>8) name=name.Substring(0,7)+"…";
                Label(new Rect(r.x+40,r.y+3,122,23),name,13,p.alive?Cream:Muted,true);
                string small=!p.connected?"연결 끊김":!p.alive?"탈락 · 관전":eligible?"지목하기 →":active?"● 도전 중":"최고 "+p.best+ScoreUnit;
                Label(new Rect(r.x+40,r.y+25,122,18),small,10,eligible?Green:Muted);
                if(r.Contains(Event.current.mousePosition))
                {
                    Box(new Rect(1035,209,345,29),Bg);
                    Label(new Rect(1043,209,330,29),p.name+(p.id==myId?" (나)":""),14,Cream,true);
                }
                return;
            }
            Circle(new Rect(r.x+16,r.y+25,48,48),p.alive?colors[i%4]:Muted);
            Label(new Rect(r.x+16,r.y+25,48,48),(i+1).ToString(),21,Bg,true,TextAnchor.MiddleCenter);
            Label(new Rect(r.x+80,r.y+15,236,32),p.name+(p.id==myId?"  (나)":""),18,p.alive?Cream:Muted,true);
            string status=!p.connected?"연결 끊김":!p.alive?"탈락 · 관전 중":eligible?"클릭해서 지목   →":active?"●  현재 도전자":"최고 기록 "+p.best+ScoreUnit;
            Label(new Rect(r.x+80,r.y+54,236,27),status,13,eligible?Green:Muted);
        }
        void DrawOrange(Rect r,float rotation)
        {
            var old=GUI.matrix; var center=new Vector3(r.center.x,r.center.y,0);
            GUI.matrix=old*Matrix4x4.Translate(center)*Matrix4x4.Rotate(Quaternion.Euler(0,0,rotation))*Matrix4x4.Translate(-center);
            GUI.color=Color.white; GUI.DrawTexture(r,orangeArt); GUI.matrix=old;
        }
        void Circle(Rect r,Color c) { GUI.color=c; GUI.DrawTexture(r,disc); GUI.color=Color.white; }
        void Box(Rect r,Color c) { GUI.color=Color.white; GUI.DrawTexture(r,Texture2D.whiteTexture,ScaleMode.StretchToFill,true,0,c,0,12); }
        void Label(Rect r,string value,int size,Color c,bool bold=false,TextAnchor alignment=TextAnchor.MiddleLeft)
        { text.fontSize=size; text.normal.textColor=c; text.fontStyle=bold?FontStyle.Bold:FontStyle.Normal; text.alignment=alignment; text.wordWrap=true; GUI.Label(r,value,text); }
        bool Button(Rect r,string value,Color background,Color foreground,int size)
        {
            bool hover=r.Contains(Event.current.mousePosition) && GUI.enabled;
            Box(r,GUI.enabled?(hover?Color.Lerp(background,Color.white,0.1f):background):Hex("29313C"));
            Label(r,value,size,GUI.enabled?foreground:Muted,true,TextAnchor.MiddleCenter);
            return GUI.Button(r,GUIContent.none,GUIStyle.none);
        }
    }
}
