using LiteLink.Shared.Func;
using Xunit;

namespace LiteLink.Tests
{
    /// <summary>
    /// CRC stability vectors: function/permission hashing is part of the wire protocol,
    /// so these values must never change. The TypeScript suite asserts the same table.
    /// </summary>
    public class CrcTests
    {
        [Theory]
        [InlineData("FooProcedures.ReturnTask", 118, 62438, 196060548u, 12256657908591851401ul)]
        [InlineData("CanView", 124, 38299, 1924038373u, 13501351349303493099ul)]
        [InlineData("a", 211, 40311, 3904355907u, 3675645893302102789ul)]
        [InlineData("Authentication.Authenticate", 112, 65504, 3872992521u, 11714893853636825607ul)]
        [InlineData("CompanyProfile.GetManagers", 229, 1476, 725045349u, 6315610395286191049ul)]
        public void CrcValues_AreStable(string input, byte crc8, ushort crc16, uint crc32, ulong crc64)
        {
            Assert.Equal(crc8, CRCFunctions.CalculateCRC8(input));
            Assert.Equal(crc16, CRCFunctions.CalculateCRC16(input));
            Assert.Equal(crc32, CRCFunctions.CalculateCRC32(input));
            Assert.Equal(crc64, CRCFunctions.CalculateCRC64(input));
        }
    }
}
