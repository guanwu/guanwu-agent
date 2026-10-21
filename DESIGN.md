# Guanwu Agent — Design

Version 1 (`ga.v1`). The wire is defined in `PROTOCOL.md`.

## 1. What this is

A local capability agent. A page or a local process opens one WebSocket to
`127.0.0.1:2699` and gains three capability groups: run executables, read and
write local files, and make HTTP requests with a per-connection cookie jar.

Two artifacts ship together and are version-matched:

1. **Backend** — C# / .NET 10 / Native AOT / self-contained single file.
   On Windows it is a WinExe (GUI subsystem, no console window) and registers
   the `ga://` protocol for the current user at startup.
2. **SDK** — TypeScript / esbuild / IIFE and ESM bundles, shipped
   unminified and minified with external source maps and type declarations.
   Pages load the minified IIFE bundle with `<script>`.

## 2. Model

One connection carries one job at a time and owns one HTTP client, one cookie
container, and the current job's temp directory. Concurrency is a second
connection.

A job: create an input sink, receive input, execute, produce output, send
output, terminal message.

Input and output are files:

- `fs.write` writes to the commit temp file in the target directory.
- `fs.read` streams the source file.
- `http.request` stages the request body and the final response body in the
  temp directory.
- `exec.run` stages stdin, stdout, and stderr in the temp directory.
- `fs.list` generates JSON Lines while it walks.

## 3. Execution

Two pump tasks copy the child's stdout and stderr to files. The agent waits
for process exit and for both pipes to reach EOF, then streams `stdout` and
`stderr` from their files and sends `result`.

Cancellation and timeout kill the process tree, delete temp files, and send
`error`.

## 4. Write commit

`fs.write` is a whole-file replacement:

1. Create `<target-dir>/.gw-<random>.part` with `CreateNew`.
2. Write input bytes as they arrive.
3. Flush, close, and rename over the target.

Delete the temp file on error or cancellation. If the target is a symbolic
link, the link itself is replaced; the link target is not written.

A crash may leave a `.part` file next to the target.

## 5. HTTP

The BCL `HttpClient` performs the request. Cookies live in a per-connection
container; `http.cookies.set` replaces the container.

Redirects are followed automatically, at most 50 hops. The final response is
returned; intermediate responses are discarded. HTTPS to HTTP redirects and
redirects to unsupported schemes are not followed. `Authorization` and
`ProxyAuthorization` are cleared on every redirect. Decompression is on. The
system proxy is used. Outgoing HTTPS accepts any certificate.

A 3xx that is not followed and a 4xx/5xx are results. Status and headers are
in `result`, after the body.

## 6. SDK

An operation takes two objects: parameters and IO. IO carries `input` and one
callback per output stream:

- `onChunk` for a single byte stream;
- `onEntry` for one parsed entry per JSON line;
- `onStdout` / `onStderr` for multiple streams.

The SDK reads while it sends. On a terminal message it stops the upload. It
serialises requests on the connection.

## 7. Layout

```
src/GuanwuAgent/            Backend
  Program.cs                Entry: arguments, bind, register, serve
  AgentIdentity.cs          Name, version, platform, AgentHome
  Hosting/                  SchemeRegistration, Log, NativeMethods
  Server/                   AgentServer, WebSocketUpgrade, Session, Job
  Protocol/                 Op, ErrorCode, Limits, ClientMessage, Wire
  Capabilities/             CommandRunner, FileSystem, HttpSession, CookieCodec,
                            PathResolver
src/GuanwuAgentSdk/         SDK
  protocol.ts               Message types, stream names
  connection.ts             State, pairing, abort, close
  client.ts                 AgentClient: one connection, exec/files/http
  files.ts http.ts exec.ts errors.ts index.ts
tests/smoke.mjs             The only automated test
docs/index.html             Manual test page
build.mjs                   SDK build
build.cmd                   Windows AOT publish and test
build.sh                    Linux/macOS AOT publish and test
.github/workflows/build.yml Multi-platform build
```

## 8. Resource ownership

| Resource | Creator | Destroyer |
|---|---|---|
| WebSocket | upgrade | close |
| HTTP client + cookie container | session | session close |
| Current job | `request` | terminal message |
| Child process tree | `exec.run` | exit, cancel, timeout, session close |
| Job temp directory | `request` | terminal, session close, startup sweep |
| `fs.write` temp file | `fs.write` | rename, error, session close |

## 9. Limits

`PROTOCOL.md` §7.

## 10. Build and verification

1. `build.cmd` or `build.sh` publishes for the target RID into `release/<RID>/`.
2. `npm run typecheck` and `npm run build` succeed.
3. The smoke test passes against the published binary.
4. Touched behaviour is exercised by hand through `docs/index.html`.
