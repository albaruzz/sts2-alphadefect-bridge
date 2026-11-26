using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Bridge;

public sealed class TopKCandidate
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("prob")]
    public float Prob { get; set; }
}

public sealed class SearchInfoPayload
{
    [JsonPropertyName("backend")]
    public string? Backend { get; set; }

    [JsonPropertyName("searched")]
    public bool Searched { get; set; }

    [JsonPropertyName("changed")]
    public bool Changed { get; set; }

    [JsonPropertyName("sims")]
    public int Sims { get; set; }

    [JsonPropertyName("elapsed_ms")]
    public float ElapsedMs { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("synced")]
    public bool Synced { get; set; }
}

public sealed class PolicyReply
{
    [JsonPropertyName("seq")]
    public int Seq { get; set; }

    [JsonPropertyName("action")]
    public int Action { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("prob")]
    public float Prob { get; set; }

    [JsonPropertyName("value")]
    public float Value { get; set; }

    [JsonPropertyName("win_prob")]
    public float? WinProb { get; set; }

    [JsonPropertyName("top_k")]
    public List<TopKCandidate> TopK { get; set; } = new();

    [JsonPropertyName("searched")]
    public bool Searched { get; set; }

    [JsonPropertyName("changed")]
    public bool Changed { get; set; }

    [JsonPropertyName("search_info")]
    public SearchInfoPayload? SearchInfo { get; set; }

    [JsonPropertyName("ckpt_version")]
    public int? CkptVersion { get; set; }

    [JsonPropertyName("infer_ms")]
    public float InferMs { get; set; }
}

/// <summary>
/// Thread-safe asynchronous TCP client talking to python/play_live.py.
/// Wire format (Sts2Bridge -> play_live.py):
///   - Control messages: UTF-8 JSON line terminated by '\n'
///   - Decision messages: UTF-8 JSON line terminated by '\n', immediately followed by
///     4-byte little-endian int32 length + raw Observation.Serialize() bytes.
/// Wire format (play_live.py -> Sts2Bridge):
///   - UTF-8 JSON line terminated by '\n' deserialized into PolicyReply.
/// </summary>
public sealed class BridgeLink : IDisposable
{
    readonly record struct OutboundItem(string JsonLine, byte[]? BinaryFrame);

    readonly BridgeConfig _cfg;
    readonly ConcurrentQueue<OutboundItem> _sendQueue = new();
    readonly ConcurrentQueue<PolicyReply> _replyQueue = new();
    readonly CancellationTokenSource _cts = new();
    readonly Thread _ioThread;

    volatile bool _connected;
    volatile bool _forceReconnect;
    volatile string _lastError = "";
    Dictionary<string, object?>? _lastRunInfo;
    int _reconnectEpoch;

    public bool IsConnected => _connected;
    public string LastError => _lastError;
    public int ReconnectEpoch => Volatile.Read(ref _reconnectEpoch);

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public BridgeLink(BridgeConfig cfg)
    {
        _cfg = cfg;
        _ioThread = new Thread(IoLoop)
        {
            Name = "Sts2Bridge-TCP",
            IsBackground = true,
        };
        _ioThread.Start();
    }

    public void Reconnect()
    {
        _forceReconnect = true;
    }

    public void SendRunInfo(string character, int ascension, string runSeed, string gameMode, int loadedFloor)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "run_info",
            ["character"] = character,
            ["ascension"] = ascension,
            ["run_seed"] = runSeed,
            ["game_mode"] = gameMode,
            ["loaded_floor"] = loadedFloor,
        };
        _lastRunInfo = payload;
        EnqueueJson(payload);
    }

    public void SendDecision(int seq, string kind, string character, int act, int floor, IReadOnlyList<string> choices, byte[] rawFrame)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["seq"] = seq,
            ["kind"] = kind,
            ["character"] = character,
            ["act"] = act,
            ["floor"] = floor,
            ["choices"] = choices,
            ["frame_len"] = rawFrame.Length,
        };
        while (_sendQueue.Count > 32 && _sendQueue.TryDequeue(out _)) { }
        _sendQueue.Enqueue(new OutboundItem(JsonSerializer.Serialize(payload, JsonOpts), rawFrame));
    }

    public void SendRunEnd(bool victory, int act, int floor)
    {
        _lastRunInfo = null;
        EnqueueJson(new Dictionary<string, object?>
        {
            ["type"] = "run_end",
            ["victory"] = victory,
            ["act"] = act,
            ["floor"] = floor,
        });
    }

    public bool TryDequeueReply(out PolicyReply? reply) => _replyQueue.TryDequeue(out reply);

    void EnqueueJson(Dictionary<string, object?> payload)
    {
        _sendQueue.Enqueue(new OutboundItem(JsonSerializer.Serialize(payload, JsonOpts), null));
    }

    void IoLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                _forceReconnect = false;
                client = new TcpClient { NoDelay = true };
                var connectTask = client.ConnectAsync(_cfg.Host, _cfg.Port);
                while (!connectTask.IsCompleted && !_cts.IsCancellationRequested && !_forceReconnect)
                    Thread.Sleep(50);
                if (_cts.IsCancellationRequested) break;
                if (_forceReconnect || !client.Connected)
                {
                    if (connectTask.IsFaulted)
                        _lastError = connectTask.Exception?.GetBaseException().Message ?? "connect failed";
                    client.Close();
                    WaitBackoff(1500);
                    continue;
                }

                var stream = client.GetStream();
                _connected = true;
                _lastError = "";
                Interlocked.Increment(ref _reconnectEpoch);
                BridgeLog.Info($"Connected to play_live.py at {_cfg.Host}:{_cfg.Port}");

                // Send handshake + cached active run_info on reconnect
                WriteMessage(stream, JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["type"] = "hello",
                    ["mod_version"] = "1.0.0",
                    ["obs_version"] = Sts2Headless.Observation.FormatVersion,
                }, JsonOpts), null);

                if (_lastRunInfo is { } runInfo)
                    WriteMessage(stream, JsonSerializer.Serialize(runInfo, JsonOpts), null);

                using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                var readThread = new Thread(() => ReadLoop(stream, readerCts.Token))
                {
                    Name = "Sts2Bridge-TCP-Recv",
                    IsBackground = true,
                };
                readThread.Start();

                while (!_cts.IsCancellationRequested && !_forceReconnect && _connected)
                {
                    if (_sendQueue.TryDequeue(out var item))
                    {
                        WriteMessage(stream, item.JsonLine, item.BinaryFrame);
                    }
                    else
                    {
                        Thread.Sleep(4);
                    }
                }

                readerCts.Cancel();
            }
            catch (Exception e)
            {
                _lastError = e.GetBaseException().Message;
            }
            finally
            {
                if (_connected)
                    BridgeLog.Info($"Disconnected from play_live.py ({_lastError})");
                _connected = false;
                try { client?.Close(); } catch { }
            }

            if (!_cts.IsCancellationRequested)
                WaitBackoff(_forceReconnect ? 100 : 2000);
        }
    }

    void ReadLoop(NetworkStream stream, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
            while (!token.IsCancellationRequested)
            {
                var line = reader.ReadLine();
                if (line == null)
                {
                    _lastError = "server closed connection";
                    _connected = false;
                    return;
                }
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var reply = JsonSerializer.Deserialize<PolicyReply>(line, JsonOpts);
                    if (reply != null)
                        _replyQueue.Enqueue(reply);
                }
                catch (Exception je)
                {
                    BridgeLog.Warn($"Ignored malformed reply: {je.Message}");
                }
            }
        }
        catch (Exception e)
        {
            if (!token.IsCancellationRequested)
            {
                _lastError = e.GetBaseException().Message;
                _connected = false;
            }
        }
    }

    static void WriteMessage(NetworkStream stream, string jsonLine, byte[]? binaryFrame)
    {
        byte[] headerBytes = Encoding.UTF8.GetBytes(jsonLine + "\n");
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (binaryFrame != null)
        {
            Span<byte> lenPrefix = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenPrefix, binaryFrame.Length);
            stream.Write(lenPrefix);
            stream.Write(binaryFrame, 0, binaryFrame.Length);
        }
        stream.Flush();
    }

    void WaitBackoff(int ms)
    {
        int slept = 0;
        while (slept < ms && !_cts.IsCancellationRequested && !_forceReconnect)
        {
            Thread.Sleep(50);
            slept += 50;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
    }
}
