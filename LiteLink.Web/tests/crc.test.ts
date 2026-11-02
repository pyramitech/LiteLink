import { describe, expect, it } from "vitest";
import { CRCHelper } from "../src/index";

// CRC stability vectors: function/permission hashing is part of the wire protocol.
// The same table is asserted against the C# CRCFunctions in LiteLink.Tests/CrcTests.cs.
const vectors: [string, bigint, bigint, bigint, bigint][] = [
  ["FooProcedures.ReturnTask", 118n, 62438n, 196060548n, 12256657908591851401n],
  ["CanView", 124n, 38299n, 1924038373n, 13501351349303493099n],
  ["a", 211n, 40311n, 3904355907n, 3675645893302102789n],
  ["Authentication.Authenticate", 112n, 65504n, 3872992521n, 11714893853636825607n],
  ["CompanyProfile.GetManagers", 229n, 1476n, 725045349n, 6315610395286191049n]
];

describe("CRC parity with the C# implementation", () => {
  it.each(vectors)("%s", (input, crc8, crc16, crc32, crc64) => {
    expect(CRCHelper.calculateCRC8(input)).toBe(crc8);
    expect(CRCHelper.calculateCRC16(input)).toBe(crc16);
    expect(CRCHelper.calculateCRC32(input)).toBe(crc32);
    expect(CRCHelper.calculateCRC64(input)).toBe(crc64);
  });
});
