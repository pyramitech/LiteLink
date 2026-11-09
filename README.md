# LiteLink

> Library primarily human written and used in production. AI was used to clean up generator code: moved StringBuilder code into .cshtml templates, built benchmarks, also found and fixed some edge cases. AI was also used for documentation and with preparing the public release. Library is pretty old at this point, at the time all the alternatives either produced much bigger payload or generated code from a special custom syntax. Primarily focused on C# <-> C# and C# <-> TS communications.


LiteLink is an RPC framework with a compact binary serialization format for .NET servers, .NET clients and TypeScript/browser clients. A Roslyn source generator turns plain static methods into a complete RPC surface at compile time: serializers, opcode tables, permission masks and the connection handshake are all pregenerated, so there is no runtime reflection on the server's hot path.

Clients are fully dynamic. At connect time the server sends a handshake describing its types, functions and permissions, and clients build their serializers from it. The protocol is therefore self-describing: a client never needs to be recompiled because the server's API changed, and the same client library works against any LiteLink server.

## Contents

- [Solution layout](#solution-layout)
- [Requirements](#requirements)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Defining the API](#defining-the-api)
- [Serialization](#serialization)
- [The connection link](#the-connection-link)
- [Permissions](#permissions)
- [Server hooks and middleware](#server-hooks-and-middleware)
- [Transports](#transports)
- [The C# client](#the-c-client)
- [The TypeScript client](#the-typescript-client)
- [Multichannel calls](#multichannel-calls)
- [Request batching](#request-batching)
- [Server-to-client push](#server-to-client-push)
- [API description endpoint](#api-description-endpoint)
- [Compression](#compression)
- [Gateway](#gateway)
- [Error codes](#error-codes)
- [The source generator](#the-source-generator)
- [Wire protocol reference](#wire-protocol-reference)
- [Building and testing](#building-and-testing)
- [Known limitations](#known-limitations)

## Solution layout

| Project | Purpose |
| --- | --- |
| `LiteLink` | Server core: `RpcServer`, links, permissions, middleware, call context |
| `LiteLink.Client` | .NET client: `RpcClient`, dynamic serialization driven by the handshake |
| `LiteLink.Shared` / `LiteLink.Shared.Func` | Shared sources (serializer, wire types, CRC) compiled into both server and client |
| `LiteLink.Generator` | Roslyn incremental source generator (netstandard2.0) |
| `LiteLink.SignalR` / `LiteLink.SignalR.Client` | SignalR transport (ASP.NET Core hub / HubConnection client) |
| `LiteLink.Tcp` / `LiteLink.Tcp.Client` | Raw TCP transport with optional TLS |
| `LiteLink.Quic` / `LiteLink.Quic.Client` | QUIC transport (always TLS 1.3) |
| `LiteLink.Pipes` / `LiteLink.Pipes.Client` | Named pipe transport for same-machine IPC (Windows and Linux) |
| `LiteLink.Transport.Shared` | Shared frame protocol used by the socket transports and the gateway |
| `LiteLink.Gateway` | Protocol-transparent gateway: intercept, analyze, transfer |
| `LiteLink.Tests`, `LiteLink.Generator.Tests`, `LiteLink.ClientGen.Tests*` | Test suites (see [Building and testing](#building-and-testing)) |
| `LiteLink.Benchmarks`, `LiteLink.Benchmarks.Host` | BenchmarkDotNet suite for the client serialization paths |
| `LiteLink.Web` | TypeScript client (browser and Node), published on npm as `@pyrami/litelink` — not part of the solution, see [The TypeScript client](#the-typescript-client) |

## Requirements

- .NET 10.0 for all runtime projects. The source generator itself targets netstandard2.0, as required by Roslyn.
- QUIC additionally requires msquic and TLS 1.3 support: Windows 11 / Windows Server 2022 or newer, or Linux with `libmsquic` installed. Check availability at runtime with `LiteLinkQuicServer.IsSupported`.
- The TypeScript client requires a bundler-based environment for the browser (`lz4-wasm` is a WebAssembly module). Under Node, alias `lz4-wasm` to `lz4-wasm-nodejs`.

## Installation

Everything ships on NuGet under the `LiteLink` prefix. A server references the core package plus one or more transports; a client references the client core plus one or more client transports. The source generator is bundled inside `LiteLink` and `LiteLink.Client` as an analyzer, so it never needs to be referenced by hand.

| Package | Reference it when |
| --- | --- |
| `LiteLink` | You host an RPC surface (server core, includes the source generator) |
| `LiteLink.SignalR` | Serving over a SignalR hub inside ASP.NET Core (browsers, the TypeScript client) |
| `LiteLink.Tcp` | Serving over raw TCP, optionally TLS; standalone, no ASP.NET Core |
| `LiteLink.Quic` | Serving over QUIC (TLS 1.3, needs msquic) |
| `LiteLink.Pipes` | Serving same-machine clients over named pipes |
| `LiteLink.Gateway` | Running a protocol-transparent gateway in front of your servers |
| `LiteLink.Client` | You call a LiteLink server from .NET (client core, includes the source generator for `[assembly: RpcClientModels]`) |
| `LiteLink.SignalR.Client`, `LiteLink.Tcp.Client`, `LiteLink.Quic.Client`, `LiteLink.Pipes.Client` | Client transports, one per server transport |

```
dotnet add package LiteLink
dotnet add package LiteLink.Tcp
```

A single project may reference both `LiteLink` and `LiteLink.Client` (a service that also calls another LiteLink service); the packages make sure the bundled generator runs only once, and the client side goes behind an extern alias as described in [Client-side generated serializers](#client-side-generated-serializers).

The TypeScript client is published on npm as `@pyrami/litelink`.

## Quick start

### 1. Define the RPC surface

Reference the `LiteLink` package (the source generator comes with it), then declare procedures as static methods. Exactly one class in the process carries the `[RpcServer]` attribute; the generator emits a module initializer that constructs the server singleton and registers everything when the assembly loads.

```csharp
using LiteLink;

public class UserLink : RpcLink
{
    // per-connection state, anything you like
    public int UserId;
}

public enum Permissions
{
    Admin = 0,
    Viewer = 1,
}

[RpcServer<UserLink>(UseCompression = true, PermissionGroups = typeof(Permissions))]
[RpcGroup("Users")]
public static class UserProcedures
{
    [RpcFunc("Login")]
    public static LoginResult Login(UserLink link, LoginRequest request)
    {
        // validate, then attach state and permissions to this connection
        link.UserId = 42;
        link.GrantPermission("Viewer");
        return new LoginResult { Ok = true };
    }

    [RpcFunc("GetProfile", PermissionGroups = new[] { "Viewer" })]
    public static async Task<Profile> GetProfile(UserLink link)
    {
        return await LoadProfileAsync(link.UserId);
    }
}
```

Every model type reachable from a procedure argument or return value gets a generated serializer automatically.

### 2. Host it

Any combination of transports can serve the same `RpcServer` singleton simultaneously; they share one procedure table, one permission set and one connection list.

```csharp
// SignalR inside an ASP.NET Core app (hub at /hub)
builder.Services.AddSignalR().AddLiteLinkProtocol();
...
app.UseLiteLink();

// raw TCP, standalone, no ASP.NET required
var tcp = new LiteLinkTcpServer(new LiteLinkTcpServerOptions { Port = 9090, UseTls = true });
tcp.Start();

// QUIC (always TLS 1.3)
var quic = new LiteLinkQuicServer(new LiteLinkQuicServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 9091)
});
await quic.StartAsync();
```

When no certificate is configured, the TCP (with `UseTls = true`) and QUIC servers generate a self-signed certificate at startup. See [Transports](#transports).

### 3. Call it from .NET

```csharp
using LiteLink.Tcp.Client; // or LiteLink.SignalR.Client / LiteLink.Quic.Client

var client = TcpConnection.Create("myhost", 9090, new TcpConnectionOptions { UseTls = true });
await client.Start(); // connects and performs the handshake

var result = await client.Send<LoginResult>("Users.Login", new LoginRequest { Name = "a", Password = "b" });
var profile = await client.Send<Profile>("Users.GetProfile", null);
```

### 4. Call it from TypeScript

```ts
import { RpcLink } from "@pyrami/litelink";

await RpcLink.connect("https://myhost:7276/hub"); // SignalR hub URL

const result = await RpcLink.Remote.Users.Login({ Name: "a", Password: "b" });
const profile = await RpcLink.Remote.Users.GetProfile();
```

## Defining the API

### The `[RpcServer]` attribute

Placed on exactly one class per process. The generic argument is your link type (see [The connection link](#the-connection-link)).

| Property | Meaning |
| --- | --- |
| `UseCompression` | Compress responses larger than 80 bytes with LZ4 |
| `PermissionGroups` | An enum type whose members become the permission groups |
| `ShowApi` | Pregenerate an API description served on request (see [API description endpoint](#api-description-endpoint)) |
| `ApiPassword` | Optional password required to retrieve the API description |

### Groups and functions

- `[RpcGroup("Name")]` on a static class prefixes its functions; the full RPC name is `Group.Function`.
- `[RpcFunc("Name")]` marks a procedure. `PermissionGroups = new[] { ... }` restricts it to connections holding at least one of the listed groups. `Compression = RpcCompression.Disabled` (or `Enabled`) overrides the server-wide compression setting for this function's responses.

### Procedure signatures

The first parameter is always the link. An optional second parameter is the single call argument (wrap multiple values in a model, tuple or collection). Supported return shapes:

```csharp
void          Fn(TLink link)                 // fire-and-acknowledge
T             Fn(TLink link, TArg arg)       // synchronous value
Task          Fn(TLink link)                 // async, no value
Task<T>       Fn(TLink link, TArg arg)       // async value
```

Function names are transmitted as CRC hashes; the generator picks the narrowest hash width (8/16/32/64 bit) that is collision-free for your API and reports a diagnostic if even 64 bits collide.

## Serialization

LiteLink uses its own compact binary format (`LiteWriter` / `LiteReader`). Supported types:

- Primitives: `bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`
- `string` (UTF-8, null-distinguishable), `DateTime`, `Guid`, `byte[]`
- Enums of any underlying type, serialized at their native width (a `byte`-backed enum costs one byte on the wire)
- `List<T>`, arrays, `Dictionary<TKey, TValue>`, `ValueTuple<...>`, `Nullable<T>` — nested arbitrarily
- User classes and structs with public settable properties, including inherited properties

Null is representable for strings, collections, dictionaries, nullables and object references, and round-trips as null. Lengths use a variable-width encoding (1 to 8 bytes), so small collections carry minimal overhead.

Types are discovered by walking procedure arguments and return values, plus every type passed through `link.Invoke<T>(...)` anywhere in the compilation. Property names are transmitted once in the handshake as an indexed string table; unknown properties on either side are skipped safely, which gives you loose schema evolution (adding a property does not break older clients).

## The connection link

Each connection gets one instance of your `RpcLink` subclass, created when the transport accepts the connection. It is passed to every procedure and is the natural place for per-connection state:

```csharp
public class UserLink : RpcLink
{
    public int UserId;
    public string SessionToken;
}
```

`RpcLink` members:

| Member | Purpose |
| --- | --- |
| `Connection` | The underlying transport connection |
| `Permissions` | The connection's permission bitmask |
| `GrantPermission(string / bitmask)` | Add a permission group |
| `SetPermission(string / bitmask)` | Replace the permission set |
| `TogglePermission(bitmask)` | Flip permission bits |
| `Invoke<T>(string function, T model)` | Push a message to this client (see [Server-to-client push](#server-to-client-push)) |

Enumerate or select connections server-side via `RpcServer<TLink>`:

```csharp
var server = (RpcServer<UserLink>)RpcServer.Instance;
foreach (var link in server.GetConnections(x => x.UserId == 42))
    link.Invoke("Notify", new Notice { Text = "hello" });
```

## Permissions

Declare groups as an enum and reference it from the attribute:

```csharp
public enum Permissions { Admin = 0, Viewer = 1 }

[RpcServer<UserLink>(PermissionGroups = typeof(Permissions))]
```

Up to 128 groups are supported (two 64-bit masks). A procedure listing `PermissionGroups` rejects callers that hold none of the listed groups with `ErrorCodes.Unauthorized`; procedures without the property are open to every connection. Grant permissions from server code (typically inside a login procedure) via `link.GrantPermission("Admin")`.

Permission names are transmitted in the handshake as CRC hashes, so clients can reason about permissions without ever seeing the plain names.

## Server hooks and middleware

All hooks live on the `RpcServer` instance (`RpcServer.Instance`):

```csharp
RpcServer.Instance.OnConnected    = link => ...;   // connection established
RpcServer.Instance.OnDisconnected = link => ...;   // connection closed
RpcServer.Instance.OnError        = ex   => ...;   // errors with no caller to surface to
```

### Call middleware

`CallMiddleware` wraps every procedure invocation. While a call runs, `RpcCallContext.Current` is an ambient (async-local) context, so any log written inside the call's async flow can be correlated with it:

```csharp
RpcServer.Instance.CallMiddleware = async (call, next) =>
{
    var timer = Stopwatch.StartNew();
    Log($"start {call.FunctionName} (opcode {call.Opcode}, multichannel: {call.IsMultiChannel})");
    try
    {
        return await next(call);
    }
    finally
    {
        Log($"end {call.FunctionName} after {timer.ElapsedMilliseconds}ms");
    }
};
```

`RpcCallContext` exposes `Opcode`, `FunctionName`, `Link`, `IsMultiChannel` and a free `Tag` slot. When `CallMiddleware` is null (the default), calls are dispatched directly with no overhead. Exceptions escaping a procedure or the middleware are reported to `OnError` and converted into an `ErrorCodes.Unhandled` response.

### Other hooks

- `ProxyFunction` (`Func<byte[], Task<byte[]>>`): when set, receives every raw message before normal dispatch; return the raw response. Superseded for most purposes by the [gateway](#gateway).
- `Shutdown()`: refuses new connections and answers requests with `ErrorCodes.ShuttingDown`. Process-wide.

## Transports

All transports are interchangeable adapters between a connection and the `RpcServer` singleton. A client behaves identically on any of them, and several can serve one process at the same time.

### Per-connection parallelism

By default one connection executes one request at a time. On the socket transports (TCP, QUIC, pipes) this is enforced by `MaxParallelRequestsPerConnection` (default 1) on the server options: plain requests queue and run sequentially in arrival order, so a single client cannot occupy unbounded server concurrency by spraying parallel requests — the DoS analogue of the batching limits. When the queue fills, the server stops reading that connection's frames and the transport's own flow control throttles the sender.

Traffic that must not queue behind a slow procedure bypasses the limit: handshakes, API info requests and responses to server-initiated `Invoke<TResult>` (so an invoke completes even at limit 1, without the parallelism requirement SignalR imposes). Multichannel envelopes also bypass it — their ack is cheap and the procedure runs in the background — which makes [multichannel](#multichannel-calls) *the* way for a client to run calls in parallel, gated per client by `RpcLink.AllowMultiChannel` and bounded by the 256-channel table. Batch envelopes run under the limit; their items execute sequentially anyway.

Set `MaxParallelRequestsPerConnection` to 0 for unlimited parallelism, or to a higher value for a fixed budget. On SignalR the equivalent knob is the hub-global `MaximumParallelInvocationsPerClient` (see [Server-to-client messages](#server-to-client-messages)); pairing it with `AllowMultiChannel` gives the same model there: sequential by default, parallelism as a per-client grant.

> Note: concurrent plain `Send`s from one connection serialize under this default. Fast calls pipeline with no added round-trips, so throughput is mostly unaffected; deliberately parallel work should use multichannel or a raised limit.

### SignalR

Server: reference `LiteLink.SignalR`, then

```csharp
builder.Services.AddSignalR().AddLiteLinkProtocol();
app.UseLiteLink(); // maps the hub at /hub
```

Client: `SignalRConnection.Create("https://host:7276", reconnectAction)` from `LiteLink.SignalR.Client` — the `/hub` suffix is appended automatically. (The TypeScript client's `RpcLink.connect` takes the full hub URL including `/hub`.) SignalR is the transport of choice for browsers and direct-internet clients; it benefits from the full ASP.NET Core stack (HTTPS, proxies, WebSockets negotiation).

### TCP

Server (`LiteLink.Tcp`), standalone:

```csharp
var server = new LiteLinkTcpServer(new LiteLinkTcpServerOptions
{
    Address = IPAddress.Any,
    Port = 9090,          // 0 picks a free port; read back server.Port
    UseTls = true,        // optional; a self-signed certificate is generated when none is set
    // Certificate = myCert,
    // MaxParallelRequestsPerConnection = 1, // the default, see Per-connection parallelism
});
server.Start();
...
await server.StopAsync();
```

Client (`LiteLink.Tcp.Client`):

```csharp
var client = TcpConnection.Create("host", 9090, new TcpConnectionOptions
{
    UseTls = true,
    // AllowInvalidCertificates = true is the default, see below
    // CertificateValidation = (s, cert, chain, err) => ... for pinning
}, reconnectAction: null);
await client.Start();
```

### QUIC

Server (`LiteLink.Quic`), always TLS 1.3 with ALPN `"litelink"`:

```csharp
var server = new LiteLinkQuicServer(new LiteLinkQuicServerOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 9091),
    // Certificate = myCert, // autogenerated when omitted
});
await server.StartAsync();
```

Client (`LiteLink.Quic.Client`): `QuicClientConnection.Create("host", 9091)`. Guard server startup with `LiteLinkQuicServer.IsSupported` on platforms where msquic may be missing.

### Named pipes (inter-process)

For processes on the same machine, the pipe transport (`LiteLink.Pipes` / `LiteLink.Pipes.Client`) skips the network stack entirely: no ports, no firewall rules, no certificates. It works on Windows and Linux — .NET implements named pipes over Unix domain sockets there. Typical uses are sidecar processes, local agents and a gateway co-located with its backend.

```csharp
// server
var server = new LiteLinkPipeServer(new LiteLinkPipeServerOptions { PipeName = "myservice" });
server.Start();

// client
var client = PipeConnection.Create("myservice");
await client.Start();
```

Access control comes from the operating system instead of TLS: with `CurrentUserOnly = true` (the default on both sides) only processes running as the same user can connect. The setting must match between server and client; set it to false on both when the processes run under different accounts. `PipeConnectionOptions.ServerName` defaults to `"."` (the local machine); connecting to a remote machine's pipe is a Windows-only feature.

#### Peer identity and authorization

For cross-account scenarios (for example a privileged service with lower-privilege clients), the server can identify and screen connecting processes:

```csharp
var server = new LiteLinkPipeServer(new LiteLinkPipeServerOptions
{
    PipeName = "updater",
    CurrentUserOnly = false,
    AuthorizeClient = identity =>
        Task.FromResult(identity.UserName == "ExpectedAccount") // kernel-asserted
});
```

`AuthorizeClient` runs after accept and before any message is exchanged; returning false (or throwing) drops the connection. The `PipeClientIdentity` carries the client's account name (both platforms), process id (Windows and Linux) and uid/gid (Linux). The same identity is available inside procedures via `(link.Connection as PipeServerConnection)?.ClientIdentity`. On Windows, clients connect with `TokenImpersonationLevel.Identification` by default, which lets the server read who they are without being able to act on their behalf.

Treat these two pieces of information very differently. The user identity is asserted by the kernel and cannot be forged — an approved-user list built on it is real authentication. The process id is also kernel-asserted, but anything derived from it (executable path, code signature) is hardening only: processes of the same user can inject into each other, and PIDs can be reused. In particular, for a privileged service such as an auto-updater, do not rely on caller verification for security — design the service so that even a hostile caller is harmless: expose narrow procedures (no client-supplied paths or binaries), verify update payload signatures against your publisher key inside the service, verify after copying into a protected location, and enforce version monotonicity.

### Certificates and validation

- The QUIC server, and the TCP server with `UseTls = true`, autogenerate a self-signed certificate when `Certificate` is not provided (ECDSA P-256, one year validity, SANs for localhost, loopback and the machine's host name). The certificate in use is exposed as `server.Certificate`.
- Clients accept invalid certificates by default (`AllowInvalidCertificates = true`). The TCP and QUIC transports are intended for same-network deployments where self-signed certificates are the norm; you still get encryption, just not endpoint authentication. For hardening, set `AllowInvalidCertificates = false` for platform chain validation, or supply a `CertificateValidation` callback to pin the server's certificate.
- On Windows, QUIC requires certificates with the serverAuth EKU and a persisted (non-ephemeral) private key. The autogenerated certificates satisfy this; if you create your own in memory, round-trip it through a PFX export.

### Client-side connection API

Every transport client implements the same `IConnection` contract used by `RpcClient`: `Connect`, `Invoke`, `Terminate`, `IsConnected`, `IsConnecting`. The `Create(...)` factories accept an optional `reconnectAction` invoked when the connection drops; calling `RpcClient.Start()` again reconnects and re-handshakes. On a drop the built-in transports also notify the client (`RpcClient.OnConnectionLost()`) before running `reconnectAction`, which is what fails pending multichannel calls and queued batch items instantly — custom `IConnection` implementations should do the same.

## The C# client

`RpcClient` (in `LiteLink.Client`) is transport-agnostic and entirely handshake-driven.

```csharp
var client = TcpConnection.Create("host", 9090);
await client.Start();

// typed call by name
var res = await client.Send<Profile>("Users.GetProfile", null);

// dynamic proxy, equivalent to the above
var res2 = await client.Remote.Users.GetProfile<Profile>();

// non-blocking parallel calls (see Multichannel calls)
var res3 = await client.SendMultiChannelMessage<Profile>("Users.GetProfile", null);

// receive server pushes
client.RegisterFunction<Notice>("Notify", n => Console.WriteLine(n.Text));

// answer server-initiated Invoke<TResult> calls (sync or async overload)
client.RegisterFunction<Prompt, DialogResult>("ConfirmAction", p => Decide(p));

// API description (server must enable ShowApi)
var api = await client.GetApiInfo("optional password");
```

Additional members:

| Member | Purpose |
| --- | --- |
| `ResponseTimeout` | How long any request may take — plain, batched or multichannel, including the wait for a free multichannel slot and the `Start` handshake — before failing with a `TimeoutException` (default 60 s, `Timeout.InfiniteTimeSpan` disables) |
| `MultiChannelByDefault` | Send every `Send`/`Remote` call as a multichannel request so they run in parallel on the server; requires the server-side grant (see [Multichannel calls](#multichannel-calls)) |
| `OnError` | Errors that cannot be surfaced to a caller (e.g. a response arriving after its timeout) |
| `Connection` | The underlying `IConnection` |
| `BatchMode`, `BatchWindow`, `MaxBatchSize` | Request batching configuration (see [Request batching](#request-batching)) |
| `EnableSerializationCache` | Compile per-type read/write plans from the handshake once instead of re-walking the type maps with reflection on every call (default true; disable to force the original walk logic, both produce identical bytes) |
| `EnableGeneratedSerializers` | Bind generated serializers for models whose structure matches the handshake (default true, see below) |
| `GeneratedSerializerBindings` | How many types this connection bound to generated serializers, for diagnostics |

Options distinguish configuration from session state: values set while disconnected (including everything set before the first `Start()`) are configuration and survive reconnects; values changed while connected last only for that connection. Every `Start()` restores the configured values first — so if a login unlocked `MultiChannelByDefault`, a dropped connection reverts it, matching the server-side grant dying with the link. Re-apply session settings in your reconnect logic after re-authenticating.

Disconnects fail fast, not by timer: the moment the connection drops, in-flight plain requests, pending multichannel calls and requests still queued for batching all fail immediately with a connection-lost error instead of waiting out `ResponseTimeout`. The built-in transports report the drop automatically; a custom `IConnection` should call `RpcClient.OnConnectionLost()` when it detects one.

### Client-side generated serializers

The C# client is handshake-driven, so by default it serializes dynamically. Marking a client assembly with `[assembly: RpcClientModels]` makes the generator emit typed serializers for every model used in typed `Send<T>`, `SendMultiChannelMessage<T>` and `RegisterFunction<T>` calls; types only reachable through the dynamic proxy are listed explicitly:

```csharp
[assembly: RpcClientModels(typeof(SomeProxyOnlyModel))]
```

Each generated serializer carries a structural fingerprint of the wire layout it implements. At handshake time the client computes the same fingerprint from the server's declared type table and binds the generated code only where they match exactly — a model the server has since changed falls back to the dynamic path *per type*, so a partial mismatch degrades only the affected models and everything keeps working. `EnableGeneratedSerializers = false` forces the dynamic path for comparison; all paths produce identical bytes.

Relative performance for a nested model echo over the in-memory transport (see `LiteLink.Benchmarks`): the compiled plan cache is roughly 2x faster than the original reflection walk, and generated serializers roughly 3.5x, with lower allocations. Primitive-only payloads (large `List<int>` etc.) gain mostly from the plan cache alone.

One structural note: an assembly can be a generated server or a generated client, not both — `[RpcClientModels]` is ignored where an `[RpcServer]` exists (the server already generates serializers for its models). In-process test setups that reference both `LiteLink` and `LiteLink.Client` should keep the server side behind an extern alias, as `LiteLink.ClientGen.Tests` demonstrates.

Failed calls throw with the server's error code in the message (see [Error codes](#error-codes)).

## The TypeScript client

The client is published on npm as `@pyrami/litelink` (source in the `LiteLink.Web` folder):

```
npm install @pyrami/litelink
```

Usage (the API is static; one connection per page/process):

```ts
import { RpcLink } from "@pyrami/litelink";

const ok = await RpcLink.connect("https://host:7276/hub"); // 1 on success
if (ok !== 1) throw new Error("could not connect");

// dynamic proxy call
const profile = await RpcLink.Remote.Users.GetProfile();

// explicit call by hash
await RpcLink.send(RpcLink.functionHasher("Users.Login"), { Name: "a", Password: "b" });

// non-blocking parallel calls
const value = await RpcLink.sendMultiChannelMessage("Users.GetProfile", null);

// server pushes
RpcLink.registerFunction("Notify", (n) => console.log(n.Text));

// responders for server-initiated Invoke<TResult> calls (value or promise)
RpcLink.registerResponder("ConfirmAction", async (prompt) => await showDialog(prompt));

// API description
const api = await RpcLink.getApiInfo("optional password");

RpcLink.setOnConnectionLost((err) => ...);
RpcLink.disconnect();
```

The TypeScript client currently connects over SignalR only, which matches its role as the browser-facing client. Models are plain objects whose property names match the C# model properties.

The client mirrors the C# option surface: `RpcLink.responseTimeout` (milliseconds, 0 disables) bounds every request — plain, batched and multichannel, the slot wait and the handshake — failing them with an `RpcTimeoutError`; `RpcLink.multiChannelByDefault` sends every call as a multichannel request (see [Multichannel calls](#multichannel-calls)); and options changed mid-connection revert on the next `connect()`, while values set before connecting persist as configuration.

Under Node (tests, scripts), alias the compression module in your bundler or vitest config:

```ts
resolve: { alias: { "lz4-wasm": "lz4-wasm-nodejs" } }
```

## Multichannel calls

A regular call occupies the request/response slot of the connection until it completes. Multichannel calls avoid that: the request is wrapped in a channel envelope, the server acknowledges immediately and executes the procedure in the background, and the real response is delivered as a push tagged with the channel number. Up to 256 calls per connection can be in flight simultaneously.

```csharp
// C#
var results = await Task.WhenAll(
    Enumerable.Range(0, 20).Select(_ => client.SendMultiChannelMessage<int>("Math.Slow", null)));
```

```ts
// TypeScript
const results = await Promise.all([...Array(20)].map(() => RpcLink.sendMultiChannelMessage("Math.Slow", null)));
```

Use multichannel for anything slow or issued in parallel; use plain `Send` for cheap sequential calls (it has slightly less overhead). Requests that receive no response within `ResponseTimeout` fail and release their channel. Channels are per connection; a disconnect fails all in-flight multichannel calls immediately, so callers never hang or wait out their timeout on a dead connection.

Multichannel calls run through the same permissions and middleware as regular calls; `RpcCallContext.IsMultiChannel` distinguishes them.

### The per-client parallelism grant

Combined with the socket transports' default of one plain request at a time (see [Per-connection parallelism](#per-connection-parallelism)), multichannel is *the* mechanism by which a connection runs calls in parallel — and the server controls it per client:

```csharp
// server: make parallelism an earned privilege instead of the default
RpcServer.Instance.AllowMultiChannelByDefault = false;

[RpcFunc("Login")]
public static bool Login(MyLink link, Credentials c)
{
    if (!Verify(c)) return false;
    link.GrantPermission("User");
    link.AllowMultiChannel = true; // this connection may now run calls in parallel
    return true;
}
```

A denied multichannel request fails with `Unauthorized` (error code 2) before the procedure runs; plain sequential calls are unaffected. The grant lives on the link, so it dies with the connection — a reconnecting client starts from `AllowMultiChannelByDefault` again. With the default of `true`, existing deployments keep their historical behavior.

On the client, `MultiChannelByDefault` routes every `Send`/`Remote` call through the multichannel path without touching call sites, which pairs naturally with the grant:

```csharp
// C# client
if (await client.Send<bool>("Auth.Login", credentials))
    client.MultiChannelByDefault = true; // all further calls run in parallel
```

```ts
// TypeScript
if (await RpcLink.Remote.Auth.Login(credentials)) RpcLink.multiChannelByDefault = true;
```

Because the setting was changed mid-connection it automatically reverts when the connection is re-established, keeping client expectation and server grant in step; re-enable it after re-authenticating in your reconnect logic. System traffic (handshake, API info, invoke responses) never routes through the multichannel path, and batching composes with it — multichannel envelopes batch like any other request.

## Request batching

Both clients can pack multiple concurrent calls into a single transport message. The server unpacks the batch, runs the calls sequentially in request order (each through the normal permission and middleware pipeline) and returns the responses in the same order — a batched sequence behaves exactly like the same calls issued one after another, so an earlier call's effects are visible to every later call in the batch. This trades per-message transport overhead (SignalR hub invocations, frame headers, syscalls) against a small amount of latency, which pays off for chatty clients issuing many small calls.

Because items run sequentially, a slow call delays the rest of its batch; calls that should run in parallel belong in multichannel requests (which batch fine as envelopes, but execute in the background).

```csharp
var client = TcpConnection.Create("host", 9090);
client.BatchMode = RpcBatchMode.Coalesce;
await client.Start();
```

```ts
import { RpcLink, RpcBatchMode } from "@pyrami/litelink";

RpcLink.batchMode = RpcBatchMode.Coalesce;
RpcLink.batchWindow = 50;                  // ms, Window mode only
RpcLink.maxBatchSize = 64;
```

Two strategies are available:

| Mode | Behavior |
| --- | --- |
| `RpcBatchMode.Coalesce` | The first call goes out immediately; calls made while a send is in flight queue up and follow as one batch. Adds no latency on an idle connection and packs automatically under load. The recommended default when enabling batching. |
| `RpcBatchMode.Window` | Calls are held for `BatchWindow` (default 50 ms) and flushed together. Packs most aggressively, but every call pays up to the window in added latency. |

In both modes a flush containing a single request goes out unwrapped, so batching adds no overhead when there is nothing to pack, and a full batch (`MaxBatchSize` requests, default 64) flushes immediately.

Call semantics are unchanged: each call in a batch keeps its own error code, so a failing call rejects only its own task, and per-function compression overrides are honored. Plain and multichannel calls both participate; the handshake and responses to server-initiated invokes are never delayed by batching.

Batches compress as a whole rather than per call: requests are packed raw and the envelope is LZ4-compressed in one pass (both directions), so many small calls share compression context that individually-sized messages would each fall below the threshold for. If a batch contains a response from a function marked `Compression = RpcCompression.Disabled`, the server leaves the envelope plain and compresses only the willing responses individually, so pre-compressed data is never recompressed. A lone call flushed by itself is compressed individually, exactly as if batching were off.

The server enforces limits so a client cannot amplify one transport message into unbounded work: batches larger than `RpcServer.MaxBatchSize` (default 64, adjustable at runtime) are rejected whole with `BadRequest`, and nested batches — including batches hidden inside multichannel envelopes — are rejected as well. Keep the client's `MaxBatchSize` at or below the server's.

Batching is opt-in per client on both the C# and the TypeScript client. Servers without batching support reject batches with `BadRequest` while individual calls continue to work, so only enable it against servers that support it. A gateway forwards batches transparently, but a `GatewayInterceptor` sees a batch as one opaque system message and cannot inspect the calls inside it.

## Server-to-client messages

The server can send one-way pushes to any connected client, or call the client and await a response.

### Fire-and-forget: `Send`

```csharp
// server
link.Send("ChatMessage", new ChatMessage { From = "bob", Text = "hi" });
```

```csharp
// C# client
client.RegisterFunction<ChatMessage>("ChatMessage", msg => ...);
```

```ts
// TypeScript client
RpcLink.registerFunction("ChatMessage", (msg) => ...);
```

### Calls with a response: `Invoke<TResult>`

```csharp
// server: awaits the client's answer (default timeout 60 s, per-link InvokeResponseTimeout)
var answer = await link.Invoke<DialogResult>("ConfirmAction", new Prompt { Text = "Proceed?" });
```

```csharp
// C# client: a responder returns the result (sync or async overload)
client.RegisterFunction<Prompt, DialogResult>("ConfirmAction", async prompt => await ShowDialogAsync(prompt));
```

```ts
// TypeScript client: registerResponder, the handler may return a value or a promise
RpcLink.registerResponder("ConfirmAction", async (prompt) => await showDialog(prompt));
```

The call faults if the client's responder throws, fails fast when the client has no responder registered under that name, fails on disconnect, and times out otherwise (`timeout` parameter or `link.InvokeResponseTimeout`). Up to 256 invokes per connection can be in flight concurrently, and they run through the gateway transparently. The result type and the model's runtime type must be part of the generated type map; the generator registers both automatically for `Invoke`/`Send` calls it can see.

When hosting over SignalR, the hub must allow parallel invocations from one client, otherwise the client's response queues behind the very procedure that is awaiting it and the call deadlocks until its timeout:

```csharp
builder.Services.AddSignalR(options =>
{
    options.MaximumParallelInvocationsPerClient = 16; //required for server-initiated Invoke
}).AddLiteLinkProtocol();
```

The socket transports (TCP, QUIC, pipes) need no configuration: invoke responses bypass the per-connection request limit, so `Invoke<TResult>` works even at the default of one parallel request (see [Per-connection parallelism](#per-connection-parallelism)).

### Deprecation of the old `Invoke`

The old fire-and-forget `link.Invoke(name, model)` is `[Obsolete]` — use `Send`. It still works, but note one behavioral change: legacy calls that passed an explicit type argument, e.g. `Invoke<Model>(name, null)`, now resolve to the result-returning `Invoke<TResult>` and expect a response; rewrite them as `Send<Model>(name, null)`.

Message names are independent of the RPC function table (hashed with CRC32 at call time).

## API description endpoint

With `ShowApi = true` on the `[RpcServer]` attribute, the generator pregenerates a description of every function (name, argument type, return type) and serves it on demand:

```csharp
[RpcServer<UserLink>(ShowApi = true, ApiPassword = "secret")]
```

```csharp
var api = await client.GetApiInfo("secret"); // List<RpcApiFunctionInfo>
```

The password can be rotated at runtime via `RpcServer.ApiInfoPassword`. Requests with a wrong password receive `ErrorCodes.Unauthorized`; servers built without `ShowApi` answer `ErrorCodes.BadRequest`. The description is enough to drive a generic API explorer: it lists every function with its argument and return types, and the type maps from the handshake describe the models, so a UI can build request templates and invoke any function interactively.

## Compression

With `UseCompression = true`, responses larger than 80 bytes are LZ4-compressed (block format, prefixed with the uncompressed size). Clients compress their requests the same way regardless of the server setting. Compression is applied only when it actually shrinks the message, and both clients decompress transparently. The TypeScript client uses `lz4-wasm`, which is byte-compatible with the server's K4os LZ4 block encoding.

Individual functions can override the server-wide setting via the attribute — useful when a response carries data that is already compressed (files, archives, media), where a second LZ4 pass costs CPU for no gain:

```csharp
[RpcFunc("DownloadFile", Compression = RpcCompression.Disabled)]
public static byte[] DownloadFile(UserLink link, int fileId) => LoadCompressedFile(fileId);
```

`RpcCompression.Enabled` works in the opposite direction, compressing one function's responses on a server that otherwise has compression off. No client-side changes are involved either way: every response carries its own compression flag.

Compression is applied by the server at the exit boundary, not inside generated procedure code: the generator registers each opcode's compression policy and `RpcServer` compresses the finished response (or a whole batch envelope) just before it is sent. Two practical consequences: `CallMiddleware` always sees uncompressed response bytes, and batches can be compressed as a single block (see [Request batching](#request-batching)).

## Gateway

`LiteLink.Gateway` provides an intercept-analyze-transfer proxy for the protocol. Its purpose is offloading work from the primary compute service: TLS termination, request screening, or CPU-heavy per-request work such as password hashing can run on a gateway tier while the backend stays on cheap plaintext TCP inside the network.

The gateway needs no generated code and is fully protocol-transparent — the backend's handshake and type maps pass straight through — so one gateway binary can front any LiteLink service.

```csharp
using LiteLink.Gateway;

var gateway = new RpcGateway(new RpcGatewayOptions
{
    BackendHost = "compute1",
    BackendPort = 9090,
    Interceptor = async (session, message) =>
    {
        if (message.EffectiveOpcode == LoginOpcode)
        {
            // analyze: parse the payload, do the expensive hashing here
            var rewritten = message.WithPayload(HashPasswordIn(message.GetPayload()));
            return GatewayVerdict.Forward(rewritten);
        }
        return GatewayVerdict.Forward();
    }
});

// plug it into any socket transport via the Handler seam; TLS terminates here
var edge = new LiteLinkTcpServer(new LiteLinkTcpServerOptions
{
    Port = 443,
    UseTls = true,
    Handler = gateway
});
edge.Start();
```

### Interceptor verdicts

| Verdict | Effect |
| --- | --- |
| `Forward()` | Pass the message to the backend unchanged (also the behavior when no interceptor is set) |
| `Forward(bytes)` | Pass a rewritten message instead |
| `Respond(bytes)` | Answer locally with a complete response; the backend never sees the call |
| `Reject(code)` | Refuse with an error code, e.g. `ErrorCodes.Unauthorized` |
| `Redirect(host, port[, bytes])` | Move the session to another backend and forward this message (or a rewritten one) there — see below |

### Routing sessions to specific backends

A gateway can front several compute servers and decide per session which one a client belongs to — typically when the login reveals the tenant:

```csharp
Interceptor = async (session, message) =>
{
    if (message.EffectiveOpcode == LoginOpcode)
    {
        var company = ReadCompany(message.GetPayload()); // your login model's wire layout
        return GatewayVerdict.Redirect(ComputeHostFor(company), 9090);
    }
    return GatewayVerdict.Forward();
}
```

On a redirect the gateway connects to the target, swaps it in as the session's backend, closes the previous backend connection, and forwards the triggering message to the new server — so the login executes there and the authenticated link state is created on the server the client will keep talking to. Every later message and every backend push (multichannel replies, server-initiated invokes) flows through the new server; the client's own connection is never interrupted. `GatewaySession.RedirectBackendAsync(host, port)` exposes the same operation outside the verdict path, and `session.BackendHost`/`BackendPort` report where a session currently points.

Two rules follow from the mechanics:

- **All targets must serve the same RPC surface.** The client built its serializers from the handshake it received through the default backend; a target with a different build would silently corrupt every later call. The gateway therefore replays the client's handshake against the target before switching and refuses the redirect unless the response is byte-identical (`VerifyHandshakeOnRedirect`, default true). A refused or unreachable redirect fails only the triggering call (`Unhandled`, reported via `OnError`) and leaves the session on its current backend.
- **Redirect on a quiet connection.** Whatever is pending on the previous backend — in-flight multichannel calls, server-initiated invokes, link state — is dropped with that connection. Logging in before issuing parallel traffic satisfies this naturally. A login packed inside a batch envelope cannot trigger a redirect, since the interceptor sees a batch as one opaque system message.

### GatewayMessage

The interceptor receives a parsed view of each raw message that sees through multichannel envelopes:

| Member | Meaning |
| --- | --- |
| `Opcode` / `SystemType` | Outer opcode; system type when 0 (0 handshake, 1 multichannel, 2 API info) |
| `EffectiveOpcode` | The actual call's opcode, unwrapped from multichannel envelopes |
| `IsMultiChannel`, `Channel` | Multichannel envelope information |
| `GetPayload()` | Decompressed payload of the effective message |
| `WithPayload(bytes)` | Rebuild the message with a new payload, preserving the envelope |
| `Raw` | The untouched bytes |

System messages (handshake, API info) flow through the interceptor as well — screening interceptors should let `EffectiveOpcode == 0` pass, or clients cannot complete `Start()`.

### Behavior and topology

- Each client connection gets its own backend connection, so per-connection backend state (links, permissions) works unchanged, and backend pushes — including multichannel replies — are routed back to the correct client.
- `GatewaySession` carries the client connection, a free `Tag` slot for interceptor state, and `InvokeBackendAsync` for gateway-originated backend calls. `ClientConnected` / `ClientDisconnected` events fire per session.
- If a session's current backend connection drops, the gateway terminates the client so it can reconnect; a backend the session left via a redirect can go away without affecting it.
- The gateway fronts the TCP and QUIC transports (the SignalR hub is bound to the in-process server). The 1:1 connection model means a gateway with N clients holds N backend sockets.

## Error codes

Failed calls throw on the client with the code in the exception message.

| Code | Value | Meaning |
| --- | --- | --- |
| `Success` | 0 | Request completed |
| `BadRequest` | 1 | Unknown opcode or malformed request |
| `Unauthorized` | 2 | Permission check failed (or wrong API password) |
| `Uninitialized` | 3 | Connection not registered with the server |
| `ShuttingDown` | 4 | Server is shutting down |
| `Unhandled` | 255 | Unhandled exception inside the procedure |

## The source generator

The generator is a true incremental generator: unrelated edits are served from cache, and only changes to RPC-relevant syntax (attributed members, `Invoke` calls, referenced model types) trigger regeneration. Diagnostics are reported with accurate source locations under stable IDs `RPCGEN001`-`RPCGEN017` (duplicate function names, undeclared permission groups, CRC collisions, unsupported types, and so on).

To attach a debugger to a generator run, set the environment variable `LITELINK_GENERATOR_DEBUG=1` before building. To inspect the generated source, build with:

```
dotnet build -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=<dir outside the project>
```

Note that the output path must be outside the project directory (or excluded from compilation), otherwise the emitted copies are compiled alongside the live generator output.

## Wire protocol reference

All integers are little-endian unless noted. Length prefixes use a variable-width encoding (1/2/4/8 bytes, high bits select the width; 0 encodes null).

Request: `[u16 opcode][u8 isCompressed][payload]`
Response: `[u8 resultCode][u8 isCompressed][payload]`
Push (server to client): `[u8 isCompressed][u32 functionHash][typeIndex][payload]`

Opcode 0 is the system opcode; the byte after the header selects the function: `0` handshake, `1` multichannel, `2` API description, `3` invoke response, `4` batch. A multichannel request wraps a complete inner request: `[u16 0][0][1][u8 channel][inner request]`; its reply arrives as a push with function hash 0: `[u8 0][u32 0][1][u8 channel][inner response]`.

Server-to-client invokes use push type 2: `[u8 0][u32 0][2][u8 requestId][typeIdx resultType][u32 functionHash][u8 hasArg][(typeIdx argType)(arg)]`; the client answers with a system request of type 3: `[u16 0][0][3][u8 requestId][u8 status][result]` (status 0 ok, 254 no responder, 255 responder threw).

A batch packs independent requests into one message: `[u16 0][isCompressed][4][u16 count]` followed by `count` length-prefixed inner requests; the response is `[u8 0][isCompressed][u16 count]` followed by `count` length-prefixed inner responses in request order. Normally the inner messages travel uncompressed and the whole envelope is compressed in one pass (its own `isCompressed` flag, payload from offset 3 for requests / offset 2 for responses); when a batch mixes in a `Compression = Disabled` response, the envelope stays plain and willing inner responses are compressed individually — every message remains self-describing via its flag. Batch items execute sequentially in request order. Batches are top-level only (they cannot nest or appear inside multichannel envelopes) and are limited to `RpcServer.MaxBatchSize` items.

The handshake response carries, in order: the property name table, the type table (with per-property type indices and storage kinds), the function table (opcode, name hash, argument/return type indices) and the permission hash table. Enums appear in the type table as their underlying primitive.

Compressed payloads are `[u32 uncompressedSize][LZ4 block]`.

The TCP and QUIC transports frame messages over their duplex stream as `[i32 frameLength][u8 kind][u32 requestId][payload]` with kinds `0` request, `1` response, `2` push; responses are matched to requests by id, so a slow call never blocks the connection. QUIC uses a single client-opened bidirectional stream per connection.

## Building and testing

```
dotnet build LiteLink.sln
dotnet test LiteLink.sln
```

`.\pack.ps1` builds, tests and packs every NuGet package into `artifacts\packages` (`-Version` overrides the version from `Directory.Build.props`, `-SkipTests`, and `-Push -ApiKey <key>` pushes to nuget.org). Package metadata — version, authors, license, repository URL — is shared from `Directory.Build.props`. `LiteLink.Generator` is not a package of its own: `Directory.Build.targets` bundles its assembly into the `LiteLink` and `LiteLink.Client` packages under `analyzers/dotnet/cs`, which is how a single `PackageReference` brings the generator along.

To release: set `Version` (and, once, `RepositoryUrl` / `PackageProjectUrl`) in `Directory.Build.props` and the matching version in `LiteLink.Web/package.json`, then either run `.\pack.ps1 -Push -ApiKey <key>` (and `npm publish` in `LiteLink.Web`) or push a `v*` tag — the CI workflow (`.github/workflows/ci.yml`) builds and tests the .NET solution on Windows and Linux and the TypeScript package on Node, and on a tag pushes the NuGet packages (`NUGET_API_KEY` secret) and the npm package (`NPM_TOKEN` secret).

`dotnet run --project LiteLink.Benchmarks -c Release` measures the C# client's serialization paths (reflection walk vs compiled plans vs generated serializers) over an in-memory connection.

`LiteLink.Tests` covers the serializer, cross-platform golden wire vectors, CRC vectors, and in-process plus real-socket integration tests (TCP, QUIC, gateway chains). `LiteLink.Generator.Tests` verifies generator incrementality and diagnostics; `LiteLink.ClientGen.Tests` covers the client-side generated serializers. The TypeScript suite (`npm test` in `LiteLink.Web`) asserts byte-identical output against the same golden vectors as the C# suite — if either platform drifts from the wire format, a test fails.

## Known limitations

- One `RpcServer` per process: the generated module initializer creates a singleton, and a second `[RpcServer]` (or manual construction) throws. `Shutdown()`, `ProxyFunction` and `CallMiddleware` are singleton-wide and therefore affect all transports at once.
- Procedures take at most one argument; use a model, tuple or collection for more.
- Server-initiated `Invoke<TResult>` over SignalR requires `MaximumParallelInvocationsPerClient` above 1 on the hub (see Server-to-client messages).
- Browsers can only use the SignalR transport; TCP, QUIC and pipe clients are for native/.NET and server-to-server scenarios.
- The socket transports process one plain request per connection at a time by default (`MaxParallelRequestsPerConnection`); parallel work belongs in multichannel requests, or raise the limit. See [Per-connection parallelism](#per-connection-parallelism).
- A client-side `ResponseTimeout` abandons the request but cannot cancel it on the server; the procedure may still run to completion, and its late response is reported through `OnError` as an orphan.
- The gateway holds one backend connection per client connection, and a `GatewayInterceptor` sees a client batch as one opaque system message rather than the individual calls inside it.
- Changing an enum's underlying type, or renaming properties/functions, is a wire-format change for that member; deploy server and long-lived clients accordingly (clients read sizes and hashes from the handshake at connect time, so a reconnect picks up the new shape).
