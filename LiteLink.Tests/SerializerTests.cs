using LiteLink.Shared.Serializer;
using Xunit;

namespace LiteLink.Tests
{
    /// <summary>direct LiteWriter/LiteReader round-trips, no server involved</summary>
    public class SerializerTests
    {
        [Fact]
        public void Enums_SerializeAtUnderlyingSize()
        {
            Assert.Equal(1, SerializeEnum(ByteEnum.Max).Length);
            Assert.Equal(1, SerializeEnum(SByteEnum.Neg).Length);
            Assert.Equal(2, SerializeEnum(ShortEnum.Neg).Length);
            Assert.Equal(2, SerializeEnum(UShortEnum.Big).Length);
            Assert.Equal(4, SerializeEnum(TestEnum.Beta).Length);
            Assert.Equal(4, SerializeEnum(UIntEnum.Big).Length);
            Assert.Equal(8, SerializeEnum(LongEnum.Huge).Length);
            Assert.Equal(8, SerializeEnum(ULongEnum.Huge).Length);
        }

        [Fact]
        public void Enums_RoundTripThroughLiteSerializer()
        {
            Assert.Equal(ByteEnum.Max, RoundTripEnum(ByteEnum.Max));
            Assert.Equal(SByteEnum.Neg, RoundTripEnum(SByteEnum.Neg));
            Assert.Equal(ShortEnum.Neg, RoundTripEnum(ShortEnum.Neg));
            Assert.Equal(UIntEnum.Big, RoundTripEnum(UIntEnum.Big));
            Assert.Equal(LongEnum.Huge, RoundTripEnum(LongEnum.Huge));
            Assert.Equal(ULongEnum.Huge, RoundTripEnum(ULongEnum.Huge));
        }

        private static byte[] SerializeEnum<T>(T value)
        {
            var writer = new LiteWriter();
            LiteSerializer.Serialize(ref writer, value);
            return writer.Close();
        }

        private static T RoundTripEnum<T>(T value)
        {
            var reader = new LiteReader(SerializeEnum(value));
            return LiteSerializer.Deserialize<T>(ref reader);
        }

        [Fact]
        public void Primitives_RoundTrip()
        {
            var writer = new LiteWriter();
            writer.WriteBoolean(true);
            writer.WriteBoolean(false);
            writer.WriteByte(255);
            writer.WriteSByte(-128);
            writer.WriteInt16(short.MinValue);
            writer.WriteUInt16(ushort.MaxValue);
            writer.WriteInt32(int.MinValue);
            writer.WriteUInt32(uint.MaxValue);
            writer.WriteInt64(long.MinValue);
            writer.WriteUInt64(ulong.MaxValue);
            writer.WriteSingle(float.Epsilon);
            writer.WriteDouble(double.MaxValue);
            var data = writer.Close();

            var reader = new LiteReader(data);
            Assert.True(reader.ReadBoolean());
            Assert.False(reader.ReadBoolean());
            Assert.Equal(255, reader.ReadByte());
            Assert.Equal(-128, reader.ReadSByte());
            Assert.Equal(short.MinValue, reader.ReadInt16());
            Assert.Equal(ushort.MaxValue, reader.ReadUInt16());
            Assert.Equal(int.MinValue, reader.ReadInt32());
            Assert.Equal(uint.MaxValue, reader.ReadUInt32());
            Assert.Equal(long.MinValue, reader.ReadInt64());
            Assert.Equal(ulong.MaxValue, reader.ReadUInt64());
            Assert.Equal(float.Epsilon, reader.ReadSingle());
            Assert.Equal(double.MaxValue, reader.ReadDouble());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("plain ascii")]
        [InlineData("unicode ✓ ün 数据")]
        public void Strings_RoundTrip(string value)
        {
            var writer = new LiteWriter();
            writer.WriteString(value);
            var data = writer.Close();

            var reader = new LiteReader(data);
            Assert.Equal(value, reader.ReadString());
        }

        [Fact]
        public void LargeString_ExpandsBuffersAndRoundTrips()
        {
            //well past the 8192-byte internal buffer
            var value = new string('ψ', 50000) + "END";

            var writer = new LiteWriter();
            writer.WriteString(value);
            writer.WriteInt32(1234); //something after the expansion
            var data = writer.Close();

            var reader = new LiteReader(data);
            Assert.Equal(value, reader.ReadString());
            Assert.Equal(1234, reader.ReadInt32());
        }

        [Theory]
        [InlineData(0ul)]
        [InlineData(1ul)]
        [InlineData(0x7Ful)] //1-byte boundary
        [InlineData(0x80ul)] //2-byte encoding starts
        [InlineData(0x3FFFul)] //2-byte boundary
        [InlineData(0x4000ul)] //4-byte encoding starts
        [InlineData(0x1FFFFFFFul)] //4-byte boundary
        [InlineData(ulong.MaxValue)] //null marker
        public void Lengths_RoundTrip(ulong value)
        {
            var writer = new LiteWriter();
            writer.WriteLength(value);
            var data = writer.Close();

            var reader = new LiteReader(data);
            Assert.Equal(value, reader.ReadLength());
        }

        [Fact]
        public void ByteArrays_RoundTrip()
        {
            var small = new byte[] { 1, 2, 3 };
            var big = new byte[30000];
            new Random(42).NextBytes(big);

            var writer = new LiteWriter();
            writer.WriteByteArray(small);
            writer.WriteByteArray(big);
            var data = writer.Close();

            var reader = new LiteReader(data);
            Assert.Equal(small, reader.ReadByteArray());
            Assert.Equal(big, reader.ReadByteArray());
        }

        [Theory]
        [InlineData("5.5")]
        [InlineData("-5.5")]
        [InlineData("0")]
        [InlineData("123.456789")]
        [InlineData("-99999999.999999999")]
        public void Decimals_RoundTrip(string text)
        {
            var value = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

            var writer = new LiteWriter();
            writer.WriteDecimal(value);
            var data = writer.Close();

            var reader = new LiteReader(data);
            Assert.Equal(value, reader.ReadDecimal());
        }

        [Fact]
        public void DatesAndGuids_RoundTrip()
        {
            var utc = new DateTime(2024, 6, 15, 12, 30, 45, DateTimeKind.Utc);
            var local = new DateTime(2024, 6, 15, 12, 30, 45, DateTimeKind.Local);
            var guid = Guid.NewGuid();

            var writer = new LiteWriter();
            writer.WriteDate(utc);
            writer.WriteDate(local);
            writer.WriteGuid(guid);
            var data = writer.Close();

            var reader = new LiteReader(data);
            Assert.Equal(utc, reader.ReadDate());
            Assert.Equal(local.ToUniversalTime(), reader.ReadDate());
            Assert.Equal(guid, reader.ReadGuid());
        }

        [Fact]
        public void Compression_RoundTripsThroughCloseAndDecompress()
        {
            //message layout: [u16 opcode][isCompressed flag][payload], compressed from offset 3
            var payload = new string('z', 5000);

            var writer = new LiteWriter();
            writer.WriteUInt16(7);
            writer.WriteByte(0); //compression placeholder
            writer.WriteString(payload);
            var data = writer.Close(3);

            Assert.True(data.Length < 5000, "payload should have been compressed");

            var reader = new LiteReader(data);
            Assert.Equal(7, reader.ReadUInt16());
            Assert.True(reader.ReadBoolean(), "compressed flag should be set");
            reader.Decompress();
            Assert.Equal(payload, reader.ReadString());
        }

        [Fact]
        public void SmallMessage_IsNotCompressed()
        {
            var writer = new LiteWriter();
            writer.WriteUInt16(7);
            writer.WriteByte(0);
            writer.WriteString("tiny");
            var data = writer.Close(3);

            var reader = new LiteReader(data);
            Assert.Equal(7, reader.ReadUInt16());
            Assert.False(reader.ReadBoolean());
            Assert.Equal("tiny", reader.ReadString());
        }

        [Fact]
        public void ReadRemaining_ReturnsTailAndConsumesReader()
        {
            var writer = new LiteWriter();
            writer.WriteInt32(1);
            writer.WriteByte(0xAA);
            writer.WriteByte(0xBB);
            var data = writer.Close();

            var reader = new LiteReader(data);
            reader.ReadInt32();
            Assert.Equal(new byte[] { 0xAA, 0xBB }, reader.ReadRemaining());
        }
    }
}
