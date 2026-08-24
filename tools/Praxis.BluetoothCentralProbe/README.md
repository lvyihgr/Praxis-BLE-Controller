# Praxis Bluetooth Central Probe

This diagnostic verifies the BLE topology where the Windows PC is the Central
and the iPad app is the Peripheral/GATT Server. It does not execute Praxis
actions or alter Praxis pairing data.

## Contract

- Service: `7a51f000-5e8f-4c7a-9a1d-707261786973`
- Command to Core: `7a51f001-5e8f-4c7a-9a1d-707261786973`
  - Direction: iPad to Praxis Core
  - iPad characteristic: Notify
- Event from Core: `7a51f002-5e8f-4c7a-9a1d-707261786973`
  - Direction: Praxis Core to iPad
  - iPad characteristic: Write or Write Without Response
- Peer Info: `7a51f003-5e8f-4c7a-9a1d-707261786973`
  - Optional iPad characteristic: Read

The UUID semantics are based on message direction, not on which device owns
the GATT server. This keeps the upper Praxis Link protocol stable if BLE roles
change.

## Run

Check only the Windows Central capability:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\tools\Praxis.BluetoothCentralProbe -- --capabilities-only
```

Discover an iPad advertising the Praxis service:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\tools\Praxis.BluetoothCentralProbe -- --scan-only --scan-timeout 30
```

Discover, connect, and validate the required characteristics:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\tools\Praxis.BluetoothCentralProbe -- --connect-only --scan-timeout 30
```

Run a full round trip:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\tools\Praxis.BluetoothCentralProbe -- --scan-timeout 30 --reply-timeout 10
```

The probe subscribes to Command to Core, writes a `probe.hello` JSON message to
Event from Core, and expects the iPad to notify a `probe.ack` message whose
`payload.requestId` matches the request ID. Add `--name <text>` to require the
advertised local name to contain a specific value. The probe messages are kept
below a single common BLE ATT payload; production Praxis Link fragmentation is
deliberately outside this first role-validation test.

Every mode prints `PROBE_RESULT=PASS` or `PROBE_RESULT=FAIL` and a specific exit
code. A scan timeout without the iPad test app advertising is an expected test
failure, not a Windows Central capability failure.

For a manual LightBlue round trip, use a fixed request ID and a longer reply
window:

```powershell
.\.tools\dotnet\dotnet.exe run --project .\tools\Praxis.BluetoothCentralProbe -- --scan-timeout 30 --reply-timeout 120 --request-id lightblue-test
```

After LightBlue receives the write on Event from Core, send this UTF-8/ASCII
notification from Command to Core:

```json
{"type":"probe.ack","payload":{"requestId":"lightblue-test"}}
```
