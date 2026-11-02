import { afterEach, describe, expect, it } from "vitest";
import { compressMessage, LiteReader, LiteWriter, RpcLink, RpcBatchMode } from "../src/index";

// reach through TS-level privacy to drive the batcher without a real SignalR connection
const link = RpcLink as any;

/** a pseudo procedure request: non-zero opcode, a real isCompressed flag byte, then the payload */
const req = (marker: number) => new Uint8Array([1, 0, 0, marker]);

/** the fake hub echoes a request back as [0 result][0 isCompressed][request bytes] */
function echo(request: Uint8Array): Uint8Array {
  const response = new Uint8Array(request.length + 2);
  response.set(request, 2);
  return response;
}

/**
 * implements the server side of the batch protocol: unpack (decompressing the envelope
 * like RpcServer.Receive does), echo each item, repack in order, compress the response
 * envelope when asked to
 */
function respond(request: Uint8Array, compressResponse = false): Uint8Array {
  const reader = new LiteReader(request);
  const opcode = reader.readUInt16();
  const isCompressed = reader.readByte();
  if (isCompressed) {
    reader.decompress();
  }

  if (opcode === 0 && reader.readByte() === 4) {
    const count = reader.readUInt16();
    const writer = new LiteWriter();
    writer.writeByte(0); // success
    writer.writeByte(0); // compressed flag, set by compressMessage below when asked to
    writer.writeUInt16(count);
    for (let i = 0; i < count; i++) {
      writer.writeByteArray(echo(reader.readByteArray() as Uint8Array));
    }
    const envelope = writer.close();
    return compressResponse ? compressMessage(envelope, 2) : envelope;
  }

  return echo(request);
}

/** installs a fake connection and returns the raw transport messages it received */
function installFakeConnection(options?: { delayFirstMs?: number; compressResponse?: boolean }): Uint8Array[] {
  const sent: Uint8Array[] = [];
  let first = true;
  link.connection = {
    invoke: async (_method: string, data: Uint8Array): Promise<Uint8Array> => {
      sent.push(data);
      if (options?.delayFirstMs && first) {
        first = false;
        await new Promise((resolve) => setTimeout(resolve, options.delayFirstMs));
      }
      return respond(data, options?.compressResponse);
    }
  };
  return sent;
}

afterEach(() => {
  RpcLink.batchMode = RpcBatchMode.Disabled;
  RpcLink.batchWindow = 50;
  RpcLink.maxBatchSize = 64;
  link.batchQueue = [];
  link.batchFlushScheduled = false;
  link.batchSendInFlight = false;
  link.connection = undefined;
});

describe("request batching", () => {
  it("window mode packs concurrent calls into one transport message", async () => {
    const sent = installFakeConnection();
    RpcLink.batchMode = RpcBatchMode.Window;
    RpcLink.batchWindow = 10;

    const results: Uint8Array[] = await Promise.all(
      [0, 1, 2, 3, 4].map((i) => link.sendTransport(req(i), 3))
    );

    expect(sent.length).toBe(1);
    expect(sent[0][0]).toBe(0); // u16 system opcode ...
    expect(sent[0][1]).toBe(0);
    expect(sent[0][3]).toBe(4); // ... type 4: a batch envelope
    results.forEach((res, i) => {
      expect(Array.from(res)).toEqual([0, 0, 1, 0, 0, i]); // responses matched in order
    });
  });

  it("a lone window flush goes out unwrapped", async () => {
    const sent = installFakeConnection();
    RpcLink.batchMode = RpcBatchMode.Window;
    RpcLink.batchWindow = 10;

    const res: Uint8Array = await link.sendTransport(req(7), 3);

    expect(sent.length).toBe(1);
    expect(Array.from(sent[0])).toEqual([1, 0, 0, 7]); // the raw request, no batch envelope
    expect(Array.from(res)).toEqual([0, 0, 1, 0, 0, 7]);
  });

  it("a full batch flushes immediately without waiting for the window", async () => {
    const sent = installFakeConnection();
    RpcLink.batchMode = RpcBatchMode.Window;
    RpcLink.batchWindow = 200;
    RpcLink.maxBatchSize = 3;

    const pending = [0, 1, 2].map((i) => link.sendTransport(req(i), 3));
    expect(sent.length).toBe(1); // flushed synchronously on the third call, window not involved

    const results: Uint8Array[] = await Promise.all(pending);
    results.forEach((res, i) => expect(Array.from(res)).toEqual([0, 0, 1, 0, 0, i]));
  });

  it("coalesce mode sends the first call immediately and packs calls made during flight", async () => {
    const sent = installFakeConnection({ delayFirstMs: 30 });
    RpcLink.batchMode = RpcBatchMode.Coalesce;

    const first: Promise<Uint8Array> = link.sendTransport(req(0), 3);
    expect(sent.length).toBe(1); // went out with no added latency
    expect(Array.from(sent[0])).toEqual([1, 0, 0, 0]); // unwrapped

    const rest: Promise<Uint8Array>[] = [1, 2, 3].map((i) => link.sendTransport(req(i), 3));
    expect(sent.length).toBe(1); // queued behind the in-flight send

    const results = await Promise.all([first, ...rest]);
    expect(sent.length).toBe(2); // the first alone, then one batch of three
    expect(sent[1][3]).toBe(4);
    results.forEach((res, i) => expect(Array.from(res)).toEqual([0, 0, 1, 0, 0, i]));
  });

  it("compresses the whole batch envelope in both directions", async () => {
    const sent = installFakeConnection({ compressResponse: true });
    RpcLink.batchMode = RpcBatchMode.Window;
    RpcLink.batchWindow = 10;

    // highly compressible ~300-byte requests, far above the 80-byte compression threshold
    const bigReq = (marker: number) => {
      const data = new Uint8Array(300).fill(marker);
      data[0] = 1;
      data[1] = 0;
      data[2] = 0; // [opcode][isCompressed] header, raw payload after
      return data;
    };

    const pending = [1, 2, 3].map((i) => link.sendTransport(bigReq(i), 3));
    const results: Uint8Array[] = await Promise.all(pending);

    expect(sent.length).toBe(1);
    // request envelope: [u16 0][isCompressed] - one compression pass over all three requests
    expect(sent[0][0]).toBe(0);
    expect(sent[0][1]).toBe(0);
    expect(sent[0][2]).toBe(1);
    expect(sent[0].length).toBeLessThan(3 * 300);
    // the compressed response envelope round-trips back to full per-item responses
    results.forEach((res, i) => {
      expect(res.length).toBe(302);
      expect(Array.from(res.subarray(0, 2))).toEqual([0, 0]);
      expect(res[10]).toBe(i + 1); // echoed payload intact
    });
  });

  it("an error response rejects every call in the batch", async () => {
    link.connection = {
      invoke: async (): Promise<Uint8Array> => new Uint8Array([1, 0]) // BadRequest
    };
    RpcLink.batchMode = RpcBatchMode.Window;
    RpcLink.batchWindow = 10;

    const a: Promise<Uint8Array> = link.sendTransport(req(0), 3);
    const b: Promise<Uint8Array> = link.sendTransport(req(1), 3);

    await expect(a).rejects.toThrow("Batch request failed");
    await expect(b).rejects.toThrow("MaxBatchSize"); // the BadRequest hint mentions the likely causes
  });

  it("a response count mismatch rejects the batch", async () => {
    link.connection = {
      invoke: async (): Promise<Uint8Array> => {
        const writer = new LiteWriter();
        writer.writeByte(0);
        writer.writeByte(0);
        writer.writeUInt16(1); // one response for two requests
        writer.writeByteArray(new Uint8Array([0, 0]));
        return writer.close();
      }
    };
    RpcLink.batchMode = RpcBatchMode.Window;
    RpcLink.batchWindow = 10;

    const a: Promise<Uint8Array> = link.sendTransport(req(0), 3);
    const b: Promise<Uint8Array> = link.sendTransport(req(1), 3);

    await expect(a).rejects.toThrow("count mismatch");
    await expect(b).rejects.toThrow("count mismatch");
  });

  it("a transport failure rejects every call in the batch", async () => {
    link.connection = {
      invoke: async (): Promise<Uint8Array> => {
        throw new Error("socket closed");
      }
    };
    RpcLink.batchMode = RpcBatchMode.Window;
    RpcLink.batchWindow = 10;

    const a: Promise<Uint8Array> = link.sendTransport(req(0), 3);
    const b: Promise<Uint8Array> = link.sendTransport(req(1), 3);

    await expect(a).rejects.toThrow("socket closed");
    await expect(b).rejects.toThrow("socket closed");
  });
});
