# EasyClient

Supports `tcp://IP:port`, `udp://localIP:port/?RemoteEndPoint=remoteIP:port`, and `sio://COM2/?BaudRate=9600`.

Query names are case insensitive and values are URI decoded. Empty separators are accepted; malformed entries, duplicate names and invalid option values throw `ArgumentException`. `ReceiceBuffer` (existing spelling) must be positive. UDP requires a remote endpoint.

Serial defaults: 8 data bits, no parity, one stop bit, RTS/DTR disabled, infinite write timeout. Supported options: `BaudRate`, `DataBits`, `Parity`, `StopBits`, `RtsEnable`, `DtrEnable`, `WriteTimeout`. Baud rate is required. Write timeout accepts positive milliseconds or `-1`.

## Request and response behavior

- Requests are serialized per channel. A positive expected length accumulates receive fragments until that length is reached; excess data goes to the receive callback.
- With expected length `0`, the first received chunk completes the response. TCP chunks are not protocol frames; specify a length when the protocol allows it.
- The timeout covers queueing, sending and waiting for the response and throws `TimeoutException`. Cancellation-token overloads allow explicit cancellation (`OperationCanceledException`). `-1` disables the deadline.
- Send/open failures propagate to the caller. Closing or losing a connection fails a pending response rather than returning an empty or partial response.
- This is a behavior change from earlier versions that silently swallowed failures or returned empty/partial responses. Callers should handle timeout, I/O, socket and disposed-object exceptions.
- TCP reconnects in the background; sending before connection establishment fails explicitly.
- Unsolicited callbacks run serially in receive order on a thread-pool worker. The queue holds at most 256 chunks; overflow records LastError and fails the connection instead of silently continuing a corrupted stream. TCP reconnects; serial/UDP require a reset. Closing discards queued callbacks; a callback already executing may finish. Callback exceptions are logged. Set `ChannelBase.Logger` to attach an `ILogger`.
- Protocol correlation is the caller's responsibility: without request identifiers, a late response after a timeout cannot be distinguished from a later response.

## Tests

Run `dotnet test IFactory.Interaction.EasyClient.slnx` with the .NET 10 SDK. Tests cover request timing, fragmented responses, data ownership, timeout cleanup, TCP reconnection, UDP round trips, URI validation and serial option parsing. Serial hardware integration requires a real or virtual serial port.

## Scheduling and lifecycle

Plain sends and request/response operations share one operation gate, preventing a plain send from entering an active request/response transaction. Responses to earlier fire-and-forget sends still require protocol-level correlation.

Reset and close are serialized. Close permanently closes an EasyClient instance; create a new client to reopen it. A reset racing close cannot leave a replacement connection running. Reset no longer imposes a fixed delay. Operations overlapping reset can fail when their old channel is closed; retry only when safe for the application protocol.

ReadOnlyMemory<byte> overloads avoid copying caller-owned outgoing data. Keep that memory unchanged until the operation finishes. Response fragments append directly to their owned list without temporary arrays or a final list copy. Custom ChannelBase subclasses now implement SendCoreAsync(ReadOnlyMemory<byte>, CancellationToken) and must honor cancellation instead of overriding public Send/SendAsync.

UDP ReceiceBuffer specifies the accepted datagram size; transport reception uses space for a full UDP datagram and drops oversized messages with a warning while continuing reception. Serial receive failures dispose the port and mark it closed. Serial driver cancellation and shutdown still require hardware validation.

## VSPD serial integration tests (Windows)

Create a connected COM1/COM2 pair in VSPD and keep both ports free of serial terminal programs. The integration tests are opt-in and run serially. They cover 16KB binary transfer in both directions, 20 fragmented request/response exchanges, timeout, cancellation, closing a pending request, and reopening ports.

```powershell
$env:EASYCLIENT_SERIAL_TESTS = '1'
dotnet test IFactory.Interaction.EasyClient.slnx --filter Category=SerialIntegration
```

Optional port overrides: `EASYCLIENT_SERIAL_A` and `EASYCLIENT_SERIAL_B`. Unset `EASYCLIENT_SERIAL_TESTS` to skip hardware tests during normal CI. The tests use 115200 baud, 8N1, and no handshake. The serial receive buffer is 64KB to accommodate virtual-port bursts. The test cleanup allows 100ms for VSPD 6.9 device release before reuse; this delay is test-environment handling, not a guarantee of immediate driver-level reopen.

## Failure boundaries

- `ChannelBase.LastError` retains the latest receive/connection failure, including callback queue overflow. TCP disconnect discards queued callbacks from that connection; an already executing callback can finish. Callbacks must not block indefinitely.
- `EasyClient.LastResetError` exposes reset failures. Built-in configurations are validated before closing the old connection. A runtime open failure leaves that old channel closed; retry reset after correcting the cause. Serial resets retry temporary I/O/access failures up to five times with 100ms intervals.
- Empty UDP datagrams are delivered to unsolicited callbacks. UDP datagram boundaries remain separate from generic expected-length response accumulation.
- Serial async writes apply WriteTimeout explicitly. Write cancellation aborts and closes the native port to prevent a partially transmitted command from sharing a later transaction. Reset before reuse. Driver-level shutdown still depends on the installed driver.
- A callback queue capacity bounds chunks, not bytes; choose receive sizes and response lengths appropriate to trusted protocol limits. Slow callback consumers should hand work to an application-owned queue with an explicit policy.
- Request identifiers, late-response correlation, unsolicited-message recognition and variable-length framing are protocol responsibilities; the transport cannot infer them from raw bytes. Application cancellation cannot retract bytes already transmitted.
