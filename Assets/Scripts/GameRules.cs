using System;
using System.Collections.Generic;
using System.Linq;

namespace OrangeRush
{
    [Serializable] public class Player
    {
        public int id;
        public string name;
        public bool alive = true;
        public bool connected = true;
        public int best;
    }
    [Serializable] public class Snapshot
    {
        public Player[] players = new Player[0];
        public string phase = "Lobby";
        public string mode = "Normal";
        public int wordProgress;
        public int active = -1, host = 0, turn = 1, target, taps, winner = -1;
        public double duration = 7, endsAt, serverTime, nominationStartedAt, timePenalty;
        public string message = "친구들을 초대하고 게임을 시작하세요.";
        public bool passed;
    }
    [Serializable] public class Wire
    {
        public string type, name, error;
        public int id, target, sequence, protocol;
        public Snapshot state;
    }

    // Pure rules: all mutations run on the host's Unity main thread.
    public class GameRules
    {
        public const int ProtocolVersion = 3;
        public const string Version = "0.3.0";
        public const int MaxPlayers = 20;
        public const double NominationGrace = 3, MinimumDuration = 1;
        public static int TurnStep(string mode) { return mode == "Hard" ? 2 : 1; }
        public static double BaseDuration(int turn, string mode) { return 7 + (turn - 1) * TurnStep(mode); }
        public static double NominationPenalty(Snapshot state, double now)
        {
            if (state.phase != "Nominate") return 0;
            // Three free seconds, then one tenth per COMPLETE extra second.
            double seconds = Math.Floor(Math.Max(0, now - state.nominationStartedAt - NominationGrace) + 0.000001);
            return Math.Min(seconds / 10, BaseDuration(state.turn + 1, state.mode) - MinimumDuration);
        }
        public static double NextDuration(Snapshot state, double now)
        { return BaseDuration(state.turn + 1, state.mode) - NominationPenalty(state, now); }
        public readonly Snapshot State = new Snapshot();
        readonly Random random;
        int nextId;
        int lastSequence;
        public GameRules(int seed = -1) { random = seed < 0 ? new Random() : new Random(seed); }
        public int Add(string name)
        {
            if (State.phase != "Lobby" || State.players.Length >= MaxPlayers) return -1;
            var p = new Player { id = nextId++, name = Clean(name) };
            State.players = State.players.Concat(new[] { p }).ToArray();
            return p.id;
        }
        static string Clean(string value)
        {
            value = new string((value ?? "PLAYER").Where(c => !char.IsControl(c) && c != '<' && c != '>').ToArray()).Trim();
            return value.Length == 0 ? "PLAYER" : value.Substring(0, Math.Min(12, value.Length));
        }
        public bool Command(int sender, string command, int target, int sequence, double now)
        {
            Tick(now);
            var player = State.players.FirstOrDefault(p => p.id == sender && p.connected);
            if (player == null) return false;
            if (command == "mode" && sender == State.host && State.phase == "Lobby" && (target == 0 || target == 1))
            {
                State.mode = target == 1 ? "Hard" : "Normal";
                State.message = State.mode == "Hard" ? "하드 모드: 오렌지를 직접 타이핑하세요!" : "기본 모드: 스페이스바를 연타하세요!";
                return true;
            }
            if (command == "start" && sender == State.host && State.phase == "Lobby" && State.players.Length >= 2)
            {
                State.active = State.players[random.Next(State.players.Length)].id;
                State.phase = "Roulette"; State.endsAt = now + 4.5;
                State.message = "첫 번째 플레이어를 뽑고 있어요…";
                return true;
            }
            if (command == "ready" && sender == State.active && State.phase == "Ready")
            {
                State.phase = "Countdown"; State.endsAt = now + 3;
                State.taps = 0; State.wordProgress = 0; lastSequence = 0; return true;
            }
            if (command == "tap" && State.mode == "Normal" && sender == State.active && State.phase == "Playing" && sequence > lastSequence)
            {
                lastSequence = sequence; State.taps++; return true;
            }
            if (command == "typeKey" && State.mode == "Hard" && sender == State.active && State.phase == "Playing" && sequence > lastSequence && WordTyping.IsValidKey(target))
            {
                lastSequence = sequence;
                if (WordTyping.Advance(ref State.wordProgress, target) == WordKeyResult.Completed) State.taps++;
                return true;
            }
            if (command == "nominate" && sender == State.active && State.phase == "Nominate" &&
                State.players.Any(p => p.id == target && p.id != sender && p.alive && p.connected))
            {
                Next(target, NominationPenalty(State, now)); return true;
            }
            if (command == "rematch" && sender == State.host && State.phase == "Finished")
            {
                State.players = State.players.Where(p => p.connected).ToArray();
                foreach (var p in State.players) { p.alive = true; p.best = 0; }
                State.phase = "Lobby"; State.turn = 1; State.duration = 7; State.target = 0;
                State.taps = 0; State.wordProgress = 0; State.active = -1; State.winner = -1;
                State.timePenalty = 0; State.nominationStartedAt = 0; State.endsAt = 0; State.passed = false;
                State.message = "다시 모였네요. 새로운 승부를 시작하세요!"; return true;
            }
            return false;
        }
        void Next(int id, double penalty = 0)
        {
            State.active = id; State.turn++;
            State.timePenalty = penalty;
            State.duration = Math.Max(MinimumDuration, BaseDuration(State.turn, State.mode) - penalty);
            State.nominationStartedAt = 0;
            State.taps = 0; State.wordProgress = 0; State.phase = "Ready";
            State.message = "키보드 준비! 준비 완료를 누르면 3초 뒤 시작합니다.";
        }
        public bool Tick(double now)
        {
            State.serverTime = now;
            if (State.phase == "Roulette" && now >= State.endsAt)
            { State.phase = "Ready"; State.message = "첫 번째 주인공이 정해졌어요!"; return true; }
            if (State.phase == "Countdown" && now >= State.endsAt)
            { State.phase = "Playing"; State.endsAt += State.duration; return true; }
            if (State.phase == "Playing" && now >= State.endsAt)
            {
                var p = State.players.First(x => x.id == State.active);
                p.best = Math.Max(p.best, State.taps);
                State.passed = State.taps >= State.target;
                if (State.passed) State.target = State.taps;
                else p.alive = false;
                State.phase = "Result"; State.endsAt = now + 3;
                State.message = State.passed ? "성공! 다음 도전자를 지목하세요." : p.name + " 탈락! 다음 주자를 추첨합니다.";
                return true;
            }
            if (State.phase == "Result" && now >= State.endsAt)
            {
                var alive = State.players.Where(p => p.alive && p.connected).ToArray();
                if (alive.Length <= 1) Finish(alive);
                else if (State.passed)
                {
                    State.phase = "Nominate"; State.nominationStartedAt = now;
                    State.message = "3초 안에 다음 도전자를 지목하세요!";
                }
                else Next(alive[random.Next(alive.Length)].id);
                return true;
            }
            return false;
        }
        void Finish(Player[] alive)
        {
            State.phase = "Finished"; State.winner = alive.Length == 1 ? alive[0].id : -1;
            State.message = alive.Length == 1 ? alive[0].name + " 최종 우승!" : "연결된 플레이어가 없습니다.";
        }
        public void Disconnect(int id)
        {
            var p = State.players.FirstOrDefault(x => x.id == id);
            if (p == null) return;
            if (State.phase == "Lobby" || State.phase == "Finished")
            { State.players = State.players.Where(x => x.id != id).ToArray(); return; }
            p.connected = false; p.alive = false;
            var alive = State.players.Where(x => x.alive && x.connected).ToArray();
            if (alive.Length <= 1) Finish(alive);
            else if (State.active == id) Next(alive[random.Next(alive.Length)].id);
        }
    }

    public enum WordKeyResult { Progress, Completed, Mistake, Edited }

    // A two-set Korean keyboard spells 오렌지 as D H F P S W L.
    // No text payload is accepted: each letter is validated on the host.
    public static class WordTyping
    {
        public const int Backspace = 26;
        static readonly int[] Keys = { 3, 7, 5, 15, 18, 22, 11 };
        static readonly string[] Stages = { "", "ㅇ", "오", "올", "오레", "오렌", "오렌ㅈ" };
        public static bool IsValidKey(int key) { return key >= 0 && key <= Backspace; }
        public static string Preview(int progress) { return Stages[Math.Max(0, Math.Min(6, progress))]; }
        public static WordKeyResult Advance(ref int progress, int key)
        {
            if (!IsValidKey(key)) return WordKeyResult.Mistake;
            if (key == Backspace) { progress = Math.Max(0, progress - 1); return WordKeyResult.Edited; }
            if (key != Keys[progress])
            { progress = key == Keys[0] ? 1 : 0; return WordKeyResult.Mistake; }
            progress++;
            if (progress < Keys.Length) return WordKeyResult.Progress;
            progress = 0; return WordKeyResult.Completed;
        }
    }
}
