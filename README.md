# EasyClient

Supports `tcp://IP:port`, `udp://localIP:port/?RemoteEndPoint=remoteIP:port`, and `sio://COM2/?BaudRate=9600`.

Query names are case insensitive and values are URI decoded. Empty separators are accepted; malformed entries, duplicate names and invalid option values throw `ArgumentException`. `ReceiceBuffer` (existing spelling) must be positive. UDP requires a remote endpoint.

Serial defaults: 8 data bits, no parity, one stop bit, RTS/DTR disabled, infinite write timeout. Supported options: `BaudRate`, `DataBits`, `Parity`, `StopBits`, `RtsEnable`, `DtrEnable`, `WriteTimeout`. Baud rate is required. Write timeout accepts positive milliseconds or `-1`.

## Request and response behavior

- Requests are serialized per channel. A positive expected length accumulates receive fragments until that length is reached; excess data goes to the receive callback.
- With expected length `0`, the first received chunk completes the response. TCP chunks are not protocol frames; specify a length when the protocol allows it.
- Response timeout starts after sending and throws `TimeoutException`, including when only part of the response arrived. It does not include time waiting for another request to finish. `-1` waits indefinitely.
- Send/open failures propagate to the caller. Closing or losing a connection fails a pending response rather than returning an empty or partial response.
- This is a behavior change from earlier versions that silently swallowed failures or returned empty/partial responses. Callers should handle timeout, I/O, socket and disposed-object exceptions.
- TCP reconnects in the background; sending before connection establishment fails explicitly.
- Unsolicited callbacks run on the thread pool and must not assume ordering. Callback exceptions are logged. Set `ChannelBase.Logger` to attach an `ILogger`.
- Protocol correlation is the caller's responsibility: without request identifiers, a late response after a timeout cannot be distinguished from a later response.

## Tests

Run `dotnet test IFactory.Interaction.EasyClient.slnx` with the .NET 10 SDK. Tests cover request timing, fragmented responses, data ownership, timeout cleanup, TCP reconnection, UDP round trips, URI validation and serial option parsing. Serial hardware integration requires a real or virtual serial port.
