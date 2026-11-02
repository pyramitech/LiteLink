import { HubConnectionState } from "@microsoft/signalr";
import { afterEach, describe, expect, it } from "vitest";
import { RpcLink, RpcBatchMode, RpcTimeoutError } from "../src/index";

// reach through TS-level privacy to drive the client without a real SignalR connection
const link = RpcLink as any;

/** puts every option, its baseline and the session flag back to factory defaults */
function resetOptions(): void {
  link.connection = undefined;
  link.sessionActive = false;
  RpcLink.responseTimeout = 60000;
  RpcLink.batchMode = RpcBatchMode.Disabled;
  RpcLink.batchWindow = 50;
  RpcLink.maxBatchSize = 64;
  RpcLink.multiChannelByDefault = false;
  link.channelRequests = new Array(256).fill(null);
  link.lastChannelIndex = 0;
  link.batchQueue = [];
  link.batchFlushScheduled = false;
  link.batchSendInFlight = false;
  link.functionMaps = undefined;
}

afterEach(resetOptions);

/** a function table with one entry (opcode 5, no argument, no return value) */
function installFunctionMap(): void {
  link.functionMaps = new Map([[1n, { opcode: 5 }]]);
}

describe("responseTimeout", () => {
  it("fails a plain send with RpcTimeoutError when the transport never responds", async () => {
    installFunctionMap();
    link.connection = {
      invoke: () => new Promise<Uint8Array>(() => {}) // never resolves
    };
    RpcLink.responseTimeout = 30;

    await expect(link.send(1n, null)).rejects.toBeInstanceOf(RpcTimeoutError);
  });

  it("fails a hanging batch flush so queued calls don't wait forever", async () => {
    link.connection = {
      invoke: () => new Promise<Uint8Array>(() => {})
    };
    RpcLink.batchMode = RpcBatchMode.Window;
    RpcLink.batchWindow = 5;
    RpcLink.responseTimeout = 30;

    const a: Promise<Uint8Array> = link.sendTransport(new Uint8Array([1, 0, 0, 1]), 3);
    const b: Promise<Uint8Array> = link.sendTransport(new Uint8Array([1, 0, 0, 2]), 3);

    await expect(a).rejects.toThrow("timed out after 30ms");
    await expect(b).rejects.toThrow("timed out after 30ms");
  });

  it("fails the wait for a free multichannel slot when all 256 are occupied", async () => {
    installFunctionMap();
    link.connection = {
      invoke: async (): Promise<Uint8Array> => new Uint8Array([0, 0])
    };
    link.channelRequests = new Array(256).fill({ resolve: () => {}, reject: () => {} });
    RpcLink.responseTimeout = 40;

    await expect(link.sendMultiChannel(1n, null)).rejects.toBeInstanceOf(RpcTimeoutError);
  });
});

describe("multiChannelByDefault", () => {
  /** fake hub that acks multichannel envelopes and pushes their response through the channel table */
  function installMultiChannelFake(): Uint8Array[] {
    const sent: Uint8Array[] = [];
    link.connection = {
      invoke: async (_method: string, data: Uint8Array): Promise<Uint8Array> => {
        sent.push(data);
        if (data[0] === 0 && data[1] === 0 && data[3] === 1) {
          const channel = data[4];
          setTimeout(() => {
            const pending = link.channelRequests[channel];
            link.channelRequests[channel] = null;
            pending?.resolve(new Uint8Array([0, 0])); // success, uncompressed, no return payload
          }, 0);
          return new Uint8Array([0, 0]); // ack
        }
        return new Uint8Array([0, 0]);
      }
    };
    return sent;
  }

  it("wraps plain sends in multichannel envelopes when enabled", async () => {
    installFunctionMap();
    const sent = installMultiChannelFake();

    await link.send(1n, null);
    expect(sent[0][0]).toBe(5); // plain request starts with its opcode

    RpcLink.multiChannelByDefault = true;
    await link.send(1n, null);
    // [u16 0][compressed 0][type 1][channel]
    expect(sent[1][0]).toBe(0);
    expect(sent[1][1]).toBe(0);
    expect(sent[1][3]).toBe(1);

    RpcLink.multiChannelByDefault = false;
    await link.send(1n, null);
    expect(sent[2][0]).toBe(5);
  });

  it("surfaces a denied multichannel request with the AllowMultiChannel hint", async () => {
    installFunctionMap();
    link.connection = {
      invoke: async (): Promise<Uint8Array> => new Uint8Array([2, 0]) // Unauthorized ack
    };
    RpcLink.multiChannelByDefault = true;

    await expect(link.send(1n, null)).rejects.toThrow("AllowMultiChannel");
  });
});

describe("connection loss", () => {
  it("fails a pending multichannel call instantly instead of waiting out responseTimeout", async () => {
    installFunctionMap();
    link.connection = {
      invoke: async (): Promise<Uint8Array> => new Uint8Array([0, 0]) // ack; the response push never comes
    };

    const pending = link.sendMultiChannel(1n, null);
    await new Promise((resolve) => setTimeout(resolve, 10)); // past the ack, awaiting the push

    link.handleConnectionLost();

    await expect(pending).rejects.toThrow("Connection was lost");
  });

  it("fails requests still queued for batching instantly", async () => {
    link.connection = {
      invoke: async (): Promise<Uint8Array> => new Uint8Array([0, 0])
    };
    RpcLink.batchMode = RpcBatchMode.Window;
    RpcLink.batchWindow = 30000;

    const queued: Promise<Uint8Array> = link.sendTransport(new Uint8Array([1, 0, 0, 1]), 3);
    link.handleConnectionLost();

    await expect(queued).rejects.toThrow("queued for batching");
  });
});

describe("option baseline", () => {
  it("reverts mid-connection changes on reconnect and keeps configured values", async () => {
    // configured while disconnected: goes into the baseline
    RpcLink.maxBatchSize = 32;

    // simulate an established session: sets are now session-only
    link.connection = { state: HubConnectionState.Connected };
    link.sessionActive = true;
    RpcLink.multiChannelByDefault = true;
    RpcLink.maxBatchSize = 8;
    RpcLink.batchMode = RpcBatchMode.Window;
    expect(RpcLink.multiChannelByDefault).toBe(true);

    // the connection drops; the reconnect attempt restores the baseline even though it fails
    link.connection.state = HubConnectionState.Disconnected;
    await RpcLink.connect("http://127.0.0.1:1");

    expect(RpcLink.multiChannelByDefault).toBe(false);
    expect(RpcLink.batchMode).toBe(RpcBatchMode.Disabled);
    expect(RpcLink.maxBatchSize).toBe(32); // configured before the session, kept
  }, 15000);
});
