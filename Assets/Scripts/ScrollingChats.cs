using System;
using System.Collections.Generic;
using System.Linq;

namespace OrangeRush
{
    // A bounded, latest-per-sender waiting list feeds three separated flight lanes.
    // Server time and sequence seeds give every client the same vertical jitter.
    public sealed class ScrollingChats
    {
        public const double ViewWidth = 880, Speed = 210, Gap = 36;
        public const int MaxVisible = 6, LaneCount = 3;
        public sealed class Message
        {
            public int sender, sequence, lane;
            public string name, text;
            public double startsAt, expiresAt, width;
            public double Duration { get { return (ViewWidth + width) / Speed; } }
            public double X(double now) { return -width + (now - startsAt) * Speed; }
            public double Y { get { return 480 + lane * 50 + (uint)(sequence * 1103515245 + sender * 12345) % 9 - 4; } }
        }
        public readonly List<Message> Active = new List<Message>();
        readonly List<Message> waiting = new List<Message>();
        int seenSequence;
        public int WaitingCount { get { return waiting.Count; } }
        public void Clear() { Active.Clear(); waiting.Clear(); seenSequence = 0; }
        public void Update(Snapshot state, double now)
        {
            if (state == null) { Clear(); return; }
            if (state.phase == "Lobby" || state.phase == "Roulette" || state.phase == "Finished")
            { Active.Clear(); waiting.Clear(); seenSequence = state.chatSequence; return; }
            foreach (var p in state.players.Where(p => p.connected && p.chatSequence > seenSequence && p.chatUntil > now && !string.IsNullOrEmpty(p.chat)).OrderBy(p => p.chatSequence))
            {
                waiting.RemoveAll(m => m.sender == p.id);
                waiting.Add(new Message { sender = p.id, sequence = p.chatSequence, name = p.name, text = p.chat, expiresAt = p.chatUntil,
                    width = new System.Globalization.StringInfo(p.chat).LengthInTextElements * 22 + 16 });
            }
            seenSequence = Math.Max(seenSequence, state.chatSequence);
            waiting.RemoveAll(m => m.expiresAt <= now || !state.players.Any(p => p.id == m.sender && p.connected));
            Active.RemoveAll(m => now - m.startsAt >= m.Duration || !state.players.Any(p => p.id == m.sender && p.connected));
            if (state.phase != "Playing") { Active.Clear(); return; }
            while (Active.Count < MaxVisible && waiting.Count > 0)
            {
                var next = waiting[0]; int lane = -1;
                for (int offset = 0; offset < LaneCount; offset++)
                {
                    int candidate = (next.sequence + offset) % LaneCount;
                    if (Active.Where(m => m.lane == candidate).All(m => m.X(now) >= Gap)) { lane = candidate; break; }
                }
                if (lane < 0) break;
                waiting.RemoveAt(0); next.lane = lane;
                next.startsAt = now;
                Active.Add(next);
            }
        }
    }
}
