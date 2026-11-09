using LiteLink.Shared.Serializer;
using Xunit;

namespace LiteLink.Tests
{
    /// <summary>
    /// Golden wire-format vectors. The same hex strings are asserted by the TypeScript client's
    /// test suite (golden.test.ts) — together they pin the wire format across
    /// platforms. If a test here fails after an intentional format change, regenerate the vectors
    /// and update BOTH suites.
    /// </summary>
    public class GoldenVectorTests
    {
        //bool true, byte 0xAB, sbyte -12, short -12345, ushort 54321, int -123456789, uint 3123456789,
        //long -1234567890123456789, ulong 12345678901234567890, float 1.5, double -2.75, decimal 5.5,
        //string "Héllo ✓ LiteLink", null string, "" string, lengths 5/500/500000/null,
        //byte[]{1..5}, guid 01020304-0506-0708-090a-0b0c0d0e0f10 [, date 2024-01-02T03:04:05Z]
        public const string FullVectorHex =
            "01ABF4C7CF31D4EB32A4F8152B2CBAEB7E16820BEFDDEED20A1FEB8CA954AB0000C03F00000000000006C0" +
            "05000000000000000065CD1D9348C3A96C6C6F20E29C93204C6974654C696E6B00808541F42007A1200085" +
            "01020304050403020106050807090A0B0C0D0E0F1080C0BF7A3F0BDC48";

        public const string NoDateVectorHex =
            "01ABF4C7CF31D4EB32A4F8152B2CBAEB7E16820BEFDDEED20A1FEB8CA954AB0000C03F00000000000006C0" +
            "05000000000000000065CD1D9348C3A96C6C6F20E29C93204C6974654C696E6B00808541F42007A1200085" +
            "01020304050403020106050807090A0B0C0D0E0F10";

        private static void WriteVector(ref LiteWriter writer, bool includeDate)
        {
            writer.WriteBoolean(true);
            writer.WriteByte(0xAB);
            writer.WriteSByte(-12);
            writer.WriteInt16(-12345);
            writer.WriteUInt16(54321);
            writer.WriteInt32(-123456789);
            writer.WriteUInt32(3123456789);
            writer.WriteInt64(-1234567890123456789);
            writer.WriteUInt64(12345678901234567890);
            writer.WriteSingle(1.5f);
            writer.WriteDouble(-2.75);
            writer.WriteDecimal(5.5m);
            writer.WriteString("Héllo ✓ LiteLink");
            writer.WriteString(null);
            writer.WriteString("");
            writer.WriteLength(5);
            writer.WriteLength(500);
            writer.WriteLength(500000);
            writer.WriteLength(ulong.MaxValue);
            writer.WriteByteArray(new byte[] { 1, 2, 3, 4, 5 });
            writer.WriteGuid(Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10"));
            if (includeDate)
            {
                writer.WriteDate(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            }
        }

        [Fact]
        public void Writer_ProducesGoldenBytes()
        {
            var writer = new LiteWriter();
            WriteVector(ref writer, includeDate: true);

            Assert.Equal(FullVectorHex, Convert.ToHexString(writer.Close()));
        }

        [Fact]
        public void Reader_ParsesGoldenBytes()
        {
            var reader = new LiteReader(Convert.FromHexString(FullVectorHex));

            Assert.True(reader.ReadBoolean());
            Assert.Equal(0xAB, reader.ReadByte());
            Assert.Equal(-12, reader.ReadSByte());
            Assert.Equal(-12345, reader.ReadInt16());
            Assert.Equal(54321, reader.ReadUInt16());
            Assert.Equal(-123456789, reader.ReadInt32());
            Assert.Equal(3123456789, reader.ReadUInt32());
            Assert.Equal(-1234567890123456789, reader.ReadInt64());
            Assert.Equal(12345678901234567890, reader.ReadUInt64());
            Assert.Equal(1.5f, reader.ReadSingle());
            Assert.Equal(-2.75, reader.ReadDouble());
            Assert.Equal(5.5m, reader.ReadDecimal());
            Assert.Equal("Héllo ✓ LiteLink", reader.ReadString());
            Assert.Null(reader.ReadString());
            Assert.Equal("", reader.ReadString());
            Assert.Equal(5ul, reader.ReadLength());
            Assert.Equal(500ul, reader.ReadLength());
            Assert.Equal(500000ul, reader.ReadLength());
            Assert.Equal(ulong.MaxValue, reader.ReadLength());
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, reader.ReadByteArray());
            Assert.Equal(Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10"), reader.ReadGuid());
            Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), reader.ReadDate());
        }

        [Fact]
        public void NoDateVector_IsPrefixOfFullVector()
        {
            var writer = new LiteWriter();
            WriteVector(ref writer, includeDate: false);

            Assert.Equal(NoDateVectorHex, Convert.ToHexString(writer.Close()));
            Assert.StartsWith(NoDateVectorHex, FullVectorHex);
        }
    }
}
