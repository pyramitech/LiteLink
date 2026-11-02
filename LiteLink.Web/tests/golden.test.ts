import { describe, expect, it } from "vitest";
import { LiteReader, LiteWriter } from "../src/index";

// Golden wire-format vectors, produced by the C# LiteWriter and asserted byte-for-byte by
// LiteLink.Tests/GoldenVectorTests.cs — the two suites together pin the wire format across
// platforms. If an intentional format change breaks this, regenerate and update BOTH suites.

const FULL_VECTOR_HEX =
  "01ABF4C7CF31D4EB32A4F8152B2CBAEB7E16820BEFDDEED20A1FEB8CA954AB0000C03F00000000000006C0" +
  "05000000000000000065CD1D9348C3A96C6C6F20E29C93204C6974654C696E6B00808541F42007A1200085" +
  "01020304050403020106050807090A0B0C0D0E0F1080C0BF7A3F0BDC48";

// same vector without the trailing date: C# encodes the DateTimeKind in the date's high bits,
// so date bytes are asserted read-side only; everything else must be written identically
const NO_DATE_VECTOR_HEX =
  "01ABF4C7CF31D4EB32A4F8152B2CBAEB7E16820BEFDDEED20A1FEB8CA954AB0000C03F00000000000006C0" +
  "05000000000000000065CD1D9348C3A96C6C6F20E29C93204C6974654C696E6B00808541F42007A1200085" +
  "01020304050403020106050807090A0B0C0D0E0F10";

const fromHex = (hex: string): Uint8Array => {
  const bytes = new Uint8Array(hex.length / 2);
  for (let i = 0; i < bytes.length; i++) {
    bytes[i] = parseInt(hex.substring(i * 2, i * 2 + 2), 16);
  }
  return bytes;
};

const toHex = (bytes: Uint8Array): string =>
  Array.from(bytes)
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("")
    .toUpperCase();

describe("cross-platform golden vectors", () => {
  it("parses the C#-produced golden bytes", () => {
    const reader = new LiteReader(fromHex(FULL_VECTOR_HEX));

    expect(reader.readBoolean()).toBe(true);
    expect(reader.readByte()).toBe(0xab);
    expect(reader.readSByte()).toBe(-12);
    expect(reader.readInt16()).toBe(-12345);
    expect(reader.readUInt16()).toBe(54321);
    expect(reader.readInt32()).toBe(-123456789);
    expect(reader.readUInt32()).toBe(3123456789);
    expect(reader.readInt64()).toBe(-1234567890123456789n);
    expect(reader.readUInt64()).toBe(12345678901234567890n);
    expect(reader.readSingle()).toBe(1.5);
    expect(reader.readDouble()).toBe(-2.75);
    expect(reader.readDecimal()).toBe(5.5);
    expect(reader.readString()).toBe("Héllo ✓ LiteLink");
    expect(reader.readString()).toBeNull();
    expect(reader.readString()).toBe("");
    expect(reader.readLength()).toBe(5);
    expect(reader.readLength()).toBe(500);
    expect(reader.readLength()).toBe(500000);
    expect(reader.readLength()).toBeNull();
    expect(Array.from(reader.readByteArray()!)).toEqual([1, 2, 3, 4, 5]);
    expect(reader.readGuid()).toBe("01020304-0506-0708-090a-0b0c0d0e0f10");
    expect(reader.readDate().getTime()).toBe(Date.UTC(2024, 0, 2, 3, 4, 5));
    expect(reader.isFinished()).toBe(true);
  });

  it("writes bytes identical to the C# writer", () => {
    const writer = new LiteWriter();
    writer.writeBoolean(true);
    writer.writeByte(0xab);
    writer.writeSByte(-12);
    writer.writeInt16(-12345);
    writer.writeUInt16(54321);
    writer.writeInt32(-123456789);
    writer.writeUInt32(3123456789);
    writer.writeInt64(-1234567890123456789n);
    writer.writeUInt64(12345678901234567890n);
    writer.writeSingle(1.5);
    writer.writeDouble(-2.75);
    writer.writeDecimal(5.5);
    writer.writeString("Héllo ✓ LiteLink");
    writer.writeString(null);
    writer.writeString("");
    writer.writeLength(5);
    writer.writeLength(500);
    writer.writeLength(500000);
    writer.writeLength(null);
    writer.writeByteArray(new Uint8Array([1, 2, 3, 4, 5]));
    writer.writeGuid("01020304-0506-0708-090a-0b0c0d0e0f10");

    expect(toHex(writer.close())).toBe(NO_DATE_VECTOR_HEX);
  });
});
