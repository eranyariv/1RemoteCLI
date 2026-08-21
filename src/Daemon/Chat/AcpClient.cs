using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneRemoteCli.Daemon.Chat;

/// <summary>Minimal ACP v1 JSON-RPC client over an agent's stdio process.</summary>
public sealed class AcpClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _calls = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _reader;
    private readonly Task _errors;
    private readonly string _agentName;
    private Exception? _failure;
    private long _nextId;

    private AcpClient(Process process, string agentName, Action<string>? log)
    {
        _process = process;
        _agentName = agentName;
        _log = log;
        _reader = Task.Run(() => ReadAsync(_stopping.Token), CancellationToken.None);
        _errors = Task.Run(() => ReadErrorsAsync(_stopping.Token), CancellationToken.None);
    }

    public event Func<string, JsonElement, ValueTask>? SessionUpdate;

    public event Func<JsonElement, JsonElement, ValueTask>? PermissionRequested;

    public event Func<JsonElement, JsonElement, ValueTask>? ElicitationRequested;

    public static async Task<AcpClient> StartAsync(
        string executable = "copilot",
        IReadOnlyList<string>? arguments = null,
        string agentName = "Copilot",
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments ?? ["--acp", "--stdio"])
        {
            start.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.Start();

        var client = new AcpClient(process, agentName, log);

        try
        {
            JsonElement initialized = await client.CallAsync(
                "initialize",
                new JsonObject
                {
                    ["protocolVersion"] = 1,
                    ["clientCapabilities"] = new JsonObject
                    {
                        ["elicitation"] = new JsonObject
                        {
                            ["form"] = new JsonObject(),
                        },
                    },
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "1remotecli",
                        ["title"] = "1RemoteCLI",
                        ["version"] = Protocol.ProductVersion.Current,
                    },
                },
                cancellationToken).ConfigureAwait(false);

            int version = initialized.TryGetProperty("protocolVersion", out JsonElement protocol)
                ? protocol.GetInt32()
                : 0;

            if (version != 1)
            {
                throw new InvalidOperationException($"{agentName} negotiated unsupported ACP version {version}.");
            }

            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement> CallAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailed();

        long id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = answer;

        if (Volatile.Read(ref _failure) is { } failed &&
            _calls.TryRemove(id, out TaskCompletionSource<JsonElement>? failedCall))
        {
            failedCall.TrySetException(failed);
            ThrowIfFailed();
        }

        using CancellationTokenRegistration cancelled = cancellationToken.Register(() =>
        {
            if (_calls.TryRemove(id, out TaskCompletionSource<JsonElement>? cancelledCall))
            {
                cancelledCall.TrySetCanceled(cancellationToken);
            }
        });

        try
        {
            await WriteAsync(
                new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["method"] = method,
                    ["params"] = parameters,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_calls.TryRemove(id, out TaskCompletionSource<JsonElement>? failedWrite))
            {
                failedWrite.TrySetException(ex);
            }

            throw;
        }

        return await answer.Task.ConfigureAwait(false);
    }

    public Task NotifyAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters,
            },
            cancellationToken);

    public Task RespondPermissionAsync(
        JsonElement rpcId,
        string optionId,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(rpcId.GetRawText()),
                ["result"] = new JsonObject
                {
                    ["outcome"] = new JsonObject
                    {
                        ["outcome"] = "selected",
                        ["optionId"] = optionId,
                    },
                },
            },
            cancellationToken);

    public Task RespondElicitationAsync(
        JsonElement rpcId,
        string action,
        string? fieldName = null,
        string? value = null,
        CancellationToken cancellationToken = default)
    {
        var result = new JsonObject
        {
            ["action"] = action,
        };
        if (action == "accept" && fieldName is not null)
        {
            result["content"] = new JsonObject
            {
                [fieldName] = value,
            };
        }

        return WriteAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(rpcId.GetRawText()),
                ["result"] = result,
            },
            cancellationToken);
    }

    public Task RespondErrorAsync(
        JsonElement rpcId,
        int code,
        string message,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(rpcId.GetRawText()),
                ["error"] = new JsonObject
                {
                    ["code"] = code,
                    ["message"] = message,
                },
            },
            cancellationToken);

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("id", out JsonElement id) &&
                    !root.TryGetProperty("method", out _))
                {
                    CompleteCall(id, root);
                    continue;
                }

                if (!root.TryGetProperty("method", out JsonElement method))
                {
                    continue;
                }

                string? name = method.GetString();
                JsonElement parameters = root.TryGetProperty("params", out JsonElement value)
                    ? value.Clone()
                    : default;

                if (name == "session/update" &&
                    parameters.TryGetProperty("sessionId", out JsonElement sessionId) &&
                    parameters.TryGetProperty("update", out JsonElement update))
                {
                    await InvokeAsync(SessionUpdate, sessionId.GetString() ?? string.Empty, update.Clone())
                        .ConfigureAwait(false);
                }
                else if (name == "session/request_permission" && root.TryGetProperty("id", out JsonElement requestId))
                {
                    await InvokeAsync(PermissionRequested, requestId.Clone(), parameters).ConfigureAwait(false);
                }
                else if (name == "elicitation/create" && root.TryGetProperty("id", out JsonElement elicitationId))
                {
                    await InvokeAsync(ElicitationRequested, elicitationId.Clone(), parameters).ConfigureAwait(false);
                }
            }

            Fail(
                new EndOfStreamException($"The {_agentName} ACP process closed its output."),
                log: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Fail(new OperationCanceledException(cancellationToken), log: false);
        }
        catch (Exception ex)
        {
            Fail(ex, log: true);
        }
    }

    private async Task ReadErrorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await _process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    _log?.Invoke($"chat: {_agentName}: {line}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CompleteCall(JsonElement id, JsonElement root)
    {
        if (id.ValueKind != JsonValueKind.Number ||
            !id.TryGetInt64(out long number) ||
            !_calls.TryRemove(number, out TaskCompletionSource<JsonElement>? call))
        {
            return;
        }

        if (root.TryGetProperty("error", out JsonElement error))
        {
            string message = error.TryGetProperty("message", out JsonElement text)
                ? text.GetString() ?? "ACP request failed."
                : "ACP request failed.";
            call.TrySetException(new InvalidOperationException(message));
            return;
        }

        call.TrySetResult(
            root.TryGetProperty("result", out JsonElement result)
                ? result.Clone()
                : JsonDocument.Parse("null").RootElement.Clone());
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        ThrowIfFailed();

        string line = message.ToJsonString();
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    private void FailCalls(Exception error)
    {
        foreach ((long id, TaskCompletionSource<JsonElement> call) in _calls)
        {
            if (_calls.TryRemove(id, out _))
            {
                call.TrySetException(error);
            }
        }
    }

    private void Fail(Exception error, bool log)
    {
        if (Interlocked.CompareExchange(ref _failure, error, null) is not null)
        {
            return;
        }

        if (log)
        {
            _log?.Invoke($"chat: ACP input stopped ({error.Message}).");
        }

        FailCalls(error);
    }

    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is { } error)
        {
            throw new InvalidOperationException($"{_agentName} ACP is no longer available.", error);
        }
    }

    private static async ValueTask InvokeAsync<T1, T2>(
        Func<T1, T2, ValueTask>? handlers,
        T1 first,
        T2 second)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Func<T1, T2, ValueTask> handler in handlers.GetInvocationList().Cast<Func<T1, T2, ValueTask>>())
        {
            await handler(first, second).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            _process.StandardInput.Close();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            await Task.WhenAll(_reader, _errors).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log?.Invoke($"chat: {_agentName} ACP readers did not stop promptly.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            _log?.Invoke($"chat: {_agentName} ACP shutdown completed with {ex.GetType().Name}.");
        }

        _process.Dispose();
        _write.Dispose();
        _stopping.Dispose();
    }
}
