using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace OrangeRush
{
    public static class RelayPacketCodec
    {
        public const int MaxBytes = 16384;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static byte[] Encode(string line)
        {
            byte[] raw = Utf8.GetBytes(line);
            if(raw.Length > MaxBytes) throw new InvalidDataException("Message is too large.");
            if(raw.Length > 384)
            {
                using(var output = new MemoryStream())
                {
                    output.WriteByte(1);
                    using(var zip = new DeflateStream(output, CompressionLevel.Fastest, true)) zip.Write(raw,0,raw.Length);
                    if(output.Length < raw.Length + 1) return output.ToArray();
                }
            }
            var packet = new byte[raw.Length + 1];
            Buffer.BlockCopy(raw,0,packet,1,raw.Length);
            return packet;
        }
        public static string Decode(byte[] packet)
        {
            if(packet == null || packet.Length < 1 || packet.Length > MaxBytes + 1) throw new InvalidDataException("Invalid message length.");
            if(packet[0] == 0) return Utf8.GetString(packet,1,packet.Length - 1);
            if(packet[0] != 1) throw new InvalidDataException("Invalid message encoding.");
            using(var input = new MemoryStream(packet,1,packet.Length - 1))
            using(var zip = new DeflateStream(input,CompressionMode.Decompress))
            using(var output = new MemoryStream())
            {
                var buffer = new byte[1024]; int count;
                while((count = zip.Read(buffer,0,buffer.Length)) > 0)
                {
                    if(output.Length + count > MaxBytes) throw new InvalidDataException("Expanded message is too large.");
                    output.Write(buffer,0,count);
                }
                if(output.Length == 0) throw new InvalidDataException("Empty compressed message.");
                return Utf8.GetString(output.ToArray());
            }
        }
    }
}
