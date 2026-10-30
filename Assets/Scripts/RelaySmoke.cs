#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace OrangeRush
{
    // Development builds only: exercises the actual game and Relay in separate processes.
    public sealed class RelaySmoke : MonoBehaviour
    {
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
        OrangeGame game;
        string role,codeFile,resultFile;
        double started,passedAt,nextKey;
        int keyIndex,typingTurn=-1,sequence;
        bool joined,startedMatch,sentChat,sentVoice,passed;
        static readonly int[] Keys={3,7,5,15,18,22,11};
        static double Now=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d;
        static string Arg(string key)
        {var args=Environment.GetCommandLineArgs();int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:"";}
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() {if(Arg("--relay-smoke")!="") new GameObject("Relay verification").AddComponent<RelaySmoke>();}
        void Start()
        {role=Arg("--relay-smoke");codeFile=Arg("--relay-code-file");resultFile=Arg("--relay-result");started=Now;}
        T Get<T>(string name)=>(T)typeof(OrangeGame).GetField(name,Flags).GetValue(game);
        void Set(string name,object value)=>typeof(OrangeGame).GetField(name,Flags).SetValue(game,value);
        void Call(string name,params object[] values)=>typeof(OrangeGame).GetMethod(name,Flags).Invoke(game,values);
        void Command(string name,int target=-1,int seq=0)=>Call("Command",name,target,seq);
        void Finish(bool success,string message)
        {
            File.WriteAllText(resultFile,JsonUtility.ToJson(new Result {success=success,message=message,version=GameRules.Version}));
            Debug.Log("RELAY_SMOKE_"+(success?"PASS ":"FAIL ")+role+" "+message);
            if(!success) Application.Quit(1);
        }
        [Serializable] class Result {public bool success;public string message,version;}
        void Update()
        {
            try
            {
                if(passed) {if(Now-passedAt>3) Application.Quit(0);return;}
                if(Now-started>70) {Finish(false,"Timed out: "+(game==null?"no game":Get<string>("error")));enabled=false;return;}
                if(game==null) {game=FindFirstObjectByType<OrangeGame>();if(game==null)return;}
                if(!joined)
                {
                    if(role!="host" && !File.Exists(codeFile)) return;
                    Set("nickname",role=="host"?"RelayHost":"Relay"+Arg("--relay-profile"));
                    if(role!="host") Set("roomCode",File.ReadAllText(codeFile).Trim());
                    joined=true;Call("Connect",role=="host");
                }
                var state=Get<Snapshot>("state");
                if(state==null)
                {
                    string error=Get<string>("error");
                    if(error!="") {Finish(false,error);enabled=false;}
                    return;
                }
                int mine=Get<int>("myId");
                if(role=="host" && !File.Exists(codeFile)) File.WriteAllText(codeFile,((RelayRoom)Get<IRoomConnection>("network")).RoomCode);
                if(role=="host" && state.phase=="Lobby" && state.players.Length==3 && !startedMatch)
                {startedMatch=true;Command("mode",1);Command("start");}
                if(state.phase=="Ready" && state.active==mine) Command("ready");
                if(GameRules.CanChat(state,mine))
                {
                    if(!sentChat) {Set("chatDraft","코드로 연결 성공");Call("SendChat");sentChat=true;}
                    if(!sentVoice) {Command("voice",0);sentVoice=true;}
                }
                if(state.phase=="Playing" && state.active==mine)
                {
                    if(typingTurn!=state.turn) {typingTurn=state.turn;keyIndex=0;}
                    if(keyIndex<Keys.Length && Now>=nextKey)
                    {nextKey=Now+0.08;Command("typeKey",Keys[keyIndex++],++sequence);}
                }
                if(state.phase=="Nominate" && state.active==mine)
                    Command("nominate",state.players.First(p=>p.id!=mine&&p.alive&&p.connected).id);
                if(state.turn>=2 && state.phase=="Playing" && state.mode=="Hard" && state.target>=1 && state.chatSequence>=1 && state.voiceSequence>=1 && state.players.Length==3)
                {passed=true;passedAt=Now;Finish(true,"3 real Relay clients; room code, Hard word scoring, chat, voice, nomination and second turn synchronized.");}
            }
            catch(Exception e) {Finish(false,e.GetBaseException().Message);enabled=false;}
        }
    }
}
#endif
