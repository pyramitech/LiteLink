using LiteLink.Shared.Serializer;

namespace LiteLink.Gateway
{
    /// <summary>
    /// A parsed view over one raw client message, used by gateway interceptors to analyze traffic.
    /// Regular messages are [u16 opcode][bool compressed][payload]; multichannel calls arrive
    /// wrapped in a system envelope [u16 0][0][type 1][channel][inner message] — this class sees
    /// through the envelope so interceptors can reason about the effective call either way.
    /// </summary>
    public class GatewayMessage
    {
        /// <summary>the raw message exactly as received from the client</summary>
        public byte[] Raw { get; }

        /// <summary>outer opcode; 0 for system messages (handshake, api info) and multichannel envelopes</summary>
        public ushort Opcode { get; }

        /// <summary>system message type when Opcode is 0: 0 handshake, 1 multichannel, 2 api info</summary>
        public byte SystemType { get; }

        public bool IsMultiChannel { get; }

        /// <summary>the client's multichannel slot, only meaningful when IsMultiChannel</summary>
        public byte Channel { get; }

        /// <summary>the opcode of the actual call: the inner opcode for multichannel envelopes, otherwise Opcode</summary>
        public ushort EffectiveOpcode { get; }

        /// <summary>whether the effective message's payload is compressed (GetPayload decompresses transparently)</summary>
        public bool IsCompressed { get; }

        public GatewayMessage(byte[] raw)
        {
            Raw = raw;

            var reader = new LiteReader(raw);
            Opcode = reader.ReadUInt16();
            IsCompressed = reader.ReadBoolean();
            EffectiveOpcode = Opcode;

            if (Opcode == 0 && raw.Length > 3)
            {
                SystemType = reader.ReadByte();
                if (SystemType == 1 && raw.Length >= 8)
                {
                    IsMultiChannel = true;
                    Channel = reader.ReadByte();
                    EffectiveOpcode = reader.ReadUInt16();
                    IsCompressed = reader.ReadBoolean();
                }
            }
        }

        /// <summary>
        /// the decompressed payload of the effective message (everything after its opcode and
        /// compression flag); for non-multichannel system messages this starts with the type byte
        /// </summary>
        public byte[] GetPayload()
        {
            var reader = new LiteReader(Raw, IsMultiChannel ? 5 : 0);
            reader.ReadUInt16();
            if (reader.ReadBoolean())
                reader.Decompress();
            return reader.ReadRemaining();
        }

        /// <summary>
        /// rebuilds the message with a replacement payload (uncompressed), preserving the
        /// multichannel envelope and effective opcode — e.g. to swap a plaintext password
        /// for its hash before forwarding
        /// </summary>
        public byte[] WithPayload(byte[] payload)
        {
            var writer = new LiteWriter();
            if (IsMultiChannel)
            {
                writer.WriteUInt16(0);
                writer.WriteBoolean(false);
                writer.WriteByte(1);
                writer.WriteByte(Channel);
            }
            writer.WriteUInt16(EffectiveOpcode);
            writer.WriteByte(0); //not compressed
            writer.WriteFixedByteArray(payload);
            return writer.Close();
        }
    }
}
