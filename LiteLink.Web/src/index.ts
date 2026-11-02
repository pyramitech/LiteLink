/* eslint-disable no-else-return */
/* eslint-disable class-methods-use-this */
/* eslint-disable no-restricted-syntax */
/* eslint-disable no-param-reassign */
/* eslint-disable no-bitwise */
/* eslint-disable no-plusplus */
/* eslint-disable max-lines */
// Copyright (c) Pyrami Technologies
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//    http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.


//require in client: npm i -D vite-plugin-wasm vite-plugin-top-level-await

import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  MessageType,
  TransferFormat
} from "@microsoft/signalr";
import type {
  CancelInvocationMessage,
  CloseMessage,
  CompletionMessage,
  HubMessage,
  IHubProtocol,
  InvocationMessage,
  MessageHeaders,
  StreamInvocationMessage,
  StreamItemMessage
} from "@microsoft/signalr";
import * as wasm from "lz4-wasm";

class MessageHeadersImpl implements MessageHeaders {
  /** Gets or sets the header with the specified key. */
  [key: string]: string;
}

export enum StorageType {
  Normal = 0,
  Array = 1,
  List = 2,
  Dictionary = 3,
  Nullable = 4,
  Tuple = 5
}

export enum SpecialType {
  Normal = 0,
  Dictionary = 1,
  Collection = 2,
  Tuple = 3,
  Nullable = 4
}

export class TypeMap {
  specialType: SpecialType = SpecialType.Normal;

  types: number[];

  basicType: BasicTypes;

  properties: PropertyMap[];
}

export class PropertyMap {
  storageType: StorageType;

  propertyIdx: number;

  typeIdx: number;
}

export class FunctionMap {
  opcode: number;

  argType: number;

  retType: number;
}

type TBasicTypes =
  | boolean
  | number
  | bigint
  | string
  | Date
  | Uint8Array
  | number[]
  | Record<string, string | number[] | null>
  | Map<string, TBasicTypes>
  | null
  | undefined;

export enum BasicTypes {
  Bool = 0,
  SByte,
  Byte,
  Int16,
  UInt16,
  Int32,
  UInt32,
  Int64,
  UInt64,
  Single,
  Double,
  Decimal,
  String,
  DateTime,
  Guid,
  Object
}

export class LiteSerializer {
  private static _propertyMaps: string[];

  private static _typeMaps: TypeMap[];

  /** property name table from the handshake, exposed read-only for tooling (e.g. API browsers) */
  static get propertyMaps(): string[] {
    return LiteSerializer._propertyMaps;
  }

  /** type table from the handshake, exposed read-only for tooling (e.g. API browsers) */
  static get typeMaps(): TypeMap[] {
    return LiteSerializer._typeMaps;
  }

  static setData = (propertyMap: string[], typeMap: TypeMap[]): void => {
    LiteSerializer._propertyMaps = propertyMap;
    LiteSerializer._typeMaps = typeMap;
  };

  static isTypeNullable = (type: number): boolean => {
    const typeMap = LiteSerializer.typeMaps[type];
    return typeMap.basicType === BasicTypes.Object || typeMap.basicType === BasicTypes.String;
  };

  static serialize = (writer: LiteWriter, type: number, value: TBasicTypes): void => {
    const typeMap = LiteSerializer.typeMaps[type];
    if (typeMap.basicType === BasicTypes.Object) {
      if (typeMap.specialType === SpecialType.Dictionary) {
        writer.writeDictionary(value as Map<string, TBasicTypes>, typeMap.types[0], typeMap.types[1]);
      } else if (typeMap.specialType === SpecialType.Collection) {
        writer.writeCollection(value as TBasicTypes[], typeMap.types[0]);
      } else if (typeMap.specialType === SpecialType.Tuple) {
        writer.writeTuple(value as TBasicTypes[], typeMap.types);
      } else if (typeMap.specialType === SpecialType.Nullable) {
        writer.writeNullable(value, typeMap.types[0]);
      } else {
        // eslint-disable-next-line no-lonely-if
        if (value) {
          writer.writeByte(1); // not null
          for (const map of typeMap.properties) {
            const property: TBasicTypes = (value as Record<string, string | null | number[]>)[
              LiteSerializer.propertyMaps[map.propertyIdx]
            ];
            if (map.storageType === StorageType.Array || map.storageType === StorageType.List) {
              writer.writeCollection(property as TBasicTypes[], map.typeIdx);
            } else if (map.storageType === StorageType.Nullable) {
              writer.writeNullable(property, map.typeIdx);
            } else if (map.storageType === StorageType.Tuple) {
              this.serialize(writer, map.typeIdx, property);
            } else {
              // else if (map.storageType == StorageType.Dictionary) {
              //    writer.writeDictionary(property, typeMap.types[0], typeMap.types[1]);
              // }
              this.serialize(writer, map.typeIdx, property);
            }
          }
        } else {
          writer.writeByte(0);
        } // null
      }
    } else {
      switch (typeMap.basicType) {
        case BasicTypes.Bool:
          writer.writeBoolean(value as boolean);
          break;
        case BasicTypes.SByte:
          writer.writeSByte(value as number);
          break;
        case BasicTypes.Byte:
          writer.writeByte(value as number);
          break;
        case BasicTypes.Int16:
          writer.writeInt16(value as number);
          break;
        case BasicTypes.UInt16:
          writer.writeUInt16(value as number);
          break;
        case BasicTypes.Int32:
          writer.writeInt32(value as number);
          break;
        case BasicTypes.UInt32:
          writer.writeUInt32(value as number);
          break;
        case BasicTypes.Int64:
          writer.writeInt64(value as bigint);
          break;
        case BasicTypes.UInt64:
          writer.writeUInt64(value as bigint);
          break;
        case BasicTypes.Single:
          writer.writeSingle(value as number);
          break;
        case BasicTypes.Double:
          writer.writeDouble(value as number);
          break;
        case BasicTypes.Decimal:
          writer.writeDecimal(value as number);
          break;
        case BasicTypes.String:
          writer.writeString(value as string);
          break;
        case BasicTypes.DateTime:
          writer.writeDate(value as Date);
          break;
        case BasicTypes.Guid:
          writer.writeGuid(value as string);
          break;
        default:
          throw new Error(`Unknown basic type: ${type}`);
      }
    }
  };

  static deserialize(reader: LiteReader, type: number): any {
    const typeMap = LiteSerializer.typeMaps[type];
    if (typeMap.basicType === BasicTypes.Object) {
      if (typeMap.specialType === SpecialType.Dictionary) {
        return reader.readDictionary(typeMap.types[0], typeMap.types[1]);
      } else if (typeMap.specialType === SpecialType.Collection) {
        return reader.readCollection(typeMap.types[0]);
      } else if (typeMap.specialType === SpecialType.Tuple) {
        return reader.readTuple(typeMap.types);
      } else if (typeMap.specialType === SpecialType.Nullable) {
        return reader.readNullable(typeMap.types[0]);
      } else if (reader.readByte() === 1) {
        const res: Record<string, string | null | number[]> = {};
        for (const map of typeMap.properties) {
          // const property = value[];
          if (map.storageType === StorageType.Array || map.storageType === StorageType.List) {
            res[LiteSerializer.propertyMaps[map.propertyIdx]] = reader.readCollection(map.typeIdx);
          } else if (map.storageType === StorageType.Nullable) {
            res[LiteSerializer.propertyMaps[map.propertyIdx]] = reader.readNullable(map.typeIdx);
          } else if (map.storageType === StorageType.Tuple) {
            res[LiteSerializer.propertyMaps[map.propertyIdx]] = this.deserialize(reader, map.typeIdx);
          } else {
            // else if (map.storageType == StorageType.Dictionary) {
            //    writer.writeDictionary(property, typeMap.types[0], typeMap.types[1]);
            // }
            res[LiteSerializer.propertyMaps[map.propertyIdx]] = this.deserialize(reader, map.typeIdx);
          }
        }
        return res;
      } else {
        return null;
      }
    } else {
      switch (typeMap.basicType) {
        case BasicTypes.Bool:
          return reader.readBoolean();
        case BasicTypes.SByte:
          return reader.readSByte();
        case BasicTypes.Byte:
          return reader.readByte();
        case BasicTypes.Int16:
          return reader.readInt16();
        case BasicTypes.UInt16:
          return reader.readUInt16();
        case BasicTypes.Int32:
          return reader.readInt32();
        case BasicTypes.UInt32:
          return reader.readUInt32();
        case BasicTypes.Int64:
          return reader.readInt64();
        case BasicTypes.UInt64:
          return reader.readUInt64();
        case BasicTypes.Single:
          return reader.readSingle();
        case BasicTypes.Double:
          return reader.readDouble();
        case BasicTypes.Decimal:
          return reader.readDecimal();
        case BasicTypes.String:
          return reader.readString();
        case BasicTypes.DateTime:
          return reader.readDate();
        case BasicTypes.Guid:
          return reader.readGuid();
        default:
          throw new Error(`Unknown basic type: ${JSON.stringify(typeMap.basicType)}`);
      }
    }
  }
}

class LitelinkBuffer {
  static BUFFER_SIZE = 8192;

  array: Uint8Array;

  length: number = 0;

  constructor(arr?: Uint8Array | number) {
    if (!arr) {
      this.array = new Uint8Array(LitelinkBuffer.BUFFER_SIZE);
    } else if (typeof arr === "object") {
      this.array = arr;
    } else {
      this.array = new Uint8Array(arr);
    }
  }
}

export class LiteWriter {
  private buffers: LitelinkBuffer[];

  private buffer: LitelinkBuffer;

  private array: Uint8Array;

  private view: DataView;

  private index: number;

  private length: number;

  constructor(arr?: Uint8Array) {
    if (arr) {
      this.array = arr;
      this.buffer = new LitelinkBuffer(arr);
    } else {
      this.buffer = new LitelinkBuffer();
      this.array = this.buffer.array;
    }
    this.buffers = [];
    this.view = new DataView(this.array.buffer);
    this.index = 0;
    this.length = this.array.length;
  }

  close = (headerOffset: number = -1): Uint8Array => {
    const finSize = this.buffers.reduce((acc: number, obj: LitelinkBuffer) => acc + obj.length, 0) + this.index;
    const arr = new Uint8Array(finSize);
    let ix = 0;

    for (let i = 0; i < this.buffers.length; i++) {
      const buf = this.buffers[i];
      arr.set(buf.array.subarray(0, buf.length), ix);
      ix += buf.length;
    }

    arr.set(this.array.subarray(0, this.index), ix);

    if (headerOffset !== -1 && arr.length > 80) {
      // lz4-wasm emits [u32 LE uncompressed size][lz4 block], which is exactly the layout
      // the server's LiteReader.Decompress expects after the isCompressed flag
      const compressed = wasm.compress(arr.subarray(headerOffset));

      // only send compressed when it actually shrinks the message
      if (headerOffset + compressed.length < arr.length) {
        const res = new Uint8Array(headerOffset + compressed.length);
        res.set(arr.subarray(0, headerOffset), 0);
        res[headerOffset - 1] = 1; // the byte before the payload is the isCompressed flag
        res.set(compressed, headerOffset);
        return res;
      }
    }

    return arr;
  };

  expandBuffer = (len?: number): void => {
    this.buffer.length = this.index;
    this.buffers.push(this.buffer);

    if (len) {
      this.buffer = new LitelinkBuffer(len);
    } else {
      this.buffer = new LitelinkBuffer();
    }
    this.array = this.buffer.array;
    this.view = new DataView(this.array.buffer);
    this.index = 0;
    this.length = this.array.length;
  };

  writeBoolean = (value: boolean): void => {
    this.writeByte(value ? 1 : 0);
  };

  writeByte = (value: number): void => {
    if (this.index + 1 > this.length) {
      this.expandBuffer();
    }
    this.array[this.index++] = value;
  };

  writeSByte = (value: number): void => {
    if (this.index + 1 > this.length) {
      this.expandBuffer();
    }
    this.view.setInt8(this.index++, value);
  };

  writeInt16 = (value: number): void => {
    if (this.index + 2 > this.length) {
      this.expandBuffer();
    }
    this.view.setInt16(this.index, value, true);
    this.index += 2;
  };

  writeUInt16 = (value: number): void => {
    if (this.index + 2 > this.length) {
      this.expandBuffer();
    }
    this.view.setUint16(this.index, value, true);
    this.index += 2;
  };

  writeInt32 = (value: number): void => {
    if (this.index + 4 > this.length) {
      this.expandBuffer();
    }
    this.view.setInt32(this.index, value, true);
    this.index += 4;
  };

  writeUInt32 = (value: number): void => {
    if (this.index + 4 > this.length) {
      this.expandBuffer();
    }
    this.view.setUint32(this.index, value, true);
    this.index += 4;
  };

  writeInt64 = (value: bigint): void => {
    if (this.index + 8 > this.length) {
      this.expandBuffer();
    }
    this.view.setBigInt64(this.index, BigInt(value), true);
    this.index += 8;
  };

  writeUInt64 = (value: bigint): void => {
    if (this.index + 8 > this.length) {
      this.expandBuffer();
    }
    this.view.setBigUint64(this.index, BigInt(value), true);
    this.index += 8;
  };

  writeSingle = (value: number): void => {
    if (this.index + 4 > this.length) {
      this.expandBuffer();
    }
    this.view.setFloat32(this.index, value, true);
    this.index += 4;
  };

  writeDouble = (value: number): void => {
    if (this.index + 8 > this.length) {
      this.expandBuffer();
    }
    this.view.setFloat64(this.index, value, true);
    this.index += 8;
  };

  writeDecimal = (value: number): void => {
    if (this.index + 12 > this.length) {
      this.expandBuffer();
    }
    let integerPart = Math.trunc(value);
    // round instead of letting setInt32 truncate: doubles like 1.59 scale to 589999999.99...,
    // and truncation would bake that representation error into the wire value
    let decimalPart = Math.round((value - integerPart) * 1_000_000_000);

    // rounding can push the fraction to a full unit (e.g. 1.9999999999); carry it over
    if (decimalPart >= 1_000_000_000) {
      integerPart += 1;
      decimalPart = 0;
    } else if (decimalPart <= -1_000_000_000) {
      integerPart -= 1;
      decimalPart = 0;
    }

    this.view.setBigInt64(this.index, BigInt(integerPart), true);
    this.view.setInt32(this.index + 8, decimalPart, true);

    this.index += 12;
  };

  writeGuid = (value: string): void => {
    if (this.index + 16 > this.length) {
      this.expandBuffer();
    }

    const guidRaw = value.replace(/-/g, "");

    const bytes = new Uint8Array(16);
    let j = 0;
    for (let i = 0; i < guidRaw.length; i += 2) {
      bytes[j++] = parseInt(guidRaw[i] + guidRaw[i + 1], 16);
    }

    this.view.setUint32(this.index, (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3], true);
    this.index += 4;
    this.view.setUint16(this.index, (bytes[4] << 8) | bytes[5], true);
    this.index += 2;
    this.view.setUint16(this.index, (bytes[6] << 8) | bytes[7], true);
    this.index += 2;

    this.view.setUint32(this.index, (bytes[11] << 24) | (bytes[10] << 16) | (bytes[9] << 8) | bytes[8], true);
    this.index += 2;
    this.index += 2;
    this.view.setUint32(this.index, (bytes[15] << 24) | (bytes[14] << 16) | (bytes[13] << 8) | bytes[12], true);
    this.index += 4;
  };

  writeDate = (value: Date): void => {
    const timestamp = BigInt(value.getTime());
    const ticks = (timestamp + 62135596800000n) * 10000n;
    // eslint-disable-next-line max-len
    this.writeUInt64(ticks & 0x3fffffffffffffffn); // bits 63-64 are used for DateTimeKind description, so let's zero them out
  };

  writeByteArray = (value: Uint8Array): void => {
    const strLen = value.length + 8;
    if (this.index + strLen > this.length) {
      if (strLen > this.length) {
        this.expandBuffer(strLen);
      } else {
        this.expandBuffer();
      }
    }

    this.writeLength(value.length);
    this.array.set(value, this.index);
    this.index += value.length;
  };

  writeCollection = (value: TBasicTypes[], type: number): void => {
    if (value == null) {
      this.writeLength(null);
    } else {
      this.writeLength(value.length);
      for (let i = 0; i < value.length; i++) {
        LiteSerializer.serialize(this, type, value[i]);
      }
    }
  };

  writeTuple = (value: TBasicTypes[], types: number[]): void => {
    for (let i = 0; i < value.length; i++) {
      LiteSerializer.serialize(this, types[i], value[i]);
    }
  };

  // value = map
  writeDictionary = (value: Map<string, TBasicTypes>, typeKey: number, typeVal: number): void => {
    if (value === null) {
      this.writeLength(null);
    } else {
      this.writeLength(value.size);
      for (const [key, val] of value) {
        LiteSerializer.serialize(this, typeKey, key);
        LiteSerializer.serialize(this, typeVal, val);
      }
    }
  };

  writeNullable(value: TBasicTypes, type: number): void {
    if (value == null) {
      this.writeByte(0);
    } else {
      this.writeByte(1);
      LiteSerializer.serialize(this, type, value);
    }
  }

  writeLength = (len: number | null): void => {
    // the encoding is up to 8 bytes; callers like writeCollection don't pre-reserve room
    if (this.index + 8 > this.length) {
      this.expandBuffer();
    }

    if (len == null) {
      // since we can pass a number as null in javascript, null means null
      this.array[this.index++] = 0;
      return;
    }

    if (len < 0x80) {
      len |= 0x80;
      this.array[this.index++] = len;
    } else if (len < 0x4000) {
      len |= 0x4000;
      this.view.setUint16(this.index, len, false);
      this.index += 2;
    } else if (len < 0x20000000) {
      len |= 0x20000000;
      this.view.setUint32(this.index, len, false);
      this.index += 4;
    } else {
      // 8 bytes big-endian with the 0x10 flag in the first byte, mirrors the C# writer
      this.view.setBigUint64(this.index, BigInt(len) | 0x1000000000000000n, false);
      this.index += 8;
    }
  };

  writeString = (value: string | null): void => {
    if (value == null) {
      if (this.index + 1 > this.length) {
        this.expandBuffer();
      }
      this.writeLength(null);
      return;
    }

    const encoder = new TextEncoder();
    const encodedData = encoder.encode(value);
    let strLen = encodedData.length + 8;
    if (this.index + strLen > this.length) {
      if (strLen > this.length) {
        this.expandBuffer(strLen);
      } else {
        this.expandBuffer();
      }
    }

    strLen -= 8;

    this.writeLength(strLen);
    this.array.set(encodedData, this.index);
    this.index += strLen;
  };
}

export class LiteReader {
  private array: Uint8Array;

  private view: DataView;

  private index: number;

  private length: number;

  constructor(input: Uint8Array) {
    this.array = input;
    // honor byteOffset/byteLength so readers over subarray views don't misalign multi-byte reads
    this.view = new DataView(input.buffer, input.byteOffset, input.byteLength);
    this.index = 0;
    this.length = this.array.length;
  }

  decompress(): void {
    this.array = wasm.decompress(this.array.subarray(this.index));
    this.view = new DataView(this.array.buffer, this.array.byteOffset, this.array.byteLength);
    this.index = 0;
    this.length = this.array.length;
  }

  isFinished(): boolean {
    return this.index >= this.length;
  }

  canReadByte(numBytes: number): boolean {
    return this.index + numBytes <= this.length;
  }

  readBoolean(): boolean {
    return this.readByte() === 1;
  }

  readByte(): number {
    return this.array[this.index++];
  }

  readSByte(): number {
    return this.view.getInt8(this.index++);
  }

  readInt16(): number {
    const res = this.view.getInt16(this.index, true);
    this.index += 2;
    return res;
  }

  readUInt16(): number {
    const res = this.view.getUint16(this.index, true);
    this.index += 2;
    return res;
  }

  readInt32(): number {
    const res = this.view.getInt32(this.index, true);
    this.index += 4;
    return res;
  }

  readUInt32(): number {
    const res = this.view.getUint32(this.index, true);
    this.index += 4;
    return res;
  }

  readInt64(): bigint {
    const res = this.view.getBigInt64(this.index, true);
    this.index += 8;
    return res;
  }

  readUInt64(): bigint {
    const res = this.view.getBigUint64(this.index, true);
    this.index += 8;
    return res;
  }

  readSingle(): number {
    const res = this.view.getFloat32(this.index, true);
    this.index += 4;
    return res;
  }

  readDouble(): number {
    const res = this.view.getFloat64(this.index, true);
    this.index += 8;
    return res;
  }

  readDecimal(): number {
    const integerPart = this.view.getBigInt64(this.index, true);
    const decimalPart = this.view.getInt32(this.index + 8, true);
    this.index += 12;

    // dividing the scaled total once rounds to the intended value (1.59), while summing
    // integer + fraction/1e9 can land one ulp off (1.5899999999999999); the single division
    // is exact while the scaled total fits a double's 2^53 integer range
    const scaled = integerPart * 1_000_000_000n + BigInt(decimalPart);
    if (scaled >= -9_007_199_254_740_992n && scaled <= 9_007_199_254_740_992n) {
      return Number(scaled) / 1_000_000_000;
    }

    // beyond that range the fraction is below the double's precision anyway
    return Number(integerPart) + decimalPart / 1_000_000_000;
  }

  readLength(): number | null {
    const len = this.array[this.index];

    if (len === 0) {
      this.index++;
      return null;
    } else if ((len & 0x80) !== 0) {
      this.index++;
      return len ^ 0x80;
    } else if ((len & 0x40) !== 0) {
      const res = this.view.getUint16(this.index, false) ^ 0x4000;
      this.index += 2;
      return res;
    } else if ((len & 0x20) !== 0) {
      const res = this.view.getUint32(this.index, false) ^ 0x20000000;
      this.index += 4;
      return res;
    } else {
      const res = this.view.getBigUint64(this.index, false) ^ 0x1000000000000000n;
      this.index += 8;
      return Number(res);
    }
  }

  readString(): string | null {
    const len = this.readLength();
    if (len == null) {
      return null;
    }

    const decoder = new TextDecoder("utf-8");

    const res = decoder.decode(this.array.subarray(this.index, this.index + len));
    this.index += len;

    return res;
  }

  readDate(): Date {
    const timestamp = (this.readInt64() & 0x3fffffffffffffffn) / 10000n - 62135596800000n;
    return new Date(Number(timestamp));
  }

  readGuid(): string {
    const f1 = this.view.getUint32(this.index, true);
    const f2 = this.view.getUint16(this.index + 4, true);
    const f3 = this.view.getUint16(this.index + 6, true);
    const f4 = this.view.getUint16(this.index + 8, false);
    const f5 = this.view.getUint16(this.index + 10, false);
    const f6 = this.view.getUint32(this.index + 12, false);

    this.index += 16;

    return `${`00000000${f1.toString(16)}`.substr(-8)}-${`0000${f2.toString(16)}`.substr(-4)}-${`0000${f3.toString(
      16
    )}`.substr(-4)}-${`0000${f4.toString(16)}`.substr(-4)}-${`0000${f5.toString(16)}`.substr(
      -4
    )}${`00000000${f6.toString(16)}`.substr(-8)}`;
  }

  readByteArray(): Uint8Array | null {
    const len = this.readLength();
    if (len === null) {
      return null;
    }

    const res = this.array.slice(this.index, this.index + len);
    this.index += len;

    return res;
  }

  /**
   * returns a copy of everything from the current position to the end of the buffer,
   * a copy (not a subarray) so consumers that hold onto it don't retain the whole original buffer
   */
  readRemaining(): Uint8Array {
    const res = this.array.slice(this.index);
    this.index = this.length;
    return res;
  }

  readTuple(types: number[]): number[] {
    const res: number[] = [];

    for (let i = 0; i < types.length; i++) {
      res.push(LiteSerializer.deserialize(this, types[i]) as number);
    }

    return res;
  }

  readCollection(type: number): number[] | null {
    const len = this.readLength();
    if (len === null) {
      return null;
    }

    const res: number[] = [];

    for (let i = 0; i < len; i++) {
      res.push(LiteSerializer.deserialize(this, type) as number);
    }

    return res;
  }

  readDictionary(typeKey: number, typeVal: number): Map<string, TBasicTypes> | null {
    const len = this.readLength();
    if (len === null) {
      return null;
    }

    const res = new Map<string, TBasicTypes>();

    for (let i = 0; i < len; i++) {
      const key = LiteSerializer.deserialize(this, typeKey) as string;
      const value = LiteSerializer.deserialize(this, typeVal) as TBasicTypes;
      res.set(key, value);
    }

    return res;
  }

  readNullable(type: number): any {
    if (this.readByte() === 1) {
      return LiteSerializer.deserialize(this, type);
    }

    return null;
  }
}
export class LiteLinkHubProtocol implements IHubProtocol {
  public readonly name: string = "litelink";

  public readonly version: number = 1;

  public readonly transferFormat: TransferFormat = TransferFormat.Binary;

  private readonly _errorResult = 1;

  private readonly _voidResult = 2;

  private readonly _nonVoidResult = 3;

  parseMessages(input: string | ArrayBuffer): HubMessage[] {
    if (!this.isArrayBuffer(input)) {
      throw new Error("Invalid input for LiteLink hub protocol. Expected an ArrayBuffer.");
    }

    const reader = new LiteReader(new Uint8Array(input as ArrayBuffer));

    const hubMessages = [];

    while (!reader.isFinished()) {
      const msgLen = reader.readUInt32();
      if (!reader.canReadByte(msgLen)) {
        throw new Error("message length is less than the buffer");
      }

      const msgType = reader.readByte();

      // missing stream invocation? but do we even care?
      switch (msgType) {
        case MessageType.Invocation:
          hubMessages.push(this.createInvocationMessage(reader));
          break;
        case MessageType.StreamItem:
          hubMessages.push(this.createStreamItemMessage(reader));
          break;
        case MessageType.Completion:
          hubMessages.push(this.createCompletionMessage(reader));
          break;
        case MessageType.Ping:
          hubMessages.push(this.createPingMessage());
          break;
        case MessageType.Close:
          hubMessages.push(this.createCloseMessage(reader));
          break;
        default:
          console.error(`Unknown message type: ${msgType}`);
          break;
      }
    }

    return hubMessages;
  }

  private createInvocationMessage(reader: LiteReader): InvocationMessage {
    const headers = this.readHeaders(reader) as MessageHeaders;
    const invocationId = reader.readString();

    const args = this.bindArguments(reader);
    const streams = this.readStreamIds(reader);

    if (invocationId) {
      return {
        arguments: args,
        headers,
        invocationId,
        streamIds: streams,
        target: "Data",
        type: MessageType.Invocation
      };
    }
    return {
      arguments: args,
      headers,
      streamIds: streams,
      target: "Data",
      type: MessageType.Invocation
    };
  }

  private createStreamItemMessage(reader: LiteReader): StreamItemMessage {
    const headers = this.readHeaders(reader);
    const invocationId = reader.readString();
    const value = reader.readByteArray();
    return {
      headers,
      invocationId,
      item: value,
      type: MessageType.StreamItem
    } as StreamItemMessage;
  }

  private createCompletionMessage(reader: LiteReader): CompletionMessage {
    const headers = this.readHeaders(reader) as MessageHeaders;
    const invocationId = reader.readString() as string;
    const resultKind = reader.readByte();

    let error: string | undefined;
    let result: CompletionMessage["result"];

    switch (resultKind) {
      case this._errorResult:
        error = reader.readString() as string;
        break;
      case this._nonVoidResult:
        if (reader.readByte() === 1) {
          result = reader.readByteArray();
        }
        break;
      default:
        console.error(`Unknown result: ${resultKind}`);
        break;
    }

    const completionMessage: CompletionMessage = {
      error,
      headers,
      invocationId,
      result,
      type: MessageType.Completion
    };

    return completionMessage;
  }

  private createCloseMessage = (reader: LiteReader): HubMessage => {
    const error = reader.readString();
    const allowReconnect = reader.readBoolean();

    return {
      // Close messages have no headers.
      allowReconnect,
      error,
      type: MessageType.Close
    } as HubMessage;
  };

  private createPingMessage(): HubMessage {
    return {
      // Ping messages have no headers.
      type: MessageType.Ping
    } as HubMessage;
  }

  private readStreamIds(reader: LiteReader): string[] {
    const streamCount = reader.readLength() as number;
    const res: string[] = [];
    for (let i = 0; i < streamCount; i++) {
      res.push(reader.readString() as string);
    }
    return res;
  }

  private bindArguments(reader: LiteReader): (Uint8Array | null)[] {
    const res = [];
    if (reader.readByte() === 1) {
      res.push(reader.readByteArray());
    }

    return res;
  }

  private readHeaders(reader: LiteReader): MessageHeaders | null {
    const headerCount = reader.readLength() as number;
    if (headerCount > 0) {
      const headers = new MessageHeadersImpl();
      for (let i = 0; i < headerCount; i++) {
        const key = reader.readString() as string;
        const value = reader.readString() as string;
        headers[key] = value;
      }
      return headers;
    }
    return null;
  }

  writeMessage = (message: HubMessage): string | ArrayBuffer => {
    let writer = new LiteWriter();
    // placeholder len
    writer.writeUInt32(0);

    switch (message.type) {
      case MessageType.Invocation:
        this.writeInvocation(message, writer);
        break;
      case MessageType.StreamInvocation:
        this.writeStreamInvocation(message, writer);
        break;
      case MessageType.StreamItem:
        this.writeStreamItem(message, writer);
        break;
      case MessageType.Completion:
        this.writeCompletion(message, writer);
        break;
      case MessageType.Ping:
        this.writePing(writer);
        break;
      case MessageType.CancelInvocation:
        this.writeCancelInvocation(message, writer);
        break;
      case MessageType.Close:
        this.writeClose(message, writer);
        break;
      default:
        throw new Error("Invalid message type.");
    }

    const arr = writer.close();
    writer = new LiteWriter(arr);
    writer.writeUInt32(arr.length - 4);

    // SignalR accepts any BufferSource at runtime; TypeScript 5.7+ no longer treats a
    // Uint8Array as an ArrayBuffer, so the interface's return type needs the cast
    return arr as unknown as ArrayBuffer;
  };

  private writeInvocation = (message: InvocationMessage, writer: LiteWriter) => {
    writer.writeByte(MessageType.Invocation);
    this.packHeaders(message.headers as MessageHeaders, writer);

    if (!message.invocationId) {
      writer.writeString(null);
    } else {
      writer.writeString(message.invocationId);
    }

    if (!message.arguments) {
      writer.writeByte(0);
    } else {
      const arg: Uint8Array = message.arguments[0];
      if (arg === null) {
        writer.writeByte(0);
      } else {
        writer.writeByte(1);
        writer.writeByteArray(arg);
      }
    }

    this.writeStreamIds(message.streamIds as string[], writer);
  };

  private writeStreamInvocation = (message: StreamInvocationMessage, writer: LiteWriter) => {
    writer.writeByte(MessageType.StreamInvocation);
    this.packHeaders(message.headers as MessageHeaders, writer);

    writer.writeString(message.invocationId);

    if (!message.arguments) {
      writer.writeLength(0);
    } else {
      writer.writeLength(message.arguments.length);
      for (let i = 0; i < message.arguments.length; i++) {
        const arg = message.arguments[i];
        if (arg === null) {
          writer.writeByte(0);
        } else {
          writer.writeByte(1);
          writer.writeByteArray(arg as Uint8Array);
        }
      }
    }

    this.writeStreamIds(message.streamIds as string[], writer);
  };

  private writeStreamItem = (message: StreamItemMessage, writer: LiteWriter) => {
    writer.writeByte(MessageType.StreamItem);
    this.packHeaders(message.headers as MessageHeaders, writer);
    writer.writeString(message.invocationId);
    if (message.item === null) {
      writer.writeByte(0);
    } else {
      writer.writeByte(1);
      writer.writeByteArray(message.item as Uint8Array);
    }
  };

  private writeCompletion = (message: CompletionMessage, writer: LiteWriter) => {
    const getResultKind = () => {
      if (message.error) {
        return this._errorResult;
      }
      if (message.result !== undefined) {
        return this._nonVoidResult;
      }
      return this._voidResult;
    };

    const resultKind = getResultKind();

    writer.writeByte(MessageType.Completion);
    this.packHeaders(message.headers as MessageHeaders, writer);
    writer.writeString(message.invocationId);
    writer.writeByte(resultKind);
    switch (resultKind) {
      case this._errorResult:
        writer.writeString(message.error as string);
        break;
      case this._nonVoidResult:
        if (message.result === null) {
          writer.writeByte(0);
        } else {
          writer.writeByte(1);
          writer.writeByteArray(message.result as Uint8Array);
        }
        break;
      default:
        console.error(`Unknown result: ${resultKind}`);
        break;
    }
  };

  private writeCancelInvocation(message: CancelInvocationMessage, writer: LiteWriter) {
    writer.writeByte(MessageType.CancelInvocation);
    this.packHeaders(message.headers as MessageHeaders, writer);
    writer.writeString(message.invocationId);
  }

  private writeClose = (message: CloseMessage, writer: LiteWriter) => {
    writer.writeByte(MessageType.Close);
    if (!message.error) {
      writer.writeString(null);
    } else {
      writer.writeString(message.error);
    }

    writer.writeBoolean(message.allowReconnect as boolean);
  };

  private writePing = (writer: LiteWriter) => {
    writer.writeByte(MessageType.Ping);
  };

  private packHeaders(headers: MessageHeaders, writer: LiteWriter) {
    if (headers == null) {
      writer.writeLength(0);
    } else {
      // headers is a plain object, so the count comes from its entries (it has no .size)
      const entries = Object.entries(headers);
      writer.writeLength(entries.length);
      for (const [key, val] of entries) {
        writer.writeString(key);
        writer.writeString(val as any);
      }
    }
  }

  private writeStreamIds(streamIds: (null | undefined | string)[], writer: LiteWriter) {
    if (streamIds === null || streamIds === undefined) {
      writer.writeLength(0);
    } else {
      writer.writeLength(streamIds.length);
      for (let i = 0; i < streamIds.length; i++) {
        writer.writeString(streamIds[i] as string);
      }
    }
  }

  private isArrayBuffer(val: string | ArrayBuffer): string | boolean {
    return val && typeof ArrayBuffer !== "undefined" && (val instanceof ArrayBuffer || ArrayBuffer.isView(val));
  }
}

export class CRCHelper {
  static calculateCRC8 = (input: string): bigint => {
    let crc = 0xff;

    for (let i = 0; i < input.length; i++) {
      const byte = input.charCodeAt(i);

      crc ^= byte;

      for (let bit = 0; bit < 8; bit++) {
        if (crc & 0x80) {
          crc = (crc << 1) ^ 0x07;
        } else {
          crc <<= 1;
        }
      }
    }

    return BigInt(crc & 0xff);
  };

  static calculateCRC16 = (input: string): bigint => {
    let crc = 0xffff;

    for (let i = 0; i < input.length; i++) {
      const byte = input.charCodeAt(i);

      crc ^= (byte << 8) & 0xffff;

      for (let bit = 0; bit < 8; bit++) {
        if (crc & 0x8000) {
          crc = ((crc << 1) ^ 0x1021) & 0xffff;
        } else {
          crc <<= 1;
        }
      }
    }

    return BigInt(crc & 0xffff);
  };

  static calculateCRC32 = (input: string): bigint => {
    let crc = 0xffffffffn;

    for (let i = 0; i < input.length; i++) {
      const byte = input.charCodeAt(i);

      crc ^= BigInt(byte);
      for (let bit = 0; bit < 8; bit++) {
        crc = (crc >> 1n) ^ ((crc & 1n) * 0xedb88320n);
      }
    }

    return BigInt(crc ^ 0xffffffffn);
  };

  // reflected (right-shifting) CRC64, bit-for-bit equivalent to the table-driven C# CRCFunctions.CalculateCRC64
  static calculateCRC64 = (input: string): bigint => {
    const polynomial = 0xc96c5795d7870f42n;
    let crc = 0xffffffffffffffffn;

    for (let i = 0; i < input.length; i++) {
      crc ^= BigInt(input.charCodeAt(i) & 0xff);

      for (let bit = 0; bit < 8; bit++) {
        crc = (crc >> 1n) ^ ((crc & 1n) * polynomial);
      }
    }

    return crc ^ 0xffffffffffffffffn;
  };
}

export class PermissionBitmask {
  constructor() {
    this.Bitmask1 = 0n;
    this.Bitmask2 = 0n;
  }

  Bitmask1: bigint;

  Bitmask2: bigint;
}

/** one entry of the server's API description, retrieved via RpcLink.getApiInfo */
export interface RpcApiFunctionInfo {
  /** the RPC name as invoked, e.g. "FooProcedures.ReturnTask" */
  name: string;
  argType: string | null;
  returnType: string | null;
}

interface PendingChannel {
  resolve: (data: Uint8Array) => void;
  reject: (error: Error) => void;
}

/**
 * Applies LiteLink's wire compression to an already-finalized message, mirroring what
 * LiteWriter.close(headerOffset) does inline: everything after headerOffset is LZ4-compressed
 * and the flag byte at headerOffset - 1 is set. Used where the compression decision happens
 * after a message is built - lone batch flushes and whole batch envelopes.
 */
export function compressMessage(data: Uint8Array, headerOffset: number): Uint8Array {
  if (data.length <= 80) {
    return data;
  }

  // lz4-wasm emits [u32 LE uncompressed size][lz4 block], the layout LiteReader.decompress expects
  const compressed = wasm.compress(data.subarray(headerOffset));

  // only send compressed when it actually shrinks the message
  if (headerOffset + compressed.length >= data.length) {
    return data;
  }

  const res = new Uint8Array(headerOffset + compressed.length);
  res.set(data.subarray(0, headerOffset), 0);
  res[headerOffset - 1] = 1; // the byte before the payload is the isCompressed flag
  res.set(compressed, headerOffset);
  return res;
}

/** how the client packs concurrent requests into transport messages */
export enum RpcBatchMode {
  /** every call goes out immediately as its own transport message (the default) */
  Disabled = 0,

  /**
   * the first call goes out immediately, calls made while a send is in flight are packed
   * into one follow-up batch; adds no latency on an idle connection, packs under load
   */
  Coalesce = 1,

  /**
   * calls are held for RpcLink.batchWindow milliseconds and flushed as one batch;
   * packs most aggressively but delays every call by up to the window
   */
  Window = 2
}

interface PendingBatchItem {
  data: Uint8Array;
  /** where this request's compressible payload starts (3 plain, 8 multichannel), used when a lone flush compresses individually */
  compressOffset: number;
  resolve: (data: Uint8Array) => void;
  reject: (error: Error) => void;
}

/** thrown when a request (or the wait for a free multichannel slot) exceeds RpcLink.responseTimeout */
export class RpcTimeoutError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "RpcTimeoutError";
  }
}

export class RpcLink {
  private static connection: HubConnection;

  /** function table from the handshake (hash -> opcode/arg/ret), exposed for tooling (e.g. API browsers) */
  static functionMaps: Map<bigint, FunctionMap>;

  private static permissionMaps: Map<bigint, PermissionBitmask>;

  // eslint-disable-next-line no-spaced-func
  private static registeredFunctions = new Map<bigint, (reader: LiteReader) => void>();

  /** handlers for server-initiated invokes that expect a response (push type 2) */
  // eslint-disable-next-line no-spaced-func
  private static registeredResponders = new Map<bigint, (arg: any) => any>();

  /** hashes an RPC name with the width negotiated in the handshake, exposed for tooling */
  static functionHasher: (name: string) => bigint;

  private static permissionHasher: (any: string) => bigint;

  private static readTypeIndex: (reader: LiteReader) => number;

  private static connectionLost: () => void;

  static finishedHandshake: boolean = false;

  /** occupied multichannel slots; freed when the response arrives or the request fails */
  private static channelRequests: (PendingChannel | null)[] = new Array(256).fill(null);

  private static lastChannelIndex: number = 0;

  // every public option below distinguishes configuration from session state: a set while
  // disconnected (including before the first connect) updates the baseline too, a set while
  // connected is session-only. connect() restores the baseline, so whatever a session earned
  // (e.g. multichannel unlocked by a login) dies with the connection instead of silently
  // carrying into an unauthenticated reconnect
  private static sessionActive: boolean = false;

  private static get isConfiguring(): boolean {
    return !RpcLink.sessionActive || RpcLink.connection?.state !== HubConnectionState.Connected;
  }

  private static baseline = {
    responseTimeout: 60000,
    batchMode: RpcBatchMode.Disabled,
    batchWindow: 50,
    maxBatchSize: 64,
    multiChannelByDefault: false
  };

  private static restoreBaseline(): void {
    RpcLink._responseTimeout = RpcLink.baseline.responseTimeout;
    RpcLink._batchMode = RpcLink.baseline.batchMode;
    RpcLink._batchWindow = RpcLink.baseline.batchWindow;
    RpcLink._maxBatchSize = RpcLink.baseline.maxBatchSize;
    RpcLink._multiChannelByDefault = RpcLink.baseline.multiChannelByDefault;
  }

  private static _responseTimeout: number = 60000;

  /**
   * how long any request (plain, batched or multichannel, including the wait for a free
   * multichannel slot and the handshake) may take before failing with an RpcTimeoutError, in ms;
   * 0 or negative disables the limit
   */
  static get responseTimeout(): number {
    return RpcLink._responseTimeout;
  }

  static set responseTimeout(value: number) {
    RpcLink._responseTimeout = value;
    if (RpcLink.isConfiguring) {
      RpcLink.baseline.responseTimeout = value;
    }
  }

  private static _batchMode: RpcBatchMode = RpcBatchMode.Disabled;

  /** how concurrent requests are packed into transport messages, see RpcBatchMode */
  static get batchMode(): RpcBatchMode {
    return RpcLink._batchMode;
  }

  static set batchMode(value: RpcBatchMode) {
    RpcLink._batchMode = value;
    if (RpcLink.isConfiguring) {
      RpcLink.baseline.batchMode = value;
    }
  }

  private static _batchWindow: number = 50;

  /** how long Window mode holds requests before flushing them as one batch, in ms */
  static get batchWindow(): number {
    return RpcLink._batchWindow;
  }

  static set batchWindow(value: number) {
    RpcLink._batchWindow = value;
    if (RpcLink.isConfiguring) {
      RpcLink.baseline.batchWindow = value;
    }
  }

  private static _maxBatchSize: number = 64;

  /**
   * most requests packed into a single batch; a full batch flushes immediately.
   * Must not exceed the server's RpcServer.MaxBatchSize (64 by default) or batches will be rejected.
   */
  static get maxBatchSize(): number {
    return RpcLink._maxBatchSize;
  }

  static set maxBatchSize(value: number) {
    RpcLink._maxBatchSize = value;
    if (RpcLink.isConfiguring) {
      RpcLink.baseline.maxBatchSize = value;
    }
  }

  private static _multiChannelByDefault: boolean = false;

  /**
   * when true every send/Remote call goes out as a multichannel request, so this connection's
   * calls run in parallel on the server (up to 256 in flight) instead of sequentially. The server
   * must allow it (RpcLink.AllowMultiChannel, denied calls fail with error code 2) - the intended
   * flow is to log in, have the server grant the link, then enable this. Like every option, a
   * mid-connection change reverts when connect() runs again after a disconnect, matching the
   * server-side grant dying with the connection.
   */
  static get multiChannelByDefault(): boolean {
    return RpcLink._multiChannelByDefault;
  }

  static set multiChannelByDefault(value: boolean) {
    RpcLink._multiChannelByDefault = value;
    if (RpcLink.isConfiguring) {
      RpcLink.baseline.multiChannelByDefault = value;
    }
  }

  /**
   * fires the moment the connection drops: fails every pending multichannel call and every
   * request still queued for batching immediately, instead of letting their callers burn the
   * full responseTimeout waiting for responses that can never arrive. Registered on the
   * connection's onclose in connect(); also marks the session inactive so option sets before
   * the next connect() count as configuration.
   */
  private static handleConnectionLost = (): void => {
    RpcLink.sessionActive = false;

    for (let i = 0; i < 256; i++) {
      const pending = RpcLink.channelRequests[i];
      RpcLink.channelRequests[i] = null;
      pending?.reject(new Error("Connection was lost while the request was in flight"));
    }
    RpcLink.lastChannelIndex = 0;

    const staleBatch = RpcLink.batchQueue;
    RpcLink.batchQueue = [];
    RpcLink.batchFlushScheduled = false;
    RpcLink.batchSendInFlight = false;
    for (const item of staleBatch) {
      item.reject(new Error("Connection was lost while the request was queued for batching"));
    }
  };

  /** rejects with an RpcTimeoutError when the promise takes longer than responseTimeout */
  private static withTimeout<T>(promise: Promise<T>, what: string): Promise<T> {
    const ms = RpcLink._responseTimeout;
    if (!ms || ms <= 0) {
      return promise;
    }
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => reject(new RpcTimeoutError(`${what} timed out after ${ms}ms`)), ms);
      promise.then(
        (value) => {
          clearTimeout(timer);
          resolve(value);
        },
        (error) => {
          clearTimeout(timer);
          reject(error);
        }
      );
    });
  }

  private static batchQueue: PendingBatchItem[] = [];

  /** Window mode: a delayed flush is already pending */
  private static batchFlushScheduled: boolean = false;

  /** Coalesce mode: the send pump is running, new calls queue behind it */
  private static batchSendInFlight: boolean = false;

  static Remote: () => void;

  static getConnectionState = () : HubConnectionState => {
    return this.connection?.state;
  };

  private static createProxyHandler = (
    target: { handler: (arg: string, args: any[]) => Promise<void> },
    path: (string | symbol)[] = []
  ): (() => void) => {
    return new Proxy(() => {}, {
      get(proxyTarget, property) {
        return RpcLink.createProxyHandler(target, [...path, property]);
      },
      apply(proxyTarget, thisArg, argumentsList) {
        const arg = [...path].join(".");
        return target.handler(arg, argumentsList);
      }
    });
  };

  /**
   * Registers a responder for server-initiated `link.Invoke<TResult>(name, model)` calls.
   * The handler may return a value or a promise; the resolved value is serialized back to the
   * server as the call's result. When the server invokes without an argument, `arg` is null.
   */
  static registerResponder = (name: string, handler: (arg: any) => any): void => {
    const hash = CRCHelper.calculateCRC32(name);

    if (RpcLink.registeredResponders.has(hash) || RpcLink.registeredFunctions.has(hash)) {
      throw new Error(
        // eslint-disable-next-line max-len
        `Hash for function ${name} already present, make sure you are not registering the function twice, otherwise try changing the invokation name`
      );
    }

    RpcLink.registeredResponders.set(hash, handler);
  };

  private static runResponder = async (
    requestId: number,
    resultTypeIdx: number,
    handler: (arg: any) => any,
    arg: any
  ): Promise<void> => {
    let status = 0;
    let result: any = null;
    try {
      result = await handler(arg);
    } catch (e) {
      status = 255;
      // eslint-disable-next-line no-console
      console.error("LiteLink responder failed:", e);
    }
    await RpcLink.sendInvokeResponse(requestId, status, resultTypeIdx, result);
  };

  private static sendInvokeResponse = async (
    requestId: number,
    status: number,
    resultTypeIdx: number,
    result: any
  ): Promise<void> => {
    try {
      const writer = new LiteWriter();
      writer.writeUInt16(0); // system opcode
      writer.writeByte(0); // not compressed
      writer.writeByte(3); // type 3: invoke response
      writer.writeByte(requestId);
      writer.writeByte(status);
      if (status === 0) {
        LiteSerializer.serialize(writer, resultTypeIdx, result);
      }
      await RpcLink.withTimeout(RpcLink.connection.invoke<Uint8Array>("Data", writer.close()), "Invoke response");
    } catch (e) {
      // the connection likely died, the server's invoke fails on disconnect
      // eslint-disable-next-line no-console
      console.error("LiteLink failed to send invoke response:", e);
    }
  };

  static registerFunction = (name: string, func: (res: any) => void): void => {
    const hash = CRCHelper.calculateCRC32(name);

    if (RpcLink.registeredFunctions.has(hash) || RpcLink.registeredResponders.has(hash)) {
      throw new Error(
        // eslint-disable-next-line max-len
        `Hash for function ${name} already present, make sure you are not registering the function twice, otherwise try changing the invokation name`
      );
    }

    RpcLink.registeredFunctions.set(hash, (reader) => {
      const typeIdx = this.readTypeIndex(reader);

      const res = LiteSerializer.deserialize(reader, typeIdx);
      func(res);
    });
  };

  static enablePermission = (userPermissions: PermissionBitmask, permission: string): void => {
    const hash = this.permissionHasher(permission);
    const permissionMap = RpcLink.permissionMaps.get(hash);

    if (!permissionMap) {
      throw new Error(`Unknown permission ${permission}`);
    }

    userPermissions.Bitmask1 |= permissionMap.Bitmask1;
    userPermissions.Bitmask2 |= permissionMap.Bitmask2;
  };

  static hasPermission(userPermissions: PermissionBitmask, permission: string): boolean {
    const hash = this.permissionHasher(permission);
    const permissionMap = RpcLink.permissionMaps.get(hash);

    if (!permissionMap) {
      throw new Error(`Unknown permission ${permission}`);
    }

    return (
      ((permissionMap.Bitmask1 & userPermissions.Bitmask1) | (permissionMap.Bitmask2 & userPermissions.Bitmask2)) !== 0n
    );
  }

  static setOnConnectionLost = (callback: (error?: Error | undefined) => void): void => {
    RpcLink.connection.onclose(callback);
  };

  static disconnect = () => {
    if (RpcLink.connection) {
      RpcLink.connection.stop();
      RpcLink.connection = null;
    }
  };

  static connect = async (hostname: string): Promise<number> => {
    if (!this.Remote) {
      this.Remote = RpcLink.createProxyHandler({
        async handler(property: string, arg: TBasicTypes[]) {
          return RpcLink.send(BigInt(RpcLink.functionHasher(property)), arg[0]);
        }
      });
    }

    if (this.connection !== null && this.connection !== undefined) {
      if (this.connection.state === HubConnectionState.Connected) {
        console.warn("Connection already established!");
        return 1;
      }

      if (this.connection.state !== HubConnectionState.Disconnected) {
        console.warn("Already connecting");
        return -1;
      }
    }

    // a (re)connect reverts every option changed during the previous session: whatever authorized
    // those changes (e.g. a login that unlocked multiChannelByDefault) did not survive the
    // connection, so the session's settings must not either
    RpcLink.restoreBaseline();
    RpcLink.sessionActive = false;

    RpcLink.finishedHandshake = false;
    RpcLink.connection = new HubConnectionBuilder()
      .withUrl(hostname)
      .withHubProtocol(new LiteLinkHubProtocol())
      .build();

    RpcLink.connection.onclose(() => RpcLink.handleConnectionLost());

    RpcLink.connection.on("Data", (x: Uint8Array) => {
      const reader = new LiteReader(x);
      const isCompressed = reader.readBoolean();
      if (isCompressed) {
        reader.decompress();
      }

      const hash = reader.readUInt32();

      if (hash === 0) {
        // system message
        const type = reader.readByte();
        if (type === 1) {
          // multichannel response
          const channel = reader.readByte();
          const pending = RpcLink.channelRequests[channel];
          if (pending) {
            RpcLink.channelRequests[channel] = null;
            pending.resolve(reader.readRemaining());
          } else {
            // eslint-disable-next-line no-console
            console.warn(
              `Received a multichannel response on channel ${channel} with no pending request, the request may have timed out`
            );
          }
        } else if (type === 2) {
          // server-initiated invoke expecting a response
          const requestId = reader.readByte();
          const resultTypeIdx = RpcLink.readTypeIndex(reader);
          const funcHash = reader.readUInt32();
          const hasArg = reader.readByte();

          const responder = RpcLink.registeredResponders.get(BigInt(funcHash));
          if (!responder) {
            // fail the server's call fast instead of letting it hit its timeout
            void RpcLink.sendInvokeResponse(requestId, 254, 0, null);
            return;
          }

          let arg: any = null;
          if (hasArg === 1) {
            const argTypeIdx = RpcLink.readTypeIndex(reader);
            arg = LiteSerializer.deserialize(reader, argTypeIdx);
          }

          void RpcLink.runResponder(requestId, resultTypeIdx, responder, arg);
        }
        return;
      }

      if (!this.registeredFunctions.has(BigInt(hash))) {
        throw new Error(`unknown function call with hash: ${hash}`);
      }

      this.registeredFunctions.get(BigInt(hash))?.(reader);
    });

    try {
      await RpcLink.connection.start();
    } catch {
      return 0;
    }

    if (RpcLink.connection.state !== HubConnectionState.Connected) {
      return 0;
    }

    // safety net for anything onclose didn't already fail: requests from the previous connection
    // can't be answered on this one
    RpcLink.handleConnectionLost();

    // handshake
    // 16 bit - opcode (0), 8 bit - isCompressed (0), 8 bit - system function type (0 = handshake)

    const writer = new LiteWriter();
    writer.writeUInt16(0);
    writer.writeByte(0);
    writer.writeByte(0);

    const ret = await RpcLink.withTimeout(
      RpcLink.connection.invoke<Uint8Array>("Data", writer.close()),
      "Handshake"
    );
    const reader = new LiteReader(ret);

    reader.readByte();
    const isCompressed = reader.readByte();

    if (isCompressed) {
      reader.decompress();
    }

    this.parseHandshake(reader);

    RpcLink.finishedHandshake = true;
    RpcLink.sessionActive = true;
    return 1;
  };

  /**
   * every procedure request (plain and multichannel) funnels through here; system traffic with
   * latency requirements (handshake, invoke responses) calls connection.invoke directly instead.
   * When batching is off the data is already compressed by its builder; when batching is on it
   * arrives raw and compression is applied at flush time (per request for a lone flush, once
   * over the whole envelope for a pack).
   */
  private static sendTransport = (data: Uint8Array, compressOffset: number): Promise<Uint8Array> => {
    switch (RpcLink.batchMode) {
      case RpcBatchMode.Coalesce:
        return RpcLink.enqueueCoalesce(data, compressOffset);
      case RpcBatchMode.Window:
        return RpcLink.enqueueWindow(data, compressOffset);
      default:
        return RpcLink.connection.invoke<Uint8Array>("Data", data);
    }
  };

  private static enqueueWindow = (data: Uint8Array, compressOffset: number): Promise<Uint8Array> => {
    return new Promise<Uint8Array>((resolve, reject) => {
      RpcLink.batchQueue.push({ data, compressOffset, resolve, reject });

      if (RpcLink.batchQueue.length >= RpcLink.maxBatchSize) {
        // a full batch flushes immediately, a pending timer flush just finds an empty queue later
        const batch = RpcLink.batchQueue;
        RpcLink.batchQueue = [];
        void RpcLink.sendBatch(batch);
      } else if (!RpcLink.batchFlushScheduled) {
        RpcLink.batchFlushScheduled = true;
        setTimeout(() => {
          RpcLink.batchFlushScheduled = false;
          const batch = RpcLink.batchQueue;
          RpcLink.batchQueue = [];
          if (batch.length > 0) {
            void RpcLink.sendBatch(batch);
          }
        }, RpcLink.batchWindow);
      }
    });
  };

  private static enqueueCoalesce = (data: Uint8Array, compressOffset: number): Promise<Uint8Array> => {
    return new Promise<Uint8Array>((resolve, reject) => {
      const item: PendingBatchItem = { data, compressOffset, resolve, reject };

      if (RpcLink.batchSendInFlight) {
        RpcLink.batchQueue.push(item);
        return;
      }

      // idle connection: this call starts the pump and goes out immediately with no added latency
      RpcLink.batchSendInFlight = true;
      void RpcLink.runCoalescePump([item]);
    });
  };

  private static runCoalescePump = async (batch: PendingBatchItem[]): Promise<void> => {
    let current = batch;
    for (;;) {
      await RpcLink.sendBatch(current);

      if (RpcLink.batchQueue.length === 0) {
        RpcLink.batchSendInFlight = false;
        return;
      }

      current = RpcLink.batchQueue.splice(0, RpcLink.maxBatchSize);
    }
  };

  private static sendBatch = async (batch: PendingBatchItem[]): Promise<void> => {
    try {
      if (batch.length === 1) {
        // a lone request goes out unwrapped, batching must not add overhead when there's
        // nothing to pack; it was built raw, so compress it individually here
        const single = compressMessage(batch[0].data, batch[0].compressOffset);
        batch[0].resolve(
          await RpcLink.withTimeout(RpcLink.connection.invoke<Uint8Array>("Data", single), "Request")
        );
        return;
      }

      const writer = new LiteWriter();
      writer.writeUInt16(0); // system opcode
      writer.writeByte(0); // compressed, set by compressMessage when the envelope compresses
      writer.writeByte(4); // type 4: batch
      writer.writeUInt16(batch.length);
      for (const item of batch) {
        writer.writeByteArray(item.data);
      }

      // raw requests packed together, then one compression pass over the whole envelope;
      // a timeout here fails every item, so the coalesce pump can't hang on a dead transport
      const response = await RpcLink.withTimeout(
        RpcLink.connection.invoke<Uint8Array>("Data", compressMessage(writer.close(), 3)),
        "Batch request"
      );
      RpcLink.completeBatch(batch, response);
    } catch (error) {
      const err = error instanceof Error ? error : new Error(String(error));
      for (const item of batch) {
        item.reject(err);
      }
    }
  };

  private static completeBatch = (batch: PendingBatchItem[], response: Uint8Array): void => {
    const reader = new LiteReader(response);
    const res = reader.readByte();
    const isCompressed = reader.readByte();

    if (res !== 0) {
      const hint = res === 1
        ? " (BadRequest - is the batch larger than the server's MaxBatchSize, or the server too old to support batching?)"
        : "";
      const err = new Error(`Batch request failed! Error code: ${res}${hint}`);
      for (const item of batch) {
        item.reject(err);
      }
      return;
    }

    if (isCompressed) {
      reader.decompress();
    }

    const count = reader.readUInt16();
    if (count !== batch.length) {
      const err = new Error(`Batch response item count mismatch: sent ${batch.length}, received ${count}`);
      for (const item of batch) {
        item.reject(err);
      }
      return;
    }

    for (const item of batch) {
      item.resolve(reader.readByteArray() as Uint8Array);
    }
  };

  // eslint-disable-next-line consistent-return
  static send = async (funcHash: bigint, model: TBasicTypes): Promise<void> => {
    if (!this.connection || this.connection.state === HubConnectionState.Disconnected) {
      if (RpcLink.connectionLost) { 
        RpcLink.connectionLost();
      }

      throw new Error("Connection lost");
    }

    
    const funcMap = RpcLink.functionMaps.get(funcHash);

    if (!funcMap) {
      throw new Error("Unknown function hash");
    }

    // multichannel-by-default changes only the envelope: callers keep using send/Remote
    if (RpcLink._multiChannelByDefault) {
      // eslint-disable-next-line @typescript-eslint/no-unsafe-return
      return RpcLink.sendMultiChannel(funcHash, model);
    }

    const writer = new LiteWriter();
    writer.writeUInt16(funcMap.opcode);
    writer.writeByte(0); // compression placeholder

    if (funcMap.argType !== undefined) {
      if ((model === null || model === undefined) && !LiteSerializer.isTypeNullable(funcMap.argType)) {
        throw new Error("Missing argument");
      }

      LiteSerializer.serialize(writer, funcMap.argType, model);
    }

    // when batching, the request stays uncompressed here so the batcher can compress the
    // whole envelope in one pass at flush time (lone flushes compress individually)
    const data = writer.close(RpcLink.batchMode === RpcBatchMode.Disabled ? 3 : -1);
    const ret = await RpcLink.withTimeout(RpcLink.sendTransport(data, 3), "Request");
    const reader = new LiteReader(ret);

    const res = reader.readByte();
    const isCompressed = reader.readByte();

    if (res !== 0) {
      throw new Error(`Error calling function! Error code: ${res}`);
    }

    if (funcMap.retType !== undefined) {
      if (isCompressed) {
        reader.decompress();
      }

      // eslint-disable-next-line @typescript-eslint/no-unsafe-return
      return LiteSerializer.deserialize(reader, funcMap.retType);
    }
  };

  /**
   * Requests the server's API description (function names and signatures). Only available when the
   * server was built with ShowApi = true on its [RpcServer] attribute; pass the password if the
   * server requires one. Throws on error responses (e.g. wrong password).
   * Works right after connect, no handshake required.
   */
  static getApiInfo = async (password: string | null = null): Promise<RpcApiFunctionInfo[]> => {
    const writer = new LiteWriter();
    writer.writeUInt16(0); // system opcode
    writer.writeByte(0); // not compressed
    writer.writeByte(2); // type 2 = api info
    writer.writeString(password);

    const ret = await RpcLink.withTimeout(
      RpcLink.connection.invoke<Uint8Array>("Data", writer.close()),
      "API info request"
    );
    const reader = new LiteReader(ret);

    const res = reader.readByte();
    if (res !== 0) {
      throw new Error(`Error retrieving API info! Error code: ${res}`);
    }

    if (reader.readBoolean()) {
      reader.decompress();
    }

    const count = reader.readUInt16();
    const functions: RpcApiFunctionInfo[] = [];

    for (let i = 0; i < count; i++) {
      functions.push({
        name: reader.readString() as string,
        argType: reader.readString(),
        returnType: reader.readString()
      });
    }

    return functions;
  };

  /**
   * Calls an RPC function over a multichannel request: the connection is not blocked while waiting
   * for the response, so multiple calls can be in flight at once (up to 256).
   */
  static sendMultiChannelMessage = async (name: string, model: TBasicTypes): Promise<any> => {
    // eslint-disable-next-line @typescript-eslint/no-unsafe-return
    return RpcLink.sendMultiChannel(RpcLink.functionHasher(name), model);
  };

  static sendMultiChannel = async (funcHash: bigint, model: TBasicTypes): Promise<any> => {
    if (!this.connection || this.connection.state === HubConnectionState.Disconnected) {
      if (RpcLink.connectionLost) {
        RpcLink.connectionLost();
      }

      throw new Error("Connection lost");
    }

    const funcMap = RpcLink.functionMaps.get(funcHash);

    if (!funcMap) {
      throw new Error("Unknown function hash");
    }

    let pending: PendingChannel;
    const responsePromise = new Promise<Uint8Array>((resolve, reject) => {
      pending = { resolve, reject };
    });

    const channel = await RpcLink.scheduleChannel(pending!);

    // frees the channel unless the response already claimed it (and possibly a new request took it)
    const releaseChannel = () => {
      if (RpcLink.channelRequests[channel] === pending) {
        RpcLink.channelRequests[channel] = null;
      }
    };

    try {
      const writer = new LiteWriter();
      writer.writeUInt16(0); // system opcode
      writer.writeByte(0); // not compressed
      writer.writeByte(1); // type 1 = multichannel request
      writer.writeByte(channel);
      // inner message
      writer.writeUInt16(funcMap.opcode);
      writer.writeByte(0); // compression placeholder

      if (funcMap.argType !== undefined) {
        if ((model === null || model === undefined) && !LiteSerializer.isTypeNullable(funcMap.argType)) {
          throw new Error("Missing argument");
        }

        LiteSerializer.serialize(writer, funcMap.argType, model);
      }

      // headerOffset 8: multichannel header (5 bytes) + inner opcode (2) + inner isCompressed flag (1);
      // uncompressed when batching, the batcher compresses at flush time
      const payload = writer.close(RpcLink.batchMode === RpcBatchMode.Disabled ? 8 : -1);
      const ack = await RpcLink.withTimeout(RpcLink.sendTransport(payload, 8), "Multichannel request");
      if (ack[0] !== 0) {
        const hint = ack[0] === 2 ? " (Unauthorized - the server has not granted this connection AllowMultiChannel)" : "";
        throw new Error(`Error calling function! Error code: ${ack[0]}${hint}`);
      }

      // 0 or negative disables the timeout, matching withTimeout
      const timeout = RpcLink.responseTimeout > 0
        ? setTimeout(() => {
          releaseChannel();
          pending.reject(new RpcTimeoutError(`Multichannel request timed out after ${RpcLink.responseTimeout}ms`));
        }, RpcLink.responseTimeout)
        : undefined;

      let data: Uint8Array;
      try {
        data = await responsePromise;
      } finally {
        if (timeout !== undefined) {
          clearTimeout(timeout);
        }
      }

      const reader = new LiteReader(data);
      const res = reader.readByte();
      const isCompressed = reader.readByte();

      if (res !== 0) {
        throw new Error(`Error calling function! Error code: ${res}`);
      }

      if (funcMap.retType !== undefined) {
        if (isCompressed) {
          reader.decompress();
        }

        // eslint-disable-next-line @typescript-eslint/no-unsafe-return
        return LiteSerializer.deserialize(reader, funcMap.retType);
      }
    } catch (error) {
      // release the channel so a failed request doesn't occupy it forever
      releaseChannel();
      throw error;
    }
  };

  /** claims a free channel slot, scanning from the last used index and wrapping around */
  private static scheduleChannel = async (pending: PendingChannel): Promise<number> => {
    const timeout = RpcLink._responseTimeout;
    const deadline = timeout > 0 ? Date.now() + timeout : Number.POSITIVE_INFINITY;
    for (;;) {
      let idx = RpcLink.lastChannelIndex;
      for (let i = 0; i < 256; i++, idx = (idx + 1) & 0xff) {
        if (RpcLink.channelRequests[idx] === null) {
          RpcLink.channelRequests[idx] = pending;
          RpcLink.lastChannelIndex = idx;
          return idx;
        }
      }
      // no free channel right now, wait for in-flight requests to complete
      const remaining = deadline - Date.now();
      if (remaining <= 0) {
        throw new RpcTimeoutError(
          `No multichannel slot became free within responseTimeout (${timeout}ms), all 256 are occupied`
        );
      }
      // eslint-disable-next-line no-await-in-loop
      await new Promise((resolve) => {
        setTimeout(resolve, Math.min(remaining, 1000));
      });
    }
  };

  static parseHandshake(reader: LiteReader): void {
    const propertyMap = [];
    const typeMap = [];

    const propertyBytes = reader.readByte();

    let readPropertyIndex: () => number;

    if (propertyBytes === 1) {
      readPropertyIndex = () => reader.readByte();
    } else if (propertyBytes === 2) {
      readPropertyIndex = () => reader.readUInt16();
    } else if (propertyBytes === 4) {
      readPropertyIndex = () => reader.readUInt32();
    } else {
      throw new Error("Incorrectly defined propety index size in handshake.");
    }

    const numProperties = reader.readUInt32();

    for (let i = 0; i < numProperties; i++) {
      propertyMap.push(reader.readString());
    }

    const typeBytes = reader.readByte();

    if (typeBytes === 1) {
      this.readTypeIndex = (reader: LiteReader) => reader.readByte();
    } else if (typeBytes === 2) {
      this.readTypeIndex = (reader: LiteReader) => reader.readUInt16();
    } else if (typeBytes === 4) {
      this.readTypeIndex = (reader: LiteReader) => reader.readUInt32();
    } else {
      throw new Error("Incorrectly defined type index size in handshake.");
    }

    const numTypes = reader.readUInt32();

    for (let i = 0; i < numTypes; i++) {
      const propCount = reader.readUInt16();

      if (propCount === 0xffff) {
        const typeSpecial = reader.readByte();
        if (typeSpecial === 1) {
          // is dictionary
          const keyType = this.readTypeIndex(reader);
          const valueType = this.readTypeIndex(reader);

          const type = new TypeMap();
          type.specialType = SpecialType.Dictionary;
          type.basicType = BasicTypes.Object;
          type.types = [keyType, valueType];

          typeMap.push(type);
        } else if (typeSpecial === 2) {
          // is collection
          const type = new TypeMap();
          type.specialType = SpecialType.Collection;
          type.basicType = BasicTypes.Object;
          type.types = [this.readTypeIndex(reader)];
          typeMap.push(type);
        } else if (typeSpecial === 3) {
          // is tuple
          const type = new TypeMap();
          type.specialType = SpecialType.Tuple;
          type.basicType = BasicTypes.Object;
          type.types = [];
          const numTupleTypes = reader.readByte();
          for (let j = 0; j < numTupleTypes; j++) {
            type.types.push(this.readTypeIndex(reader));
          }

          typeMap.push(type);
        } else if (typeSpecial === 4) {
          // is nullable
          const type = new TypeMap();
          type.specialType = SpecialType.Nullable;
          type.basicType = BasicTypes.Object;
          type.types = [this.readTypeIndex(reader)];
          typeMap.push(type);
        } else {
          const type = new TypeMap();
          type.specialType = SpecialType.Normal;
          type.basicType = RpcLink.getBasicType(reader.readString() as string);
          typeMap.push(type);
        }
      } else {
        const type = new TypeMap();
        type.specialType = SpecialType.Normal;
        type.basicType = BasicTypes.Object;
        type.properties = [];

        for (let j = 0; j < propCount; j++) {
          const prop = new PropertyMap();
          prop.propertyIdx = readPropertyIndex();
          prop.typeIdx = this.readTypeIndex(reader);
          prop.storageType = reader.readByte();
          type.properties.push(prop);
        }

        typeMap.push(type);
      }
    }

    const funcBytes = reader.readByte();

    let readFuncHash: () => bigint;

    if (funcBytes === 1) {
      readFuncHash = () => BigInt(reader.readByte());
      RpcLink.functionHasher = CRCHelper.calculateCRC8;
    } else if (funcBytes === 2) {
      readFuncHash = () => BigInt(reader.readUInt16());
      RpcLink.functionHasher = CRCHelper.calculateCRC16;
    } else if (funcBytes === 4) {
      readFuncHash = () => BigInt(reader.readUInt32());
      RpcLink.functionHasher = CRCHelper.calculateCRC32;
    } else if (funcBytes === 8) {
      readFuncHash = () => BigInt(reader.readUInt64());
      RpcLink.functionHasher = CRCHelper.calculateCRC64;
    } else {
      throw new Error("Incorrectly defined func hash size in handshake.");
    }

    const numFuncs = reader.readUInt16();

    RpcLink.functionMaps = new Map<bigint, FunctionMap>();

    for (let i = 0; i < numFuncs; i++) {
      const funcMap = new FunctionMap();
      funcMap.opcode = reader.readUInt16();
      const funcHash = readFuncHash();
      if (reader.readByte() === 1) {
        funcMap.argType = this.readTypeIndex(reader);
      }
      if (reader.readByte() === 1) {
        funcMap.retType = this.readTypeIndex(reader);
      }
      RpcLink.functionMaps.set(funcHash, funcMap);
    }

    const permissionBytes = reader.readByte();

    let readPermissionHash: () => bigint;

    if (permissionBytes === 1) {
      readPermissionHash = () => BigInt(reader.readByte());
      RpcLink.permissionHasher = CRCHelper.calculateCRC8;
    } else if (permissionBytes === 2) {
      readPermissionHash = () => BigInt(reader.readUInt16());
      RpcLink.permissionHasher = CRCHelper.calculateCRC16;
    } else if (permissionBytes === 4) {
      readPermissionHash = () => BigInt(reader.readUInt32());
      RpcLink.permissionHasher = CRCHelper.calculateCRC32;
    } else if (permissionBytes === 8) {
      readPermissionHash = () => BigInt(reader.readUInt64());
      RpcLink.permissionHasher = CRCHelper.calculateCRC64;
    } else {
      throw new Error("Incorrectly defined permission hash size in handshake.");
    }

    const numPermissions = reader.readUInt16();

    RpcLink.permissionMaps = new Map<bigint, PermissionBitmask>();

    for (let i = 0; i < numPermissions; i++) {
      const permMap = new PermissionBitmask();
      const permHash = readPermissionHash();

      if (i < 64) {
        permMap.Bitmask1 = RpcLink.bitmasks[i];
        permMap.Bitmask2 = 0n;
      } else {
        permMap.Bitmask1 = 0n;
        permMap.Bitmask2 = RpcLink.bitmasks[i - 64];
      }

      RpcLink.permissionMaps.set(permHash, permMap);
    }

    LiteSerializer.setData(propertyMap as string[], typeMap);
  }

  private static getBasicType(type: string): BasicTypes {
    switch (type) {
      case "bool":
        return BasicTypes.Bool;
      case "sbyte":
        return BasicTypes.SByte;
      case "byte":
        return BasicTypes.Byte;
      case "short":
        return BasicTypes.Int16;
      case "ushort":
        return BasicTypes.UInt16;
      case "int":
        return BasicTypes.Int32;
      case "uint":
        return BasicTypes.UInt32;
      case "long":
        return BasicTypes.Int64;
      case "ulong":
        return BasicTypes.UInt64;
      case "float":
        return BasicTypes.Single;
      case "double":
        return BasicTypes.Double;
      case "decimal":
        return BasicTypes.Decimal;
      case "string":
        return BasicTypes.String;
      case "date":
        return BasicTypes.DateTime;
      case "guid":
        return BasicTypes.Guid;
      default:
        return BasicTypes.Object;
    }
  }

  private static bitmasks = [
    0x0000000000000001n,
    0x0000000000000002n,
    0x0000000000000004n,
    0x0000000000000008n,
    0x0000000000000010n,
    0x0000000000000020n,
    0x0000000000000040n,
    0x0000000000000080n,
    0x0000000000000100n,
    0x0000000000000200n,
    0x0000000000000400n,
    0x0000000000000800n,
    0x0000000000001000n,
    0x0000000000002000n,
    0x0000000000004000n,
    0x0000000000008000n,
    0x0000000000010000n,
    0x0000000000020000n,
    0x0000000000040000n,
    0x0000000000080000n,
    0x0000000000100000n,
    0x0000000000200000n,
    0x0000000000400000n,
    0x0000000000800000n,
    0x0000000001000000n,
    0x0000000002000000n,
    0x0000000004000000n,
    0x0000000008000000n,
    0x0000000010000000n,
    0x0000000020000000n,
    0x0000000040000000n,
    0x0000000080000000n,
    0x0000000100000000n,
    0x0000000200000000n,
    0x0000000400000000n,
    0x0000000800000000n,
    0x0000001000000000n,
    0x0000002000000000n,
    0x0000004000000000n,
    0x0000008000000000n,
    0x0000010000000000n,
    0x0000020000000000n,
    0x0000040000000000n,
    0x0000080000000000n,
    0x0000100000000000n,
    0x0000200000000000n,
    0x0000400000000000n,
    0x0000800000000000n,
    0x0001000000000000n,
    0x0002000000000000n,
    0x0004000000000000n,
    0x0008000000000000n,
    0x0010000000000000n,
    0x0020000000000000n,
    0x0040000000000000n,
    0x0080000000000000n,
    0x0100000000000000n,
    0x0200000000000000n,
    0x0400000000000000n,
    0x0800000000000000n,
    0x1000000000000000n,
    0x2000000000000000n,
    0x4000000000000000n,
    0x8000000000000000n
  ];
}

/**
 * @deprecated litelink 0.4.0 renamed the client class to {@link RpcLink} to match the C#-side
 * naming; this alias keeps existing imports compiling and will be removed in a future release.
 */
export const RPCLink = RpcLink;
