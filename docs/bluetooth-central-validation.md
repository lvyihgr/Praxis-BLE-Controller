# Praxis BLE Central Validation

This is the handoff contract for validating the topology selected after the
Windows GATT Server investigation:

```text
Windows PC / Praxis Core       iPad / Praxis Panel
Central + GATT Client    <->   Peripheral + GATT Server
```

The LightBlue test validates role compatibility and a short bidirectional
message. The Bluetooth-only Praxis version now also defines production
fragmentation and automatic foreground reconnection. Authentication and iPad
background behavior remain later acceptance stages. Transport failover is
intentionally out of scope because this version contains Bluetooth only.

## Stable UUIDs and directions

| Purpose | UUID | Business direction | iPad characteristic |
| --- | --- | --- | --- |
| Praxis service | `7a51f000-5e8f-4c7a-9a1d-707261786973` | n/a | Primary service |
| Command to Core | `7a51f001-5e8f-4c7a-9a1d-707261786973` | iPad to PC | Notify |
| Event from Core | `7a51f002-5e8f-4c7a-9a1d-707261786973` | PC to iPad | Write and optionally Write Without Response |
| Peer Info | `7a51f003-5e8f-4c7a-9a1d-707261786973` | iPad to PC | Read, optional |

The names describe Praxis message direction rather than GATT server-relative
RX/TX. This prevents the upper protocol from changing when BLE roles change.

## iPad probe behavior

The iPad test app should use `CBPeripheralManager` and perform these steps while
the app is in the foreground:

1. Publish the primary Praxis service.
2. Add Command to Core with `.notify` and readable-free permissions.
3. Add Event from Core with `.write` (and optionally
   `.writeWithoutResponse`) plus writable permissions.
4. Add Peer Info with `.read` and return UTF-8 JSON containing a stable local
   device identity, version and optional name, for example
   `{"deviceId":"<persistent-id>","version":"1.2.0","name":"iPad"}`.
5. Advertise the Praxis service UUID and local name `Praxis Panel`.
6. Accept a UTF-8 JSON value written to Event from Core.
7. For a `probe.hello`, send this JSON through Command to Core as a
   notification:

```json
{
  "type": "probe.ack",
  "payload": {
    "requestId": "the id from probe.hello"
  }
}
```

If `updateValue` returns `false`, retain the reply and retry it from
`peripheralManagerIsReady(toUpdateSubscribers:)`.

For this first validation, enable the `bluetooth-peripheral` background mode in
the Xcode target but keep the app visible during the round-trip test. Background
and lock-screen behavior are separate acceptance stages.

## PC validation sequence

From the repository root, run the stages separately:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\tools\Praxis.BluetoothCentralProbe -- --capabilities-only
.\.tools\dotnet\dotnet.exe run --project .\tools\Praxis.BluetoothCentralProbe -- --scan-only --scan-timeout 30
.\.tools\dotnet\dotnet.exe run --project .\tools\Praxis.BluetoothCentralProbe -- --connect-only --scan-timeout 30
.\.tools\dotnet\dotnet.exe run --project .\tools\Praxis.BluetoothCentralProbe -- --scan-timeout 30 --reply-timeout 10
```

When LightBlue is used instead of the iPad probe app, make the acknowledgement
manual and predictable:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\tools\Praxis.BluetoothCentralProbe -- --scan-timeout 30 --reply-timeout 120 --request-id lightblue-test
```

Then notify this ASCII value from Command to Core in LightBlue:

```json
{"type":"probe.ack","payload":{"requestId":"lightblue-test"}}
```

Expected progression:

1. Windows reports Central role support.
2. The PC discovers the iPad advertisement.
3. The PC connects and discovers both required characteristics.
4. The PC subscribes, writes `probe.hello`, and receives the matching
   `probe.ack` notification.

## Acceptance stages after the first round trip

Once the foreground round trip passes, test these independently so failures are
attributed correctly:

1. Disconnect and automatic reconnect while both apps remain in the foreground.
2. Toggle Bluetooth off and on at each endpoint.
3. Restart Praxis Core while the iPad keeps advertising.
4. Lock and unlock the iPad.
5. Put Praxis Panel in the background and return it to the foreground.
6. Send messages larger than the negotiated ATT payload after adding the Praxis
   Link framing and reassembly layer.

Record the Windows build, Bluetooth adapter and driver version, iPad model, and
iOS version for every completed compatibility run.

## Completed compatibility run

### 2026-08-07: Windows Central to LightBlue on iPad mini 2

- Windows: Windows 11 25H2, build 26200.8875
- Bluetooth adapter: Intel Wi-Fi 6E AX211
- Bluetooth device driver: 24.40.11.1
- iPad: iPad mini 2
- iOS: 12.5.8
- Peripheral implementation: LightBlue virtual peripheral, foreground
- Advertisement: connectable undirected, random address
- Signal during the run: approximately -51 dBm

Results:

- Advertisement discovery: pass
- `BluetoothLEDevice` creation: pass from a normal desktop process
- GATT service discovery: pass
- Command-to-Core notification subscription: pass
- Event-from-Core write with response: pass
- Matching `probe.ack` notification: pass

The same connection attempt returned `0x80004005` when the probe was launched
inside the Codex restricted execution sandbox. Running the identical binary as
a normal desktop process succeeded. Praxis Core runs as a normal desktop
process, so the sandbox-only failure is not considered a BLE compatibility
failure.

## Production Praxis Link framing

LightBlue sends and receives raw JSON only for the compatibility probe. The
production iPad app must frame every UTF-8 JSON protocol message before sending
it through BLE, and must reassemble frames written by the PC before decoding
JSON.

Both characteristics use the same 12-byte header. Multi-byte integers are
little-endian.

| Offset | Size | Field | Value |
| --- | ---: | --- | --- |
| 0 | 2 | Magic | ASCII `PX` |
| 2 | 1 | Frame version | `1` |
| 3 | 1 | Flags | bit 0 = start, bit 1 = end |
| 4 | 2 | Message ID | Unsigned 16-bit ID, wraps naturally |
| 6 | 2 | Sequence | Starts at `0` and increases by one |
| 8 | 4 | Total message bytes | UTF-8 JSON length before fragmentation |
| 12 | variable | Payload | One ordered slice of the JSON bytes |

Rules:

1. A one-frame message has both start and end flags set.
2. Every frame belonging to a message repeats its message ID and total length.
3. Only one message is assembled at a time in each direction in version 1.
4. A missing, duplicated, or out-of-order frame invalidates the current
   message; discard it and wait for the next start frame.
5. A complete message may not exceed 65,536 bytes.
6. The PC derives its maximum frame size from the negotiated GATT session MTU.
   The iPad should honor Core Bluetooth's maximum update/write length and split
   the same application message accordingly.

Production directions remain:

- iPad to PC: notify one framed slice at a time on Command to Core (`f001`).
- PC to iPad: write one framed slice at a time to Event from Core (`f002`).

After reassembly, the payload is exactly the transport-neutral Praxis Link JSON
used by the existing action, state, configuration, and presentation features.
