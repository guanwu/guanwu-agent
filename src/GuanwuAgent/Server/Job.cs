// Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers;
using System.Net;
using System.Text.Json;
using GuanwuAgent.Capabilities;
using GuanwuAgent.Hosting;
using GuanwuAgent.Protocol;

namespace GuanwuAgent.Server;

// Maps plan validation failures to the request.invalid error code.
public sealed class RequestInvalidException : Exception
{
  public RequestInvalidException(string message) : base(message)
  {
  }
}

// Runs one request: validates its plan, stages input on disk, executes the
// capability, streams output, then sends exactly one result or error and
// removes every temporary file it created.
public sealed class Job
{
  private const int BufferSize = 64 * 1024;

  private readonly Session _session;
  private readonly string _tempDir;
  private readonly Plan _plan;
  private readonly string _partPath;
  private readonly CancellationTokenSource _cts = new();

  // Execution waits on this task, so every input path—the last chunk or the
  // first write failure—must complete it or the job would hang.
  private readonly TaskCompletionSource _inputDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private FileStream? _input;
  private Exception? _inputError;
  private long _inputSize;
  private int _acceptingInput = 1;
  private int _terminalSent;
  private int _aborted;
  private int _committed;
  private Task _completion = Task.CompletedTask;

  private Job(Session session, string tempDir, Plan plan, FileStream? input, string partPath)
  {
    _session = session;
    _tempDir = tempDir;
    _plan = plan;
    _input = input;
    _partPath = partPath;
  }

  public bool AcceptsInput => Volatile.Read(ref _acceptingInput) == 1;

  public bool TerminalSent => Volatile.Read(ref _terminalSent) != 0;

  public Task Completion => _completion;

  public static Job Create(Session session, Op op, JsonElement parameters)
  {
    var plan = ParsePlan(op, parameters, session.Server.Resolver);
    var tempDir = session.Server.CreateJobDirectory();
    FileStream? input = null;
    var partPath = string.Empty;

    try
    {
      if (op == Op.FsWrite && plan is FsWritePlan write)
      {
        input = FileSystem.CreatePartFile(write.TargetPath, out partPath);
      }
      else if (op is Op.ExecRun or Op.HttpRequest)
      {
        var name = op == Op.ExecRun ? "stdin.bin" : "request.bin";
        input = FileSystem.CreateFile(Path.Combine(tempDir, name));
      }

      return new Job(session, tempDir, plan, input, partPath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      input?.Dispose();
      if (partPath.Length > 0)
      {
        FileSystem.TryDeleteFile(partPath);
      }

      TryDeleteDirectory(tempDir);
      throw new GuanwuException(ex.Message);
    }
  }

  public void Start() => _completion = RunAsync();

  public void Cancel() => _cts.Cancel();

  public void Abort()
  {
    Volatile.Write(ref _aborted, 1);
    _cts.Cancel();
  }

  public async ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
  {
    if (!AcceptsInput || _input is null)
    {
      return;
    }

    try
    {
      await _input.WriteAsync(data, cancellationToken);
      _inputSize += data.Length;
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception ex)
    {
      FailInput(ex);
    }
  }

  public void EndInput()
  {
    if (Interlocked.Exchange(ref _acceptingInput, 0) == 0)
    {
      return;
    }

    CloseInput();
    _inputDone.TrySetResult();
  }

  private void FailInput(Exception error)
  {
    Interlocked.CompareExchange(ref _inputError, error, null);
    Interlocked.Exchange(ref _acceptingInput, 0);
    CloseInput();
    _inputDone.TrySetResult();
  }

  private void CloseInput()
  {
    var input = Interlocked.Exchange(ref _input, null);
    if (input is null)
    {
      return;
    }

    try
    {
      input.Flush();
      input.Dispose();
    }
    catch (Exception ex)
    {
      Interlocked.CompareExchange(ref _inputError, ex, null);
    }
  }

  private async Task RunAsync()
  {
    try
    {
      try
      {
        await _inputDone.Task.WaitAsync(_cts.Token);

        if (_inputError is not null)
        {
          throw _inputError;
        }

        var result = await ExecuteAsync();
        await SendResultAsync(result);
      }
      catch (OperationCanceledException) when (_cts.IsCancellationRequested)
      {
        await SendErrorAsync(ErrorCodes.JobCancelled, "job cancelled");
      }
      catch (OperationTimeoutException ex)
      {
        await SendErrorAsync(ErrorCodes.JobTimeout, ex.Message);
      }
      catch (GuanwuException ex)
      {
        await SendErrorAsync(ErrorCodes.JobFailed, ex.Message);
      }
      catch (IOException ex)
      {
        await SendErrorAsync(ErrorCodes.JobFailed, ex.Message);
      }
      catch (UnauthorizedAccessException ex)
      {
        await SendErrorAsync(ErrorCodes.JobFailed, ex.Message);
      }
      catch (Exception ex)
      {
        Log.Error($"job failed unexpectedly: {ex.Message}");
        await SendErrorAsync(ErrorCodes.AgentInternal, "internal error");
      }
    }
    finally
    {
      _session.FinishJob(this);
      Cleanup();
    }
  }

  private Task<byte[]> ExecuteAsync() => _plan switch
  {
    ExecPlan plan => RunExecAsync(plan),
    FsListPlan plan => RunListAsync(plan),
    FsReadPlan plan => RunReadAsync(plan),
    FsWritePlan plan => RunWriteAsync(plan),
    FsDeletePlan plan => RunDeleteAsync(plan),
    FsStatPlan plan => RunStatAsync(plan),
    HttpPlan plan => RunHttpAsync(plan),
    CookiesGetPlan => Task.FromResult(Wire.BuildResult(writer =>
    {
      writer.WritePropertyName("cookies");
      writer.WriteStartArray();
      foreach (var cookie in _session.Http.GetCookies())
      {
        WriteCookie(writer, cookie);
      }

      writer.WriteEndArray();
    })),
    CookiesSetPlan plan => RunCookiesSetAsync(plan),
    _ => throw new InvalidOperationException("unknown job plan"),
  };

  private async Task<byte[]> RunExecAsync(ExecPlan plan)
  {
    var outcome = await _session.Server.Commands.RunAsync(
      plan.Spec,
      Path.Combine(_tempDir, "stdin.bin"),
      Path.Combine(_tempDir, "stdout.bin"),
      Path.Combine(_tempDir, "stderr.bin"),
      _cts.Token);

    await SendFileAsync(Path.Combine(_tempDir, "stdout.bin"), "stdout", _cts.Token);
    await SendFileAsync(Path.Combine(_tempDir, "stderr.bin"), "stderr", _cts.Token);

    return Wire.BuildResult(writer =>
    {
      writer.WriteNumber("exitCode", outcome.ExitCode);
      writer.WriteNumber("durationMs", outcome.DurationMs);
    });
  }

  // Reads one buffer ahead so a chunk can be marked last before it is sent;
  // an empty file opens no content stream at all.
  private async Task<byte[]> RunReadAsync(FsReadPlan plan)
  {
    await using var stream = FileSystem.OpenRead(plan.Path);
    var primary = new byte[BufferSize];
    var secondary = new byte[BufferSize];
    var current = await stream.ReadAsync(primary, _cts.Token);
    long size = 0;

    while (current > 0)
    {
      var next = await stream.ReadAsync(secondary, _cts.Token);
      size += current;
      await _session.SendChunkAsync("content", next == 0, primary.AsMemory(0, current), _cts.Token);
      (primary, secondary) = (secondary, primary);
      current = next;
    }

    return Wire.BuildResult(writer => writer.WriteNumber("size", size));
  }

  // Entries are emitted only when there is at least one, so an empty
  // directory has no entries stream at all.
  private async Task<byte[]> RunListAsync(FsListPlan plan)
  {
    var buffer = new ArrayBufferWriter<byte>(BufferSize);
    var count = 0;

    foreach (var entry in FileSystem.List(plan.Path, plan.Recursive))
    {
      var line = Wire.BuildJsonLine(writer =>
      {
        writer.WriteString("name", entry.Name);
        writer.WriteString("path", entry.Path);
        writer.WriteString("type", TypeName(entry.Type));
        if (entry.Size is long size)
        {
          writer.WriteNumber("size", size);
        }

        writer.WriteNumber("mtimeMs", entry.MtimeMs);
      });

      line.CopyTo(buffer.GetSpan(line.Length));
      buffer.Advance(line.Length);
      count++;

      if (buffer.WrittenCount >= BufferSize)
      {
        await _session.SendChunkAsync("entries", false, buffer.WrittenMemory, _cts.Token);
        buffer.Clear();
      }
    }

    if (count > 0)
    {
      await _session.SendChunkAsync("entries", true, buffer.WrittenMemory, _cts.Token);
    }

    return Wire.BuildResult(writer => writer.WriteNumber("count", count));
  }

  private Task<byte[]> RunWriteAsync(FsWritePlan plan)
  {
    _cts.Token.ThrowIfCancellationRequested();
    FileSystem.Commit(_partPath, plan.TargetPath);
    Volatile.Write(ref _committed, 1);

    return Task.FromResult(Wire.BuildResult(writer =>
    {
      writer.WriteString("path", plan.TargetPath);
      writer.WriteNumber("size", _inputSize);
    }));
  }

  private Task<byte[]> RunStatAsync(FsStatPlan plan)
  {
    var stat = FileSystem.Stat(plan.Path);
    return Task.FromResult(Wire.BuildResult(writer =>
    {
      writer.WriteString("type", StatTypeName(stat.Type));
      writer.WriteNumber("size", stat.Size);
      writer.WriteNumber("mtimeMs", stat.MtimeMs);
    }));
  }

  private Task<byte[]> RunDeleteAsync(FsDeletePlan plan)
  {
    FileSystem.Delete(plan.Path, plan.Recursive);
    return Task.FromResult(Wire.BuildResult(_ => { }));
  }

  private async Task<byte[]> RunHttpAsync(HttpPlan plan)
  {
    var result = await _session.Http.SendAsync(
      plan.Spec,
      Path.Combine(_tempDir, "request.bin"),
      Path.Combine(_tempDir, "response.bin"),
      _cts.Token);

    await SendFileAsync(Path.Combine(_tempDir, "response.bin"), "body", _cts.Token);

    return Wire.BuildResult(writer =>
    {
      writer.WriteNumber("status", result.Status);
      writer.WritePropertyName("headers");
      writer.WriteStartObject();
      foreach (var (name, values) in result.Headers)
      {
        writer.WritePropertyName(name);
        if (values.Length == 1)
        {
          writer.WriteStringValue(values[0]);
        }
        else
        {
          writer.WriteStartArray();
          foreach (var value in values)
          {
            writer.WriteStringValue(value);
          }

          writer.WriteEndArray();
        }
      }

      writer.WriteEndObject();
      writer.WriteNumber("size", result.Size);
      writer.WriteNumber("durationMs", result.DurationMs);
    });
  }

  private Task<byte[]> RunCookiesSetAsync(CookiesSetPlan plan)
  {
    _session.Http.SetCookies(plan.Cookies);
    return Task.FromResult(Wire.BuildResult(_ => { }));
  }

  // An empty file sends no stream. A short read means the file changed under
  // us, which is reported instead of silently truncating the output.
  private async Task SendFileAsync(string path, string streamName, CancellationToken cancellationToken)
  {
    await using var stream = FileSystem.OpenRead(path);
    var length = stream.Length;
    if (length == 0)
    {
      return;
    }

    var buffer = new byte[BufferSize];
    long remaining = length;
    while (remaining > 0)
    {
      var read = await stream.ReadAsync(
        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
        cancellationToken);

      if (read == 0)
      {
        throw new GuanwuException($"{streamName}: output ended early");
      }

      remaining -= read;
      await _session.SendChunkAsync(
        streamName,
        remaining == 0,
        buffer.AsMemory(0, read),
        cancellationToken);
    }
  }

  // Interlocked makes the terminal message single-shot; an aborted connection
  // is not worth another send attempt.
  private async Task SendResultAsync(byte[] result)
  {
    if (Interlocked.Exchange(ref _terminalSent, 1) != 0 || Volatile.Read(ref _aborted) != 0)
    {
      return;
    }

    try
    {
      await _session.SendResultAsync(result);
    }
    catch (Exception)
    {
    }
  }

  // Shares the terminal flag with SendResultAsync, so exactly one terminal
  // message leaves the job.
  private async Task SendErrorAsync(string code, string message)
  {
    if (Interlocked.Exchange(ref _terminalSent, 1) != 0 || Volatile.Read(ref _aborted) != 0)
    {
      return;
    }

    try
    {
      await _session.SendErrorAsync(code, message);
    }
    catch (Exception)
    {
    }
  }

  // A committed part file is gone after the rename; anything still uncommitted
  // must be removed so a failed write leaves no partial target.
  private void Cleanup()
  {
    CloseInput();

    if (Volatile.Read(ref _committed) == 0 && _partPath.Length > 0)
    {
      FileSystem.TryDeleteFile(_partPath);
    }

    TryDeleteDirectory(_tempDir);
  }

  private static void TryDeleteDirectory(string path)
  {
    try
    {
      Directory.Delete(path, recursive: true);
    }
    catch (Exception)
    {
    }
  }

  private static void WriteCookie(Utf8JsonWriter writer, CookieRecord cookie)
  {
    writer.WriteStartObject();
    writer.WriteString("name", cookie.Name);
    writer.WriteString("value", cookie.Value);
    if (cookie.Domain is not null)
    {
      writer.WriteString("domain", cookie.Domain);
    }

    writer.WriteString("path", cookie.Path);
    writer.WriteBoolean("secure", cookie.Secure);
    writer.WriteBoolean("httpOnly", cookie.HttpOnly);
    if (cookie.ExpiresMs is long expiresMs)
    {
      writer.WriteNumber("expiresMs", expiresMs);
    }

    writer.WriteEndObject();
  }

  private static string TypeName(FsEntryType type) => type switch
  {
    FsEntryType.File => "file",
    FsEntryType.Dir => "dir",
    FsEntryType.Link => "link",
    _ => "other",
  };

  private static string StatTypeName(FsEntryType type) => type switch
  {
    FsEntryType.Dir => "dir",
    FsEntryType.File => "file",
    _ => "other",
  };

  private static Plan ParsePlan(Op op, JsonElement parameters, PathResolver paths) => op switch
  {
    Op.ExecRun => ParseExec(parameters, paths),
    Op.FsList => ParseList(parameters, paths),
    Op.FsRead => new FsReadPlan(paths.Resolve(RequireString(parameters, "path"))),
    Op.FsWrite => ParseWrite(parameters, paths),
    Op.FsDelete => ParseDelete(parameters, paths),
    Op.FsStat => new FsStatPlan(paths.Resolve(RequireString(parameters, "path"))),
    Op.HttpRequest => ParseHttp(parameters),
    Op.HttpCookiesGet => new CookiesGetPlan(),
    Op.HttpCookiesSet => ParseCookiesSet(parameters),
    _ => throw new RequestInvalidException("unknown operation"),
  };

  private static ExecPlan ParseExec(JsonElement parameters, PathResolver paths)
  {
    var file = paths.ResolveExecutable(RequireString(parameters, "file"));
    var args = OptionalStrings(parameters, "args");
    var cwd = paths.ResolveWorkingDirectory(OptionalString(parameters, "cwd"));
    if (!Directory.Exists(cwd))
    {
      throw new GuanwuException($"{cwd}: working directory not found");
    }

    var env = OptionalStringMap(parameters, "env");
    var timeoutMs = OptionalNonNegativeInt(parameters, "timeoutMs", 0);

    return new ExecPlan(new CommandSpec(file, args, cwd, env, timeoutMs));
  }

  private static FsListPlan ParseList(JsonElement parameters, PathResolver paths)
  {
    var path = paths.Resolve(RequireString(parameters, "path"));
    var recursive = OptionalBool(parameters, "recursive", false);
    return new FsListPlan(path, recursive);
  }

  // A directory cannot be replaced by the part-file rename, so it is rejected
  // before input starts; a directory link is a valid replace target.
  private static FsWritePlan ParseWrite(JsonElement parameters, PathResolver paths)
  {
    var target = paths.Resolve(RequireString(parameters, "path"));
    try
    {
      var attributes = File.GetAttributes(target);
      if ((attributes & FileAttributes.Directory) != 0 &&
          (attributes & FileAttributes.ReparsePoint) == 0)
      {
        throw new GuanwuException($"{target}: is a directory");
      }
    }
    catch (FileNotFoundException)
    {
    }
    catch (DirectoryNotFoundException)
    {
    }

    return new FsWritePlan(target);
  }

  private static FsDeletePlan ParseDelete(JsonElement parameters, PathResolver paths)
  {
    var path = paths.Resolve(RequireString(parameters, "path"));
    var recursive = OptionalBool(parameters, "recursive", false);
    return new FsDeletePlan(path, recursive);
  }

  private static HttpPlan ParseHttp(JsonElement parameters)
  {
    var url = RequireString(parameters, "url");
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
      throw new RequestInvalidException("url must be an absolute http or https URL");
    }

    var method = OptionalString(parameters, "method") ?? "GET";
    if (!IsToken(method))
    {
      throw new RequestInvalidException("method must be an HTTP token");
    }

    var headers = OptionalHeaders(parameters, "headers");
    var timeoutMs = OptionalNonNegativeInt(parameters, "timeoutMs", Limits.DefaultHttpTimeoutMs);
    return new HttpPlan(new HttpRequestSpec(url, method, headers, timeoutMs));
  }

  private static CookiesSetPlan ParseCookiesSet(JsonElement parameters)
  {
    if (!TryGetObjectProperty(parameters, "cookies", out var array) || array.ValueKind != JsonValueKind.Array)
    {
      throw new RequestInvalidException("\"cookies\" must be an array");
    }

    var cookies = new List<CookieRecord>();
    foreach (var item in array.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.Object)
      {
        throw new RequestInvalidException("cookie must be an object");
      }

      try
      {
        var record = CookieCodec.Create(
          RequireString(item, "name"),
          RequireString(item, "value"),
          OptionalString(item, "domain"),
          OptionalString(item, "path") ?? "/",
          OptionalBool(item, "secure", false),
          OptionalBool(item, "httpOnly", false),
          OptionalLong(item, "expiresMs"));

        cookies.Add(record);
      }
      catch (Exception ex) when (ex is CookieException or ArgumentException or ArgumentOutOfRangeException)
      {
        throw new RequestInvalidException(ex.Message);
      }
    }

    try
    {
      CookieCodec.Validate(cookies);
    }
    catch (Exception ex) when (ex is CookieException or ArgumentException)
    {
      throw new RequestInvalidException(ex.Message);
    }

    return new CookiesSetPlan(cookies);
  }

  private static string RequireString(JsonElement parameters, string name)
  {
    if (TryGetObjectProperty(parameters, name, out var value) && value.ValueKind == JsonValueKind.String)
    {
      return value.GetString() ?? string.Empty;
    }

    throw new RequestInvalidException($"\"{name}\" must be a string");
  }

  private static string? OptionalString(JsonElement parameters, string name)
  {
    if (!TryGetObjectProperty(parameters, name, out var value) || value.ValueKind == JsonValueKind.Null)
    {
      return null;
    }

    if (value.ValueKind != JsonValueKind.String)
    {
      throw new RequestInvalidException($"\"{name}\" must be a string");
    }

    return value.GetString();
  }

  private static bool OptionalBool(JsonElement parameters, string name, bool fallback)
  {
    if (!TryGetObjectProperty(parameters, name, out var value))
    {
      return fallback;
    }

    return value.ValueKind switch
    {
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => throw new RequestInvalidException($"\"{name}\" must be a boolean"),
    };
  }

  private static int OptionalNonNegativeInt(JsonElement parameters, string name, int fallback)
  {
    if (!TryGetObjectProperty(parameters, name, out var value))
    {
      return fallback;
    }

    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0)
    {
      throw new RequestInvalidException($"\"{name}\" must be a non-negative integer");
    }

    return number;
  }

  private static long? OptionalLong(JsonElement parameters, string name)
  {
    if (!TryGetObjectProperty(parameters, name, out var value) || value.ValueKind == JsonValueKind.Null)
    {
      return null;
    }

    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
    {
      throw new RequestInvalidException($"\"{name}\" must be an integer");
    }

    return number;
  }

  private static string[] OptionalStrings(JsonElement parameters, string name)
  {
    if (!TryGetObjectProperty(parameters, name, out var value))
    {
      return Array.Empty<string>();
    }

    if (value.ValueKind != JsonValueKind.Array)
    {
      throw new RequestInvalidException($"\"{name}\" must be an array of strings");
    }

    var result = new List<string>();
    foreach (var item in value.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.String)
      {
        throw new RequestInvalidException($"\"{name}\" must be an array of strings");
      }

      result.Add(item.GetString() ?? string.Empty);
    }

    return result.ToArray();
  }

  private static IReadOnlyDictionary<string, string> OptionalStringMap(JsonElement parameters, string name)
  {
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    if (!TryGetObjectProperty(parameters, name, out var value))
    {
      return result;
    }

    if (value.ValueKind != JsonValueKind.Object)
    {
      throw new RequestInvalidException($"\"{name}\" must be an object");
    }

    foreach (var property in value.EnumerateObject())
    {
      if (property.Value.ValueKind != JsonValueKind.String)
      {
        throw new RequestInvalidException($"\"{name}\" values must be strings");
      }

      result[property.Name] = property.Value.GetString() ?? string.Empty;
    }

    return result;
  }

  private static IReadOnlyDictionary<string, string[]> OptionalHeaders(JsonElement parameters, string name)
  {
    var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    if (!TryGetObjectProperty(parameters, name, out var value))
    {
      return result;
    }

    if (value.ValueKind != JsonValueKind.Object)
    {
      throw new RequestInvalidException($"\"{name}\" must be an object");
    }

    foreach (var property in value.EnumerateObject())
    {
      if (!IsToken(property.Name))
      {
        throw new RequestInvalidException($"invalid header name: {property.Name}");
      }

      var values = new List<string>();
      if (property.Value.ValueKind == JsonValueKind.String)
      {
        values.Add(property.Value.GetString() ?? string.Empty);
      }
      else if (property.Value.ValueKind == JsonValueKind.Array)
      {
        foreach (var item in property.Value.EnumerateArray())
        {
          if (item.ValueKind != JsonValueKind.String)
          {
            throw new RequestInvalidException($"header {property.Name} values must be strings");
          }

          values.Add(item.GetString() ?? string.Empty);
        }
      }
      else
      {
        throw new RequestInvalidException($"header {property.Name} must be a string or array");
      }

      foreach (var headerValue in values)
      {
        if (headerValue.Contains('\r') || headerValue.Contains('\n'))
        {
          throw new RequestInvalidException($"header {property.Name} contains a line break");
        }
      }

      result[property.Name] = values.ToArray();
    }

    return result;
  }

  private static bool TryGetObjectProperty(JsonElement parameters, string name, out JsonElement value)
  {
    value = default;
    return parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out value);
  }

  private static bool IsToken(string value)
  {
    if (value.Length == 0)
    {
      return false;
    }

    foreach (var ch in value)
    {
      if (!IsTokenChar(ch))
      {
        return false;
      }
    }

    return true;
  }

  private static bool IsTokenChar(char ch) =>
    ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' ||
    "!#$%&'*+-.^_`|~".Contains(ch);

  private abstract record Plan;

  private sealed record ExecPlan(CommandSpec Spec) : Plan;

  private sealed record FsListPlan(string Path, bool Recursive) : Plan;

  private sealed record FsReadPlan(string Path) : Plan;

  private sealed record FsWritePlan(string TargetPath) : Plan;

  private sealed record FsDeletePlan(string Path, bool Recursive) : Plan;

  private sealed record FsStatPlan(string Path) : Plan;

  private sealed record HttpPlan(HttpRequestSpec Spec) : Plan;

  private sealed record CookiesGetPlan : Plan;

  private sealed record CookiesSetPlan(IReadOnlyList<CookieRecord> Cookies) : Plan;
}
