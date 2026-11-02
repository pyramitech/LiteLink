import { describe, expect, it } from "vitest";
import { LiteReader, LiteWriter } from "../src/index";

describe("LiteWriter/LiteReader round trips", () => {
  it("round-trips primitives", () => {
    const writer = new LiteWriter();
    writer.writeBoolean(true);
    writer.writeBoolean(false);
    writer.writeByte(255);
    writer.writeSByte(-128);
    writer.writeInt16(-32768);
    writer.writeUInt16(65535);
    writer.writeInt32(-2147483648);
    writer.writeUInt32(4294967295);
    writer.writeInt64(-9223372036854775808n);
    writer.writeUInt64(18446744073709551615n);
    writer.writeSingle(1.5);
    writer.writeDouble(-2.75);
    const data = writer.close();

    const reader = new LiteReader(data);
    expect(reader.readBoolean()).toBe(true);
    expect(reader.readBoolean()).toBe(false);
    expect(reader.readByte()).toBe(255);
    expect(reader.readSByte()).toBe(-128);
    expect(reader.readInt16()).toBe(-32768);
    expect(reader.readUInt16()).toBe(65535);
    expect(reader.readInt32()).toBe(-2147483648);
    expect(reader.readUInt32()).toBe(4294967295);
    expect(reader.readInt64()).toBe(-9223372036854775808n);
    expect(reader.readUInt64()).toBe(18446744073709551615n);
    expect(reader.readSingle()).toBe(1.5);
    expect(reader.readDouble()).toBe(-2.75);
  });

  it.each([null, "", "plain ascii", "unicode ✓ ün 数据"])("round-trips string %j", (value) => {
    const writer = new LiteWriter();
    writer.writeString(value);
    const reader = new LiteReader(writer.close());
    expect(reader.readString()).toBe(value);
  });

  it("round-trips a string larger than the internal buffer", () => {
    const value = "ψ".repeat(50000) + "END";
    const writer = new LiteWriter();
    writer.writeString(value);
    writer.writeInt32(1234);
    const reader = new LiteReader(writer.close());
    expect(reader.readString()).toBe(value);
    expect(reader.readInt32()).toBe(1234);
  });

  it.each([0, 1, 0x7f, 0x80, 0x3fff, 0x4000, 0x1fffffff, null])("round-trips length %s", (value) => {
    const writer = new LiteWriter();
    writer.writeLength(value);
    const reader = new LiteReader(writer.close());
    expect(reader.readLength()).toBe(value);
  });

  it("writes lengths correctly at every buffer boundary offset", () => {
    // regression: writeLength used to skip the capacity check, silently dropping
    // the length byte when a collection length landed exactly at the buffer end
    for (let padding = 8185; padding <= 8195; padding++) {
      const writer = new LiteWriter();
      for (let i = 0; i < padding; i++) {
        writer.writeByte(0x11);
      }
      writer.writeLength(300000); //4-byte encoding
      writer.writeByte(0xee);

      const data = writer.close();
      const reader = new LiteReader(data);
      for (let i = 0; i < padding; i++) {
        reader.readByte();
      }
      expect(reader.readLength()).toBe(300000);
      expect(reader.readByte()).toBe(0xee);
    }
  });

  it("round-trips byte arrays", () => {
    const small = new Uint8Array([1, 2, 3]);
    const writer = new LiteWriter();
    writer.writeByteArray(small);
    const reader = new LiteReader(writer.close());
    expect(Array.from(reader.readByteArray()!)).toEqual([1, 2, 3]);
  });

  it.each([5.5, -5.5, 0, 123.25])("round-trips decimal %f", (value) => {
    const writer = new LiteWriter();
    writer.writeDecimal(value);
    const reader = new LiteReader(writer.close());
    expect(reader.readDecimal()).toBeCloseTo(value, 9);
  });

  it.each([
    [1.59, 1n, 590000000],
    [-1.59, -1n, -590000000],
    [19.95, 19n, 950000000],
    [0.1, 0n, 100000000],
    [1234567.89, 1234567n, 890000000]
  ])("writes decimal %f as the exact wire value", (value, integerPart, decimalPart) => {
    const writer = new LiteWriter();
    writer.writeDecimal(value as number);
    const bytes = writer.close();

    // truncation used to bake the double representation error into the wire value
    // (1.59 scaled to 589999999.99... and shipped as 589999999); the wire value must
    // match what C#'s exact decimal arithmetic produces for the same number
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    expect(view.getBigInt64(0, true)).toBe(integerPart);
    expect(view.getInt32(8, true)).toBe(decimalPart);
    expect(new LiteReader(bytes).readDecimal()).toBe(value);
  });

  it("carries a fraction that rounds to a full unit into the integer part", () => {
    const writer = new LiteWriter();
    writer.writeDecimal(1.9999999999); // beyond the format's 9-digit precision, rounds up to exactly 2
    writer.writeDecimal(-1.9999999999);
    const reader = new LiteReader(writer.close());
    expect(reader.readDecimal()).toBe(2);
    expect(reader.readDecimal()).toBe(-2);
  });

  it("round-trips dates and guids", () => {
    const date = new Date(Date.UTC(2024, 5, 15, 12, 30, 45));
    const guid = "01020304-0506-0708-090a-0b0c0d0e0f10";

    const writer = new LiteWriter();
    writer.writeDate(date);
    writer.writeGuid(guid);
    const reader = new LiteReader(writer.close());
    expect(reader.readDate().getTime()).toBe(date.getTime());
    expect(reader.readGuid()).toBe(guid);
  });

  it("reads correctly from an offset subarray view", () => {
    // regression: the DataView used to ignore byteOffset, misaligning multi-byte reads
    const writer = new LiteWriter();
    writer.writeInt32(-123456789);
    writer.writeUInt16(54321);
    const inner = writer.close();

    const padded = new Uint8Array(inner.length + 4);
    padded.set([9, 9, 9, 9], 0);
    padded.set(inner, 4);

    const reader = new LiteReader(padded.subarray(4));
    expect(reader.readInt32()).toBe(-123456789);
    expect(reader.readUInt16()).toBe(54321);
  });

  it("readRemaining returns the tail as a copy", () => {
    const writer = new LiteWriter();
    writer.writeInt32(1);
    writer.writeByte(0xaa);
    writer.writeByte(0xbb);
    const reader = new LiteReader(writer.close());
    reader.readInt32();
    expect(Array.from(reader.readRemaining())).toEqual([0xaa, 0xbb]);
  });
});

describe("compression", () => {
  it("compresses large messages through close() and decompresses them", () => {
    const payload = "z".repeat(10000);
    const writer = new LiteWriter();
    writer.writeUInt16(7);
    writer.writeByte(0); //compression placeholder
    writer.writeString(payload);
    const data = writer.close(3);

    expect(data.length).toBeLessThan(10000);

    const reader = new LiteReader(data);
    expect(reader.readUInt16()).toBe(7);
    expect(reader.readBoolean()).toBe(true);
    reader.decompress();
    expect(reader.readString()).toBe(payload);
  });

  it("leaves small messages uncompressed", () => {
    const writer = new LiteWriter();
    writer.writeUInt16(7);
    writer.writeByte(0);
    writer.writeString("tiny");
    const data = writer.close(3);

    const reader = new LiteReader(data);
    expect(reader.readUInt16()).toBe(7);
    expect(reader.readBoolean()).toBe(false);
    expect(reader.readString()).toBe("tiny");
  });
});
