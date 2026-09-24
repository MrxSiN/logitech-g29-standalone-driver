using System;
using System.Collections.Generic;
using System.Text;

namespace G29.Bridge.Runtime
{
    // One framed message between the bridge and the Brainfuck program
    // (src/brainfuck/ABI.md):
    //   A5 | version | type | flags | sequence u16 LE | length u16 LE | payload
    public sealed class Frame
    {
        public const byte Magic = 0xA5;
        public const byte Version = 1;
        public const int HeaderLength = 8;
        public const int MaximumPayload = 4096;

        public Frame(byte type, byte flags, ushort sequence, byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException("payload");
            }

            if (payload.Length > MaximumPayload)
            {
                throw new ArgumentException("A frame payload may not exceed " + MaximumPayload + " bytes.", "payload");
            }

            Type = type;
            Flags = flags;
            Sequence = sequence;
            Payload = payload;
        }

        public byte Type { get; private set; }

        public byte Flags { get; private set; }

        public ushort Sequence { get; private set; }

        public byte[] Payload { get; private set; }

        public byte[] Encode()
        {
            var bytes = new byte[HeaderLength + Payload.Length];
            bytes[0] = Magic;
            bytes[1] = Version;
            bytes[2] = Type;
            bytes[3] = Flags;
            bytes[4] = (byte)Sequence;
            bytes[5] = (byte)(Sequence >> 8);
            bytes[6] = (byte)Payload.Length;
            bytes[7] = (byte)(Payload.Length >> 8);
            Buffer.BlockCopy(Payload, 0, bytes, HeaderLength, Payload.Length);
            return bytes;
        }

        public override string ToString()
        {
            var text = new StringBuilder();
            text.AppendFormat("{0:X2} f{1:X2} #{2} [{3}]", Type, Flags, Sequence, Payload.Length);
            if (Payload.Length > 0)
            {
                text.Append(' ').Append(BitConverter.ToString(Payload, 0, Math.Min(Payload.Length, 64)).Replace("-", string.Empty));
                if (Payload.Length > 64)
                {
                    text.Append("...");
                }
            }

            return text.ToString();
        }
    }

    // Splits the Brainfuck program's output into frames. Anything malformed is a
    // protocol violation: the bridge never guesses what the program meant.
    public sealed class FrameParser
    {
        private readonly byte[] header = new byte[Frame.HeaderLength];
        private int headerCount;
        private byte[] payload;
        private int payloadCount;

        public bool InFrame
        {
            get { return headerCount > 0; }
        }

        // Returns a frame when b completes one, otherwise null.
        public Frame Push(byte b)
        {
            if (headerCount < Frame.HeaderLength)
            {
                if (headerCount == 0 && b != Frame.Magic)
                {
                    throw new AbiViolation(string.Format("Output byte {0:X2} is not the start of a frame.", b));
                }

                if (headerCount == 1 && b != Frame.Version)
                {
                    throw new AbiViolation(string.Format("Output frame uses ABI version {0}; version {1} is required.", b, Frame.Version));
                }

                header[headerCount++] = b;
                if (headerCount < Frame.HeaderLength)
                {
                    return null;
                }

                int length = header[6] | (header[7] << 8);
                if (length > Frame.MaximumPayload)
                {
                    throw new AbiViolation("Output frame payload of " + length + " bytes exceeds the limit.");
                }

                payload = new byte[length];
                payloadCount = 0;
                return length == 0 ? Complete() : null;
            }

            payload[payloadCount++] = b;
            return payloadCount == payload.Length ? Complete() : null;
        }

        private Frame Complete()
        {
            var frame = new Frame(header[2], header[3], (ushort)(header[4] | (header[5] << 8)), payload);
            headerCount = 0;
            payload = null;
            return frame;
        }
    }

    public sealed class AbiViolation : Exception
    {
        public AbiViolation(string message)
            : base(message)
        {
        }
    }

    // Little-endian payload builder for bridge -> Brainfuck events.
    public sealed class PayloadWriter
    {
        private readonly List<byte> bytes = new List<byte>();

        public PayloadWriter U8(int value)
        {
            bytes.Add((byte)value);
            return this;
        }

        public PayloadWriter U16(int value)
        {
            bytes.Add((byte)value);
            bytes.Add((byte)(value >> 8));
            return this;
        }

        public PayloadWriter U32(long value)
        {
            for (int shift = 0; shift < 32; shift += 8)
            {
                bytes.Add((byte)(value >> shift));
            }

            return this;
        }

        public PayloadWriter U64(long value)
        {
            for (int shift = 0; shift < 64; shift += 8)
            {
                bytes.Add((byte)(value >> shift));
            }

            return this;
        }

        public PayloadWriter Bytes(byte[] value)
        {
            bytes.AddRange(value);
            return this;
        }

        // u16 length + UTF-8.
        public PayloadWriter Text(string value)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(value ?? string.Empty);
            U16(encoded.Length);
            bytes.AddRange(encoded);
            return this;
        }

        public byte[] ToArray()
        {
            return bytes.ToArray();
        }
    }

    // Little-endian payload reader for Brainfuck -> bridge commands. Every read is
    // bounds-checked; leftover bytes are a violation.
    public sealed class PayloadReader
    {
        private readonly byte[] payload;
        private int position;

        public PayloadReader(byte[] payload)
        {
            this.payload = payload;
        }

        public int Remaining
        {
            get { return payload.Length - position; }
        }

        public int U8()
        {
            Need(1);
            return payload[position++];
        }

        public int U16()
        {
            Need(2);
            int value = payload[position] | (payload[position + 1] << 8);
            position += 2;
            return value;
        }

        public long U32()
        {
            Need(4);
            long value = 0;
            for (int index = 3; index >= 0; index--)
            {
                value = (value << 8) | payload[position + index];
            }

            position += 4;
            return value;
        }

        public byte[] Bytes(int count)
        {
            Need(count);
            var value = new byte[count];
            Buffer.BlockCopy(payload, position, value, 0, count);
            position += count;
            return value;
        }

        public string Text()
        {
            int length = U16();
            byte[] value = Bytes(length);
            try
            {
                return new UTF8Encoding(false, true).GetString(value);
            }
            catch (ArgumentException)
            {
                throw new AbiViolation("A text field is not valid UTF-8.");
            }
        }

        public void End()
        {
            if (position != payload.Length)
            {
                throw new AbiViolation(string.Format("{0} unexpected trailing payload byte(s).", payload.Length - position));
            }
        }

        private void Need(int count)
        {
            if (count < 0 || position + count > payload.Length)
            {
                throw new AbiViolation("A frame payload is shorter than its command requires.");
            }
        }
    }
}
