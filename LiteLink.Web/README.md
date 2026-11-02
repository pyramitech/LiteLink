# litelink-web

TypeScript client for [LiteLink](https://www.nuget.org/packages/LiteLink), an RPC framework with a compact binary serialization format for .NET servers. The client is fully handshake-driven: at connect time the server describes its types, functions and permissions, and the client builds its serializers from that description. It never needs to be regenerated or recompiled because the server's API changed.

The client connects over SignalR, which makes it the browser-facing half of LiteLink; it also runs under Node.

## Installation

```
npm install litelink-web
```

`lz4-wasm` (the compression codec) is a WebAssembly module, so a bundler is required in the browser. With Vite:

```
npm install -D vite-plugin-wasm vite-plugin-top-level-await
```

```ts
// vite.config.ts
import wasm from "vite-plugin-wasm";
import topLevelAwait from "vite-plugin-top-level-await";

export default { plugins: [wasm(), topLevelAwait()] };
```

Under Node (tests, scripts), alias the codec to its Node build in your bundler or vitest config:

```ts
resolve: { alias: { "lz4-wasm": "lz4-wasm-nodejs" } }
```

## Usage

The API is static: one connection per page or process.

```ts
import { RpcLink } from "litelink-web";

const ok = await RpcLink.connect("https://host:7276/hub"); // the SignalR hub URL, 1 on success
if (ok !== 1) throw new Error("could not connect");

// dynamic proxy call: Group.Function on the server
const profile = await RpcLink.Remote.Users.GetProfile();
const result = await RpcLink.Remote.Users.Login({ Name: "a", Password: "b" });

// explicit call by hash
await RpcLink.send(RpcLink.functionHasher("Users.Login"), { Name: "a", Password: "b" });

// non-blocking parallel calls
const values = await Promise.all([...Array(20)].map(() => RpcLink.sendMultiChannelMessage("Math.Slow", null)));

// server pushes
RpcLink.registerFunction("Notify", (n) => console.log(n.Text));

// responders for server-initiated Invoke<TResult> calls (return a value or a promise)
RpcLink.registerResponder("ConfirmAction", async (prompt) => await showDialog(prompt));

// API description (the server must enable ShowApi)
const api = await RpcLink.getApiInfo("optional password");

RpcLink.setOnConnectionLost((err) => console.warn("connection lost", err));
RpcLink.disconnect();
```

Models are plain objects whose property names match the C# model properties. `DateTime` values arrive as `Date` objects and are sent as `Date` objects; enums travel as their underlying number.

Failed calls reject with the server's error code in the message: `1` bad request, `2` unauthorized (missing permission, or a multichannel call the server has not granted), `3` uninitialized, `4` shutting down, `255` unhandled exception in the procedure.

## Multichannel calls

A plain call occupies the connection's request slot until it completes. A multichannel call is acknowledged immediately, executes in the background on the server and delivers its response as a push, so up to 256 calls per connection run in parallel:

```ts
const value = await RpcLink.sendMultiChannelMessage("Users.GetProfile", null);

// or route every call through the multichannel path without touching call sites
RpcLink.multiChannelByDefault = true;
```

The server decides per connection whether multichannel is allowed (`RpcLink.AllowMultiChannel` on the server-side link); a denied call rejects with error code 2.

## Request batching

Calls issued concurrently can be packed into one transport message. Items of a batch run sequentially on the server in request order, so earlier effects are visible to later calls:

```ts
import { RpcLink, RpcBatchMode } from "litelink-web";

RpcLink.batchMode = RpcBatchMode.Coalesce; // first call goes out at once, calls made meanwhile follow as one batch
RpcLink.batchWindow = 50;                  // ms, Window mode only: hold calls and flush them together
RpcLink.maxBatchSize = 64;                 // keep at or below the server's MaxBatchSize
```

## Options and reconnects

| Option | Meaning |
| --- | --- |
| `RpcLink.responseTimeout` | Milliseconds any request may take (plain, batched, multichannel, including the handshake) before rejecting with an `RpcTimeoutError`; `0` disables. Default 60000. |
| `RpcLink.multiChannelByDefault` | Send every call as a multichannel request. |
| `RpcLink.batchMode`, `batchWindow`, `maxBatchSize` | Request batching, see above. |

Values set while disconnected are configuration and survive reconnects; values changed while connected last only for that connection and revert on the next `connect()`. This mirrors server-side grants that die with the link: if a login unlocked `multiChannelByDefault`, re-enable it after re-authenticating in your reconnect logic.

When the connection drops, in-flight and queued calls reject immediately with a connection-lost error instead of waiting out `responseTimeout`.

## Building from source

This package lives in the `LiteLink.Web` folder of the LiteLink repository, next to the .NET projects it talks to.

```
npm install
npm run build   # dist/index.js (CommonJS), dist/index.mjs (ESM), dist/index.d.ts
npm test        # vitest; includes golden wire vectors shared with the C# test suite
```

## License

Apache-2.0. Copyright (c) Pyrami Technologies.
