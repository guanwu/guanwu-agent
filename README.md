# Guanwu Agent

A local capability agent. A page or a local process opens one WebSocket to
`ws://127.0.0.1:2699/` with the `ga.v1` subprotocol and gains three capability
groups:

- run executables on the machine;
- list, read, write, stat, and delete local files;
- make HTTP requests with a per-connection cookie jar.

The backend is C# / .NET 10 / Native AOT, self-contained, and loopback-only. On
Windows it is a GUI-subsystem executable and registers `ga://` for the current
user. The SDK is TypeScript, built by esbuild as IIFE and ESM bundles with type
declarations.

## Build and run

Requirements: .NET 10 SDK, Node.js 22, and a Native AOT C++ toolchain.

```
build.cmd          # Windows
./build.sh         # Linux and macOS
```

Both scripts accept an optional RID and `--skip-tests`. The output is
`release/<RID>/`, containing the agent binary, the test console under
`docs/`, and the SDK under `docs/dist/`.

Start the agent:

```
release/win-x64/guanwu-agent.exe
```

## SDK

```html
<script src="docs/dist/guanwu-agent.iife.min.js"></script>
<script>
  const agent = await GuanwuAgent.connect();
  const stat = await agent.fs.stat({ path: "readme.txt" });
  agent.close();
</script>
```

## Documentation

- [`PROTOCOL.md`](PROTOCOL.md) — wire protocol, operations, errors, limits
- [`DESIGN.md`](DESIGN.md) — components, execution model, resource ownership
- `docs/index.html` — browser test console

## Security

Any process or web page that can reach `127.0.0.1:2699` has the user's full
capabilities on the machine. Outgoing HTTPS accepts any certificate.

## License

GNU Affero General Public License v3.0 (AGPL-3.0-only).
Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>.

See [`LICENSE`](LICENSE) for the full terms.
