using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Bridge;

/// <summary>
/// Pure .NET 9.0 SIMD neural network inference engine for AlphaDefect PolicyValueNet (.s2nn).
/// Zero external NuGet or native DLL dependencies: runs natively on macOS (Apple Silicon arm64 & x64),
/// Windows (x64), and Linux (x64) using System.Numerics.Vector&lt;float&gt; hardware acceleration.
/// Implements exact protocol.py parsing, features.py collation, 6-layer Pre-LN Transformer encoder,
/// recycle 2-pass (Ironclad), future_head residual (Fool/Ironclad/Necrobinder), HL-Gauss value head (Defect),
/// and greedy.py equivalence-class probability merging.
/// </summary>
public sealed class NativeModel
{
    const int HeaderInts = 34;
    const int TokInts = 5;
    const int TokFeatsMax = 30;
    const int TokPrivCols = 2;
    const int CandInts = 5;
    const int CandFeatsMax = 20;
    const int NumPools = 5;
    const int ModeRun = 1;

    // TokenKind constants (matching sts2env/protocol.py)
    const int KindGlobal = 1;
    const int KindCreature = 2;
    const int KindPower = 3;
    const int KindCard = 4;
    const int KindRelic = 5;
    const int KindOrb = 7;
    const int KindMapNode = 8;
    const int KindOption = 9;
    const int KindMove = 10;
    const int KindSphereCell = 11;
    const int KindRouteSummary = 14;
    const int KindCombatStat = 15;
    const int KindEncounterPool = 16;
    const int KindFutureContent = 17;
    const int KindOracle = 20;

    // Zone constants
    const int ZoneAlly = 1;
    const int ZoneHand = 3;
    const int ZoneDraw = 4;
    const int ZoneDiscard = 5;
    const int ZoneExhaust = 6;
    const int ZoneDeck = 7;

    // DecisionKind & ActionKind constants
    const int DecisionStuck = 0;
    const int DecisionCombat = 1;
    const int DecisionGameOver = 12;
    const int ActBuy = 14;
    const int ActMinigameCell = 18;

    sealed class S2nnTensorEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("dtype")] public string Dtype { get; set; } = "f32";
        [JsonPropertyName("shape")] public int[] Shape { get; set; } = Array.Empty<int>();
        [JsonPropertyName("offset")] public long Offset { get; set; }
        [JsonPropertyName("nbytes")] public int NBytes { get; set; }
    }

    sealed class S2nnHeader
    {
        [JsonPropertyName("character")] public string Character { get; set; } = "";
        [JsonPropertyName("ckpt_version")] public int CkptVersion { get; set; }
        [JsonPropertyName("obs_format")] public int ObsFormat { get; set; } = 19;
        [JsonPropertyName("vocab_rows")] public int VocabRows { get; set; } = 2560;
        [JsonPropertyName("d")] public int D { get; set; } = 256;
        [JsonPropertyName("layers")] public int Layers { get; set; } = 6;
        [JsonPropertyName("heads")] public int Heads { get; set; } = 4;
        [JsonPropertyName("ff_mult")] public int FfMult { get; set; } = 4;
        [JsonPropertyName("n_kinds")] public int NKinds { get; set; } = 16;
        [JsonPropertyName("n_zones")] public int NZones { get; set; } = 19;
        [JsonPropertyName("n_actions")] public int NActions { get; set; } = 32;
        [JsonPropertyName("n_decisions")] public int NDecisions { get; set; } = 16;
        [JsonPropertyName("n_modes")] public int NModes { get; set; } = 4;
        [JsonPropertyName("obs_v16")] public bool ObsV16 { get; set; }
        [JsonPropertyName("obs_v17")] public bool ObsV17 { get; set; }
        [JsonPropertyName("obs_v18")] public bool ObsV18 { get; set; }
        [JsonPropertyName("obs_v19")] public bool ObsV19 { get; set; }
        [JsonPropertyName("obs_v26")] public bool ObsV26 { get; set; }
        [JsonPropertyName("obs_v27")] public bool ObsV27 { get; set; }
        [JsonPropertyName("value_hl")] public bool ValueHl { get; set; }
        [JsonPropertyName("hl_bins")] public int HlBins { get; set; } = 128;
        [JsonPropertyName("hl_min")] public float HlMin { get; set; } = -2.5f;
        [JsonPropertyName("hl_max")] public float HlMax { get; set; } = 8.0f;
        [JsonPropertyName("hl_beta")] public float HlBeta { get; set; }
        [JsonPropertyName("future_head")] public bool FutureHead { get; set; }
        [JsonPropertyName("fh_dims")] public int FhDims { get; set; } = 5;
        [JsonPropertyName("recycle")] public bool Recycle { get; set; }
        [JsonPropertyName("tensors")] public List<S2nnTensorEntry> Tensors { get; set; } = new();
    }

    sealed class TransformerLayerWeights
    {
        public required float[] Norm1W, Norm1B;
        public required float[] InProjW, InProjB;
        public required float[] OutProjW, OutProjB;
        public required float[] Norm2W, Norm2B;
        public required float[] Linear1W, Linear1B;
        public required float[] Linear2W, Linear2B;
    }

    public string Character { get; }
    public int CkptVersion { get; }
    public int ObsFormat { get; }

    readonly int _vocabRows, _d, _layers, _heads, _ffDim, _nKinds, _nZones, _nActions, _nDecisions, _nModes;
    readonly bool _obsV16, _obsV17, _obsV18, _obsV19, _obsV26, _obsV27, _valueHl, _futureHead, _recycle;
    readonly int _hlBins, _fhDims;
    readonly float _hlBeta;

    // Embeddings & linear weights (all expanded to contiguous float32 at load time for peak SIMD throughput)
    readonly float[] _idEmb, _auxEmb, _candIdEmb, _contentEmbed;
    readonly float[] _bossEmb, _boss2Emb, _histNEmb, _histEEmb, _encNext, _encBag, _encSeen;
    readonly float[] _kindEmb, _zoneEmb, _decEmb, _modeEmb, _actEmb;
    readonly float[] _featW, _featB, _featXW, _featCW, _featV11W, _featV13W, _featV14W, _featV15W;
    readonly float[] _histLinW, _stageLinW;
    readonly float[] _moveInW, _moveInB, _moveOutW, _moveOutB;
    readonly float[] _bindPwInW, _bindPwInB, _bindPwOutW, _bindPwOutB;
    readonly float[] _bindPoolInW, _bindPoolInB, _bindPoolOutW, _bindPoolOutB;
    readonly float[] _poolEmb, _poolN;
    readonly TransformerLayerWeights[] _encLayers;
    readonly float[] _normW, _normB;
    readonly float[] _candFeatW, _candFeatB, _candFeatXW, _candFeatV15W, _noneRef;
    readonly float[] _candMlp0W, _candMlp0B, _candMlp2W, _candMlp2B;
    readonly float[] _candMlpM0W, _candMlpM0B, _candMlpM2W, _candMlpM2B;
    readonly float[] _privProjW, _privProjB, _privBase;
    readonly float[] _valMlp0W, _valMlp0B, _valMlp2W, _valMlp2B;
    readonly float[] _valMlpM0W, _valMlpM0B, _valMlpM2W, _valMlpM2B;

    // Optional versioned weights
    readonly float[]? _drawLinW, _featV16W, _candFeatV16W, _gainGate;
    readonly float[]? _shopLinW, _pwOrderW, _retGate;
    readonly float[]? _v18LinW, _mapV18W, _bridgeGate;
    readonly float[]? _candV19W, _v19LinW, _shopGate, _featV19W;
    readonly float[]? _candV26W, _featV27W;
    readonly float[]? _valHl0W, _valHl0B, _valHl2W, _valHl2B;
    readonly float[]? _valHlM0W, _valHlM0B, _valHlM2W, _valHlM2B, _hlCenters;
    readonly float[]? _fhPred0W, _fhPred0B, _fhPred2W, _fhPred2B, _fhRes0W, _fhRes0B, _fhRes2W, _fhRes2B;
    readonly float[]? _rcyAW, _rcyAB, _rcyBW, _rcyBB, _rcyCW, _rcyCB, _rcyPW, _rcyPB;

    readonly object _inferLock = new();

    private NativeModel(S2nnHeader h, Dictionary<string, float[]> t)
    {
        Character = h.Character;
        CkptVersion = h.CkptVersion;
        ObsFormat = h.ObsFormat;
        _vocabRows = h.VocabRows;
        _d = h.D;
        _layers = h.Layers;
        _heads = h.Heads;
        _ffDim = h.FfMult * h.D;
        _nKinds = h.NKinds;
        _nZones = h.NZones;
        _nActions = h.NActions;
        _nDecisions = h.NDecisions;
        _nModes = h.NModes;
        _obsV16 = h.ObsV16;
        _obsV17 = h.ObsV17;
        _obsV18 = h.ObsV18;
        _obsV19 = h.ObsV19;
        _obsV26 = h.ObsV26;
        _obsV27 = h.ObsV27;
        _valueHl = h.ValueHl && h.HlBeta != 0f;
        _hlBins = h.HlBins;
        _hlBeta = h.HlBeta;
        _futureHead = h.FutureHead;
        _fhDims = h.FhDims;
        _recycle = h.Recycle;

        float[] Req(string k) => t.TryGetValue(k, out var v) ? v : throw new InvalidDataException($"Missing tensor '{k}' in .s2nn");

        _idEmb = Req("id_emb.weight");
        _auxEmb = Req("aux_emb.weight");
        _candIdEmb = Req("cand_id_emb.weight");
        _contentEmbed = Req("content_embed");
        _bossEmb = Req("boss_emb.weight");
        _boss2Emb = Req("boss2_emb.weight");
        _histNEmb = Req("hist_n_emb.weight");
        _histEEmb = Req("hist_e_emb.weight");
        _encNext = Req("enc_next.weight");
        _encBag = Req("enc_bag.weight");
        _encSeen = Req("enc_seen.weight");

        _kindEmb = Req("kind_emb.weight");
        _zoneEmb = Req("zone_emb.weight");
        _decEmb = Req("dec_emb.weight");
        _modeEmb = Req("mode_emb.weight");
        _actEmb = Req("act_emb.weight");

        _featW = Req("feat.weight");
        _featB = Req("feat.bias");
        _featXW = Req("feat_x.weight");
        _featCW = Req("feat_c.weight");
        _featV11W = Req("feat_v11.weight");
        _featV13W = Req("feat_v13.weight");
        _featV14W = Req("feat_v14.weight");
        _featV15W = Req("feat_v15.weight");
        _histLinW = Req("hist_lin.weight");
        _stageLinW = Req("stage_lin.weight");

        _moveInW = Req("move_in.weight");
        _moveInB = Req("move_in.bias");
        _moveOutW = Req("move_out.weight");
        _moveOutB = Req("move_out.bias");

        _bindPwInW = Req("bind_pw_in.weight");
        _bindPwInB = Req("bind_pw_in.bias");
        _bindPwOutW = Req("bind_pw_out.weight");
        _bindPwOutB = Req("bind_pw_out.bias");

        _bindPoolInW = Req("bind_pool_in.weight");
        _bindPoolInB = Req("bind_pool_in.bias");
        _bindPoolOutW = Req("bind_pool_out.weight");
        _bindPoolOutB = Req("bind_pool_out.bias");

        _poolEmb = Req("pool_emb");
        _poolN = Req("pool_n");

        _encLayers = new TransformerLayerWeights[_layers];
        for (int i = 0; i < _layers; i++)
        {
            string lp = $"encoder.layers.{i}";
            _encLayers[i] = new TransformerLayerWeights
            {
                Norm1W = Req($"{lp}.norm1.weight"),
                Norm1B = Req($"{lp}.norm1.bias"),
                InProjW = Req($"{lp}.self_attn.in_proj_weight"),
                InProjB = Req($"{lp}.self_attn.in_proj_bias"),
                OutProjW = Req($"{lp}.self_attn.out_proj.weight"),
                OutProjB = Req($"{lp}.self_attn.out_proj.bias"),
                Norm2W = Req($"{lp}.norm2.weight"),
                Norm2B = Req($"{lp}.norm2.bias"),
                Linear1W = Req($"{lp}.linear1.weight"),
                Linear1B = Req($"{lp}.linear1.bias"),
                Linear2W = Req($"{lp}.linear2.weight"),
                Linear2B = Req($"{lp}.linear2.bias"),
            };
        }

        _normW = Req("norm.weight");
        _normB = Req("norm.bias");
        _candFeatW = Req("cand_feat.weight");
        _candFeatB = Req("cand_feat.bias");
        _candFeatXW = Req("cand_feat_x.weight");
        _candFeatV15W = Req("cand_feat_v15.weight");
        _noneRef = Req("none_ref");

        _candMlp0W = Req("cand_mlp.0.weight");
        _candMlp0B = Req("cand_mlp.0.bias");
        _candMlp2W = Req("cand_mlp.2.weight");
        _candMlp2B = Req("cand_mlp.2.bias");
        _candMlpM0W = Req("cand_mlp_m.0.weight");
        _candMlpM0B = Req("cand_mlp_m.0.bias");
        _candMlpM2W = Req("cand_mlp_m.2.weight");
        _candMlpM2B = Req("cand_mlp_m.2.bias");

        _privProjW = Req("priv_proj.weight");
        _privProjB = Req("priv_proj.bias");
        _privBase = Req("priv_base");

        _valMlp0W = Req("value_mlp.0.weight");
        _valMlp0B = Req("value_mlp.0.bias");
        _valMlp2W = Req("value_mlp.2.weight");
        _valMlp2B = Req("value_mlp.2.bias");
        _valMlpM0W = Req("value_mlp_m.0.weight");
        _valMlpM0B = Req("value_mlp_m.0.bias");
        _valMlpM2W = Req("value_mlp_m.2.weight");
        _valMlpM2B = Req("value_mlp_m.2.bias");

        if (_obsV16)
        {
            _drawLinW = Req("draw_lin.weight");
            _featV16W = Req("feat_v16.weight");
            _candFeatV16W = Req("cand_feat_v16.weight");
            _gainGate = Req("gain_gate");
        }
        if (_obsV17)
        {
            _shopLinW = Req("shop_lin.weight");
            _pwOrderW = Req("pw_order.weight");
            _retGate = Req("ret_gate");
        }
        if (_obsV18)
        {
            _v18LinW = Req("v18_lin.weight");
            _mapV18W = Req("map_v18.weight");
            _bridgeGate = Req("bridge_gate");
        }
        if (_obsV19)
        {
            _candV19W = Req("cand_v19.weight");
            _v19LinW = Req("v19_lin.weight");
            _shopGate = Req("shop_gate");
            _featV19W = Req("feat_v19.weight");
        }
        if (_obsV26) _candV26W = Req("cand_v26.weight");
        if (_obsV27) _featV27W = Req("feat_v27.weight");

        if (_valueHl)
        {
            _valHl0W = Req("value_hl.0.weight");
            _valHl0B = Req("value_hl.0.bias");
            _valHl2W = Req("value_hl.2.weight");
            _valHl2B = Req("value_hl.2.bias");
            _valHlM0W = Req("value_hl_m.0.weight");
            _valHlM0B = Req("value_hl_m.0.bias");
            _valHlM2W = Req("value_hl_m.2.weight");
            _valHlM2B = Req("value_hl_m.2.bias");
            _hlCenters = Req("hl_centers");
        }

        if (_futureHead)
        {
            _fhPred0W = Req("fh_pred.0.weight");
            _fhPred0B = Req("fh_pred.0.bias");
            _fhPred2W = Req("fh_pred.2.weight");
            _fhPred2B = Req("fh_pred.2.bias");
            _fhRes0W = Req("fh_res.0.weight");
            _fhRes0B = Req("fh_res.0.bias");
            _fhRes2W = Req("fh_res.2.weight");
            _fhRes2B = Req("fh_res.2.bias");
        }

        if (_recycle)
        {
            _rcyAW = Req("rcy_a.weight");
            _rcyAB = Req("rcy_a.bias");
            _rcyBW = Req("rcy_b.weight");
            _rcyBB = Req("rcy_b.bias");
            _rcyCW = Req("rcy_c.weight");
            _rcyCB = Req("rcy_c.bias");
            _rcyPW = Req("rcy_p.weight");
            _rcyPB = Req("rcy_p.bias");
        }
    }

    public static NativeModel Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || bytes[0] != (byte)'S' || bytes[1] != (byte)'2' || bytes[2] != (byte)'N' || bytes[3] != (byte)'N')
            throw new InvalidDataException($"Invalid .s2nn file magic in {path}");

        uint ver = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        if (ver != 1)
            throw new InvalidDataException($"Unsupported .s2nn version {ver} in {path}");

        int headerLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4));
        string headerJson = Encoding.UTF8.GetString(bytes, 12, headerLen);
        var header = JsonSerializer.Deserialize<S2nnHeader>(headerJson)
                     ?? throw new InvalidDataException($"Malformed JSON header in {path}");

        int prefixLen = 12 + headerLen;
        int dataStart = prefixLen + ((8 - (prefixLen % 8)) % 8);

        var tensors = new Dictionary<string, float[]>(header.Tensors.Count);
        foreach (var entry in header.Tensors)
        {
            int pos = checked((int)(dataStart + entry.Offset));
            ReadOnlySpan<byte> slice = bytes.AsSpan(pos, entry.NBytes);
            if (entry.Dtype == "f16")
            {
                int count = entry.NBytes / 2;
                var arr = new float[count];
                ReadOnlySpan<ushort> rawU16 = MemoryMarshal.Cast<byte, ushort>(slice);
                for (int i = 0; i < count; i++)
                {
                    ushort bits = BitConverter.IsLittleEndian ? rawU16[i] : BinaryPrimitives.ReverseEndianness(rawU16[i]);
                    arr[i] = (float)BitConverter.UInt16BitsToHalf(bits);
                }
                tensors[entry.Name] = arr;
            }
            else if (entry.Dtype == "f32")
            {
                int count = entry.NBytes / 4;
                var arr = new float[count];
                if (BitConverter.IsLittleEndian)
                {
                    MemoryMarshal.Cast<byte, float>(slice).CopyTo(arr);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                        arr[i] = BinaryPrimitives.ReadSingleLittleEndian(slice.Slice(i * 4, 4));
                }
                tensors[entry.Name] = arr;
            }
            else
            {
                throw new InvalidDataException($"Unsupported tensor dtype '{entry.Dtype}' for {entry.Name}");
            }
        }

        return new NativeModel(header, tensors);
    }

    // -------------------------------------------------------------------------
    // Public Inference Entry Points
    // -------------------------------------------------------------------------

    public readonly record struct InferOutput(
        float[] Logits,
        double[] Probs,
        float Value,
        int ChosenAction,
        string[] Labels,
        float InferMs);

    public PolicyReply Decide(int seq, IReadOnlyList<string>? choiceLabels, ReadOnlySpan<byte> rawFrame, bool mergeEquiv = true)
    {
        var res = EvaluateRaw(rawFrame, mergeEquiv);
        int n = res.Probs.Length;
        var labels = (choiceLabels != null && choiceLabels.Count > 0) ? choiceLabels : res.Labels;

        if (n <= 0)
        {
            return new PolicyReply
            {
                Seq = seq,
                Action = 0,
                Label = "",
                Prob = 1f,
                Value = 0f,
                WinProb = 0.5f,
                CkptVersion = CkptVersion,
                InferMs = res.InferMs,
                Source = "builtin",
                ModelCharacter = Character,
            };
        }

        var order = Enumerable.Range(0, n)
            .OrderByDescending(i => res.Probs[i])
            .ThenBy(i => i)
            .Take(Math.Min(10, n))
            .Select(i => new TopKCandidate
            {
                Index = i,
                Label = i < labels.Count ? labels[i] : $"Action #{i}",
                Prob = MathF.Round((float)res.Probs[i], 4),
            })
            .ToList();

        int act = res.ChosenAction;
        string chosenLabel = (act >= 0 && act < labels.Count) ? labels[act] : $"Action #{act}";
        float winProb = EstimateWinProb(res.Value);

        return new PolicyReply
        {
            Seq = seq,
            Action = act,
            Label = chosenLabel,
            Prob = MathF.Round((float)res.Probs[act], 4),
            Value = MathF.Round(res.Value, 4),
            WinProb = MathF.Round(winProb, 4),
            TopK = order,
            Searched = false,
            Changed = false,
            SearchInfo = null,
            CkptVersion = CkptVersion,
            InferMs = MathF.Round(res.InferMs, 2),
            Source = "builtin",
            ModelCharacter = Character,
        };
    }

    public InferOutput EvaluateRaw(ReadOnlySpan<byte> rawFrame, bool mergeEquiv = true)
    {
        var sw = Stopwatch.StartNew();
        lock (_inferLock)
        {
            var parsed = ParseFrame(rawFrame);
            int n = parsed.NumCands;
            if (n <= 0)
                return new InferOutput(Array.Empty<float>(), Array.Empty<double>(), 0f, 0, parsed.Labels, (float)sw.Elapsed.TotalMilliseconds);

            var prep = PrepareSingle(parsed);

            RecycleAdd? rec = _recycle ? RunRecyclePass(prep) : null;
            EncodeSingle(prep, rec, out float[] e, out float[] ctx, out float[] g);
            float[] logits = CandLogitsSingle(prep, e, ctx, g, rec?.Cand);
            float value = ValueSingle(prep, e, g);

            double[] probs = new double[n];
            double maxLg = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
                if (float.IsFinite(logits[i]) && logits[i] > maxLg)
                    maxLg = logits[i];

            if (double.IsFinite(maxLg))
            {
                double sum = 0.0;
                for (int i = 0; i < n; i++)
                {
                    if (float.IsFinite(logits[i]))
                    {
                        double p = Math.Exp(logits[i] - maxLg);
                        probs[i] = p;
                        sum += p;
                    }
                }
                double inv = 1.0 / Math.Max(sum, 1e-12);
                for (int i = 0; i < n; i++) probs[i] *= inv;
            }
            else
            {
                double u = 1.0 / n;
                for (int i = 0; i < n; i++) probs[i] = u;
            }

            int chosen = mergeEquiv ? MergedArgmax(parsed, probs) : Argmax(probs);
            float ms = (float)sw.Elapsed.TotalMilliseconds;
            return new InferOutput(logits, probs, value, chosen, parsed.Labels, ms);
        }
    }

    public static float EstimateWinProb(float value) =>
        (float)(1.0 / (1.0 + Math.Exp(-0.85 * (value - 1.8))));

    // -------------------------------------------------------------------------
    // Frame Parsing & Collation (matching protocol.py & features.py)
    // -------------------------------------------------------------------------

    sealed class ParsedObs
    {
        public required int[] Header;          // [34]
        public required int NumTokens;
        public required int NumCands;
        public required int[] TokInts;         // [S * 5]
        public required float[] RawTokFeats;   // [S * 30] unsquashed
        public required int[] CandInts;        // [C * 5]
        public required float[] RawCandFeats;  // [C * 20] unsquashed
        public required string[] Labels;
    }

    static ParsedObs ParseFrame(ReadOnlySpan<byte> buf)
    {
        int ver = BinaryPrimitives.ReadInt32LittleEndian(buf[..4]);
        int nh = ver >= 19 ? HeaderInts : ver == 18 ? 32 : ver == 17 ? 30 : ver == 16 ? 28 : ver == 15 ? 26 : ver == 14 ? 24 : 18;
        int[] header = new int[HeaderInts];
        for (int i = 0; i < nh; i++)
            header[i] = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(i * 4, 4));

        int n = header[2];
        int m = header[3];
        int off = nh * 4;

        int[] tokInts = new int[n * TokInts];
        for (int i = 0; i < tokInts.Length; i++, off += 4)
            tokInts[i] = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(off, 4));

        int ntf = ver >= 29 ? 30 : ver >= 27 ? 22 : 20;
        float[] rawTokFeats = new float[n * TokFeatsMax];
        for (int r = 0; r < n; r++)
        {
            int rowBase = r * TokFeatsMax;
            for (int c = 0; c < ntf; c++, off += 4)
                rawTokFeats[rowBase + c] = BinaryPrimitives.ReadSingleLittleEndian(buf.Slice(off, 4));
        }

        // Skip tok_priv (forced to 0 in live mode for human-visible fairness)
        off += n * TokPrivCols * 4;

        int[] candInts = new int[m * CandInts];
        for (int i = 0; i < candInts.Length; i++, off += 4)
            candInts[i] = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(off, 4));

        int ncf = ver >= 29 ? 20 : ver >= 26 ? 14 : ver >= 19 ? 12 : 8;
        float[] rawCandFeats = new float[m * CandFeatsMax];
        for (int r = 0; r < m; r++)
        {
            int rowBase = r * CandFeatsMax;
            for (int c = 0; c < ncf; c++, off += 4)
                rawCandFeats[rowBase + c] = BinaryPrimitives.ReadSingleLittleEndian(buf.Slice(off, 4));
        }

        string[] labels = Array.Empty<string>();
        if (buf.Length >= off + 4)
        {
            int labelLen = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(off, 4));
            off += 4;
            if (labelLen > 0 && buf.Length >= off + labelLen)
            {
                string json = Encoding.UTF8.GetString(buf.Slice(off, labelLen));
                labels = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            }
        }

        return new ParsedObs
        {
            Header = header,
            NumTokens = n,
            NumCands = m,
            TokInts = tokInts,
            RawTokFeats = rawTokFeats,
            CandInts = candInts,
            RawCandFeats = rawCandFeats,
            Labels = labels,
        };
    }

    sealed class PreparedStep
    {
        public required ParsedObs Obs;
        public required float[] TokFeats;       // [S * 30] squashed
        public required float[] CandFeats;      // [C * 20] squashed
        public required int[] CoreIdx;          // [K_core]
        public required List<(int Src, int Dst)> PwLinks;
        public required List<(int Src, int Dst)> MvLinks;
        public required List<(int Tok, int Slot)> PoolItems;
        public required float[] PoolCnt;        // [5]
        public required int[] CandA;            // [C]
        public required int[] CandB;            // [C]
        public required List<int> DrawToks;
        public required List<int> GlobToks;
        public required List<(int Src, int Dst)> EncLinks;
        public required List<(int Tok, int GainId)> GainItems;
        public required bool Macro;
        public required float[] GlobHist;       // [3]
        public required float[] GlobStage;      // [2]
        public required float[] GlobDraw;       // [2]
        public required float[] GlobShop;       // [2]
        public required float[] GlobV18;        // [2]
        public required float[] GlobV19;        // [2]
    }

    static PreparedStep PrepareSingle(ParsedObs o)
    {
        int S = o.NumTokens;
        int C = o.NumCands;
        int[] ti = o.TokInts;

        float[] tf = new float[o.RawTokFeats.Length];
        for (int i = 0; i < tf.Length; i++) tf[i] = Squash(o.RawTokFeats[i]);

        float[] cf = new float[o.RawCandFeats.Length];
        for (int i = 0; i < cf.Length; i++) cf[i] = Squash(o.RawCandFeats[i]);

        int[] role = new int[S];
        var coreList = new List<int>(S);
        var pwLinks = new List<(int, int)>();
        var mvLinks = new List<(int, int)>();
        var poolItems = new List<(int, int)>();
        float[] poolCnt = new float[NumPools];
        var drawToks = new List<int>();
        var globToks = new List<int>(2);
        var encSrcs = new List<int>();
        var gainItems = new List<(int, int)>();
        int nHand = 0;

        for (int t = 0; t < S; t++)
        {
            int kind = ti[t * 5];
            int zone = ti[t * 5 + 2];
            int owner = ti[t * 5 + 3];

            if (kind == KindCard && zone == ZoneHand) nHand++;
            if (kind == KindGlobal) globToks.Add(t);
            if (kind == KindOption && o.RawTokFeats[t * TokFeatsMax + 15] > 0f)
            {
                int gid = (int)MathF.Round(o.RawTokFeats[t * TokFeatsMax + 15], MidpointRounding.ToEven);
                gainItems.Add((t, gid));
            }

            int r = -1;
            if (kind == KindCard)
            {
                r = zone switch
                {
                    ZoneDraw => 0,
                    ZoneDiscard => 1,
                    ZoneExhaust => 2,
                    ZoneDeck => 3,
                    _ => -1,
                };
            }
            else if (kind == KindRelic && zone == ZoneAlly) r = 4;
            else if (kind == KindPower || kind == KindCombatStat) r = -2;
            else if (kind == KindMove) r = -3;
            else if (kind == KindEncounterPool) r = -4;
            else if (kind == KindFutureContent) r = -5;
            else if (kind == KindOracle) r = -6;

            role[t] = r;
            if (r == -1) coreList.Add(t);
            else if (r == -2 && owner >= 0 && owner < S) pwLinks.Add((t, owner));
            else if (r == -3 && owner >= 0 && owner < S) mvLinks.Add((t, owner));
            else if (r >= 0)
            {
                poolItems.Add((t, r));
                poolCnt[r] += 1f;
                if (r == 0) drawToks.Add(t);
            }
            else if (r == -4) encSrcs.Add(t);
        }

        int globOf = globToks.Count > 0 ? globToks[^1] : -1;
        var encLinks = new List<(int, int)>(encSrcs.Count);
        if (globOf >= 0)
        {
            foreach (int es in encSrcs) encLinks.Add((es, globOf));
        }

        int[] candA = new int[C];
        int[] candB = new int[C];
        for (int c = 0; c < C; c++)
        {
            int a = o.CandInts[c * 5 + 1];
            int b = o.CandInts[c * 5 + 2];
            candA[c] = ResolveRef(a, S, role);
            candB[c] = ResolveRef(b, S, role);
        }

        int[] h = o.Header;
        bool macro = h[11] == 0 && h[1] != DecisionCombat && h[1] != DecisionGameOver && h[1] != DecisionStuck;

        int ver = h[0];
        float canDraw = ver < 16 ? 1f : h[26];
        float handSpace = (h[11] > 0 ? (ver < 16 ? Math.Max(0, 10 - nHand) : h[27]) : 0) / 10f;

        return new PreparedStep
        {
            Obs = o,
            TokFeats = tf,
            CandFeats = cf,
            CoreIdx = coreList.ToArray(),
            PwLinks = pwLinks,
            MvLinks = mvLinks,
            PoolItems = poolItems,
            PoolCnt = poolCnt,
            CandA = candA,
            CandB = candB,
            DrawToks = drawToks,
            GlobToks = globToks,
            EncLinks = encLinks,
            GainItems = gainItems,
            Macro = macro,
            GlobHist = new[] { Squash(h[18]), Squash(h[19]), Squash(h[20]) },
            GlobStage = new[] { (float)h[24], (float)h[25] },
            GlobDraw = new[] { canDraw, handSpace },
            GlobShop = ver >= 17 ? new[] { h[28] / 5f, h[29] / 300f } : new[] { 0f, 0f },
            GlobV18 = ver >= 18 ? new[] { h[30] / 3f, h[31] / 10f } : new[] { 0f, 0f },
            GlobV19 = ver >= 19 ? new[] { h[32] / 10f, h[33] / 10f } : new[] { 0f, 0f },
        };
    }

    static int ResolveRef(int a, int S, int[] role)
    {
        if (a < 0 || S <= 0) return -1;
        int g = Math.Clamp(a, 0, S - 1);
        return role[g] == -1 ? g : S + g;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Squash(float x)
    {
        if (x == 0f) return 0f;
        float clamped = MathF.Min(MathF.Abs(x), 10000f);
        return MathF.CopySign(float.LogP1(clamped), x);
    }

    // -------------------------------------------------------------------------
    // Encoder, Recycle, Candidate Head, Value Head (matching model.py)
    // -------------------------------------------------------------------------

    sealed class RecycleAdd
    {
        public required float[] TokAdd; // [S * d]
        public required float[] Pool;   // [d]
        public required float[] Cand;   // [C * d]
    }

    RecycleAdd RunRecyclePass(PreparedStep p)
    {
        int S = p.Obs.NumTokens;
        int C = p.Obs.NumCands;
        int d = _d;

        EncodeSingle(p, null, out float[] e1, out float[] ctx1, out float[] g1);
        float[] lg = CandLogitsSingle(p, e1, ctx1, g1, null);

        int width = Math.Max(1, C);
        float maxLg = float.NegativeInfinity;
        int nFinite = 0;
        for (int i = 0; i < width; i++)
        {
            float v = i < C ? lg[i] : 0f;
            if (float.IsFinite(v))
            {
                nFinite++;
                if (v > maxLg) maxLg = v;
            }
        }
        if (!float.IsFinite(maxLg)) maxLg = 0f;

        float sumExp = 0f;
        float[] lp = new float[width];
        float[] pr = new float[width];
        for (int i = 0; i < width; i++)
        {
            float v = i < C ? lg[i] : 0f;
            pr[i] = float.IsFinite(v) ? MathF.Exp(v - maxLg) : 0f;
            sumExp += pr[i];
        }
        float logZ = maxLg + MathF.Log(MathF.Max(sumExp, 1e-20f));
        float ent = 0f, p1 = 0f, p2 = 0f;
        for (int i = 0; i < width; i++)
        {
            float v = i < C ? lg[i] : 0f;
            lp[i] = v - logZ;
            pr[i] = float.IsFinite(v) ? MathF.Exp(lp[i]) : 0f;
            if (pr[i] > 0f) ent -= pr[i] * lp[i];
            if (pr[i] >= p1) { p2 = p1; p1 = pr[i]; }
            else if (pr[i] > p2) { p2 = pr[i]; }
        }

        Span<float> fs = stackalloc float[4] { ent, p1, p1 - p2, MathF.Log(Math.Max(1, nFinite)) / 3f };
        float[] poolAdd = new float[d];
        Linear(_rcyPW!, _rcyPB, fs, poolAdd);

        float[] tokAdd = new float[S * d];
        float[] candAdd = new float[C * d];
        Span<float> fc = stackalloc float[6];
        Span<float> tmp = stackalloc float[d];

        for (int c = 0; c < C; c++)
        {
            fc[0] = pr[c];
            fc[1] = MathF.Max(lp[c], -20f) / 8f;
            fc[2] = MathF.Max(lg[c] - maxLg, -20f) / 8f;
            fc[3] = pr[c] >= p1 - 1e-6f ? 1f : 0f;
            fc[4] = ent;
            fc[5] = p1;

            Linear(_rcyCW!, _rcyCB, fc, candAdd.AsSpan(c * d, d));

            if (p.CandA[c] >= 0 && S > 0)
            {
                Linear(_rcyAW!, _rcyAB, fc, tmp);
                AddInPlace(tokAdd.AsSpan((p.CandA[c] % S) * d, d), tmp);
            }
            if (p.CandB[c] >= 0 && S > 0)
            {
                Linear(_rcyBW!, _rcyBB, fc, tmp);
                AddInPlace(tokAdd.AsSpan((p.CandB[c] % S) * d, d), tmp);
            }
        }

        return new RecycleAdd { TokAdd = tokAdd, Pool = poolAdd, Cand = candAdd };
    }

    void EncodeSingle(PreparedStep p, RecycleAdd? rec, out float[] e, out float[] ctx, out float[] g)
    {
        int S = p.Obs.NumTokens;
        int d = _d;
        int[] ti = p.Obs.TokInts;
        e = new float[S * d];

        Span<float> tf = stackalloc float[20];
        Span<float> tf27 = stackalloc float[2];
        Span<float> tf15 = stackalloc float[20];
        Span<float> tf16 = stackalloc float[20];
        Span<float> tf17 = stackalloc float[20];
        Span<float> tf18 = stackalloc float[20];
        Span<float> tf19 = stackalloc float[20];
        Span<float> tf14 = stackalloc float[20];
        Span<float> tf13 = stackalloc float[20];
        Span<float> tfNew = stackalloc float[20];
        Span<float> v19In = stackalloc float[8];
        Span<float> v11Vec = stackalloc float[d];

        for (int t = 0; t < S; t++)
        {
            int kind = ti[t * 5];
            int id = ti[t * 5 + 1];
            int zone = ti[t * 5 + 2];
            int aux = ti[t * 5 + 4];

            ReadOnlySpan<float> srcRow = p.TokFeats.AsSpan(t * TokFeatsMax, TokFeatsMax);
            srcRow[..20].CopyTo(tf);
            srcRow.Slice(20, 2).CopyTo(tf27);

            // Exact split sequence matching model.py lines 344-354
            tf15.Clear();
            for (int c = 0; c < 20; c++)
                if ((kind == KindOrb && c == 3) || (kind == KindSphereCell && c >= 16) || (kind == KindPower && c >= 4 && c <= 6))
                { tf15[c] = tf[c]; tf[c] = 0f; }

            if (_obsV16)
            {
                tf16.Clear();
                if (kind == KindOption)
                    for (int c = 12; c <= 15; c++) { tf16[c] = tf[c]; tf[c] = 0f; }
            }

            if (_obsV17)
            {
                tf17.Clear();
                if (kind == KindPower) { tf17[7] = tf[7]; tf[7] = 0f; }
            }

            if (_obsV18)
            {
                tf18.Clear();
                if (kind == KindMapNode)
                    for (int c = 16; c <= 18; c++) { tf18[c] = tf[c]; tf[c] = 0f; }
            }

            tf19.Clear();
            if (kind == KindOption)
            {
                for (int c = 8; c <= 11; c++) { tf19[c] = tf[c]; tf[c] = 0f; }
                for (int c = 16; c < 20; c++) { tf19[c] = tf[c]; tf[c] = 0f; }
            }

            tf14.Clear();
            if (kind == KindRelic)
                for (int c = 3; c <= 7; c++) { tf14[c] = tf[c]; tf[c] = 0f; }
            else if (kind == KindCombatStat)
            { tf14[19] = tf[19]; tf[19] = 0f; }

            tf13.Clear();
            if (kind == KindCombatStat)
                for (int c = 13; c < 20; c++) { tf13[c] = tf[c]; tf[c] = 0f; }
            else if (kind == KindRouteSummary)
                for (int c = 16; c < 20; c++) { tf13[c] = tf[c]; tf[c] = 0f; }
            else if (kind == KindPower)
            { tf13[3] = tf[3]; tf[3] = 0f; }

            tfNew.Clear();
            bool isPlayer = kind == KindCreature && tf[5] > 0f;
            for (int c = 0; c < 20; c++)
            {
                if ((kind == KindPower && c == 2)
                    || (kind == KindMove && c >= 14 && c <= 17)
                    || (isPlayer && (c == 18 || c == 19))
                    || kind == KindCombatStat)
                {
                    tfNew[c] = tf[c];
                    tf[c] = 0f;
                }
            }

            Span<float> dst = e.AsSpan(t * d, d);
            int kIdx = Math.Clamp(kind, 0, _nKinds - 1);
            Linear(_featV11W, null, tfNew, v11Vec);

            if (kind == KindCombatStat)
            {
                v11Vec.CopyTo(dst);
                AddInPlace(dst, _kindEmb.AsSpan(kIdx * d, d));
            }
            else
            {
                int zIdx = Math.Clamp(zone, 0, _nZones - 1);
                Linear(_featW, _featB, tf[..16], dst);
                AddVocabRow(dst, _idEmb, id);
                AddVocabRow(dst, _auxEmb, aux);
                AddVocabRow(dst, _contentEmbed, id);
                AddVocabRow(dst, _contentEmbed, aux);
                AddInPlace(dst, _kindEmb.AsSpan(kIdx * d, d));
                AddInPlace(dst, _zoneEmb.AsSpan(zIdx * d, d));
                LinearAdd(kind == KindCreature ? _featCW : _featXW, tf.Slice(16, 4), dst);
                AddInPlace(dst, v11Vec);
            }

            LinearAdd(_featV13W, tf13, dst);
            LinearAdd(_featV14W, tf14, dst);
            LinearAdd(_featV15W, tf15, dst);

            if (_obsV16) LinearAdd(_featV16W!, tf16.Slice(12, 3), dst);
            if (_obsV17) LinearAdd(_pwOrderW!, tf17.Slice(7, 1), dst);
            if (_obsV18) LinearAdd(_mapV18W!, tf18.Slice(16, 3), dst);
            if (_obsV19)
            {
                tf19.Slice(8, 4).CopyTo(v19In[..4]);
                tf19.Slice(16, 4).CopyTo(v19In.Slice(4, 4));
                LinearAdd(_featV19W!, v19In, dst);
            }
            if (_obsV27) LinearAdd(_featV27W!, tf27, dst);
        }

        if (_obsV16 && p.GainItems.Count > 0)
        {
            foreach (var (gt, gid) in p.GainItems)
            {
                if (gid > 0 && gid < _vocabRows)
                    AddMulInPlace(e.AsSpan(gt * d, d), _auxEmb.AsSpan(gid * d, d), _gainGate!);
            }
        }

        int boss0 = p.Obs.Header[16], boss1 = p.Obs.Header[17];
        int lastN = p.Obs.Header[21], lastE = p.Obs.Header[22];
        foreach (int gt in p.GlobToks)
        {
            Span<float> gd = e.AsSpan(gt * d, d);
            AddVocabRow(gd, _bossEmb, boss0);
            AddVocabRow(gd, _bossEmb, boss1);
            AddVocabRow(gd, _boss2Emb, boss1);
            LinearAdd(_histLinW, p.GlobHist, gd);
            AddVocabRow(gd, _histNEmb, lastN);
            AddVocabRow(gd, _histEEmb, lastE);
            LinearAdd(_stageLinW, p.GlobStage, gd);
            if (_obsV16) LinearAdd(_drawLinW!, p.GlobDraw, gd);
            if (_obsV17) LinearAdd(_shopLinW!, p.GlobShop, gd);
            if (_obsV18) LinearAdd(_v18LinW!, p.GlobV18, gd);
            if (_obsV19) LinearAdd(_v19LinW!, p.GlobV19, gd);
        }

        foreach (var (es, ed) in p.EncLinks)
        {
            int eid = ti[es * 5 + 1];
            if (eid > 0 && eid < _vocabRows)
            {
                Span<float> gd = e.AsSpan(ed * d, d);
                float ef3 = p.TokFeats[es * TokFeatsMax + 3];
                float ef4 = p.TokFeats[es * TokFeatsMax + 4];
                float ef5 = p.TokFeats[es * TokFeatsMax + 5];
                AddScaledInPlace(gd, _encNext.AsSpan(eid * d, d), ef5);
                AddScaledInPlace(gd, _encBag.AsSpan(eid * d, d), ef4);
                AddScaledInPlace(gd, _encSeen.AsSpan(eid * d, d), ef3);
            }
        }

        // Compute move & power bindings from pre-accumulation e, then apply together
        float[] mvBuf = new float[p.MvLinks.Count * d];
        Span<float> midD = stackalloc float[d];
        for (int i = 0; i < p.MvLinks.Count; i++)
        {
            int ms = p.MvLinks[i].Src;
            LinearGelu(_moveInW, _moveInB, e.AsSpan(ms * d, d), midD);
            Linear(_moveOutW, _moveOutB, midD, mvBuf.AsSpan(i * d, d));
        }

        float[] pwBuf = new float[p.PwLinks.Count * d];
        Span<float> mid2D = stackalloc float[2 * d];
        for (int i = 0; i < p.PwLinks.Count; i++)
        {
            int ps = p.PwLinks[i].Src;
            int pKind = ti[ps * 5];
            Span<float> pw = pwBuf.AsSpan(i * d, d);
            e.AsSpan(ps * d, d).CopyTo(pw);
            LinearGelu(_bindPwInW, _bindPwInB, pw, mid2D);
            LinearAddWithBias(_bindPwOutW, _bindPwOutB, mid2D, pw);

            if (_obsV17 && pKind == KindPower && p.TokFeats[ps * TokFeatsMax + 19] > 0f)
                MulInPlace(pw, _retGate!);
            if (_obsV18 && pKind == KindPower && p.TokFeats[ps * TokFeatsMax + 18] > 0f)
                MulInPlace(pw, _bridgeGate!);
            if (pKind == KindPower && p.TokFeats[ps * TokFeatsMax + 17] > 0f)
            {
                if (_obsV19) MulInPlace(pw, _shopGate!);
                else pw.Clear();
            }
        }

        for (int i = 0; i < p.PwLinks.Count; i++)
            AddInPlace(e.AsSpan(p.PwLinks[i].Dst * d, d), pwBuf.AsSpan(i * d, d));
        for (int i = 0; i < p.MvLinks.Count; i++)
            AddInPlace(e.AsSpan(p.MvLinks[i].Dst * d, d), mvBuf.AsSpan(i * d, d));

        if (rec != null)
            AddInPlace(e, rec.TokAdd);

        // 5 summary pools
        float[] pools = new float[NumPools * d];
        Span<float> ptTmp = stackalloc float[d];
        foreach (var (ptok, slot) in p.PoolItems)
        {
            e.AsSpan(ptok * d, d).CopyTo(ptTmp);
            LinearGelu(_bindPoolInW, _bindPoolInB, ptTmp, mid2D);
            LinearAddWithBias(_bindPoolOutW, _bindPoolOutB, mid2D, ptTmp);
            AddInPlace(pools.AsSpan(slot * d, d), ptTmp);
        }
        for (int s = 0; s < NumPools; s++)
        {
            Span<float> ps = pools.AsSpan(s * d, d);
            float cnt = p.PoolCnt[s];
            if (cnt > 1f) ScaleInPlace(ps, 1f / cnt);
            AddInPlace(ps, _poolEmb.AsSpan(s * d, d));
            AddScaledInPlace(ps, _poolN.AsSpan(s * d, d), float.LogP1(cnt));
            if (rec != null) AddInPlace(ps, rec.Pool);
        }

        // Pre-LN Transformer Encoder over [core_0 .. core_{K-1}, pool_0 .. pool_4]
        int nCore = p.CoreIdx.Length;
        int L = nCore + NumPools;
        float[] x = new float[L * d];
        for (int i = 0; i < nCore; i++)
            e.AsSpan(p.CoreIdx[i] * d, d).CopyTo(x.AsSpan(i * d, d));
        pools.CopyTo(x.AsSpan(nCore * d, NumPools * d));

        float[] hNorm = new float[L * d];
        float[] qkv = new float[L * 3 * d];
        float[] attnOut = new float[L * d];
        float[] ffMid = new float[_ffDim];
        float[] scores = new float[L];
        int dh = d / _heads;
        float scale = 1f / MathF.Sqrt(dh);

        for (int li = 0; li < _layers; li++)
        {
            var lw = _encLayers[li];
            for (int t = 0; t < L; t++)
            {
                LayerNorm(x.AsSpan(t * d, d), lw.Norm1W, lw.Norm1B, hNorm.AsSpan(t * d, d));
                Linear(lw.InProjW, lw.InProjB, hNorm.AsSpan(t * d, d), qkv.AsSpan(t * 3 * d, 3 * d));
            }

            Array.Clear(attnOut, 0, attnOut.Length);
            for (int head = 0; head < _heads; head++)
            {
                int qOff = head * dh;
                int kOff = d + head * dh;
                int vOff = 2 * d + head * dh;
                for (int i = 0; i < L; i++)
                {
                    ReadOnlySpan<float> qi = qkv.AsSpan(i * 3 * d + qOff, dh);
                    float maxS = float.NegativeInfinity;
                    for (int j = 0; j < L; j++)
                    {
                        float s = Dot(qi, qkv.AsSpan(j * 3 * d + kOff, dh)) * scale;
                        scores[j] = s;
                        if (s > maxS) maxS = s;
                    }
                    float sumS = 0f;
                    for (int j = 0; j < L; j++)
                    {
                        scores[j] = MathF.Exp(scores[j] - maxS);
                        sumS += scores[j];
                    }
                    float invS = 1f / sumS;
                    Span<float> oi = attnOut.AsSpan(i * d + qOff, dh);
                    for (int j = 0; j < L; j++)
                        AddScaledInPlace(oi, qkv.AsSpan(j * 3 * d + vOff, dh), scores[j] * invS);
                }
            }

            for (int t = 0; t < L; t++)
            {
                Span<float> xt = x.AsSpan(t * d, d);
                LinearAddWithBias(lw.OutProjW, lw.OutProjB, attnOut.AsSpan(t * d, d), xt);
                LayerNorm(xt, lw.Norm2W, lw.Norm2B, hNorm.AsSpan(t * d, d));
                LinearRelu(lw.Linear1W, lw.Linear1B, hNorm.AsSpan(t * d, d), ffMid);
                LinearAddWithBias(lw.Linear2W, lw.Linear2B, ffMid, xt);
            }
        }

        g = new float[d];
        for (int t = 0; t < L; t++)
        {
            Span<float> xt = x.AsSpan(t * d, d);
            LayerNormInPlace(xt, _normW, _normB);
            AddInPlace(g, xt);
        }
        ScaleInPlace(g, 1f / Math.Max(1, L));

        ctx = new float[S * d];
        for (int i = 0; i < nCore; i++)
            x.AsSpan(i * d, d).CopyTo(ctx.AsSpan(p.CoreIdx[i] * d, d));

        int decIdx = Math.Clamp(p.Obs.Header[1], 0, _nDecisions - 1);
        int modeIdx = Math.Clamp(ModeRun, 0, _nModes - 1);
        AddInPlace(g, _decEmb.AsSpan(decIdx * d, d));
        AddInPlace(g, _modeEmb.AsSpan(modeIdx * d, d));
    }

    float[] CandLogitsSingle(PreparedStep p, float[] e, float[] ctx, float[] g, float[]? candAdd)
    {
        int S = p.Obs.NumTokens;
        int C = p.Obs.NumCands;
        int d = _d;
        float[] logits = new float[C];

        Span<float> zc = stackalloc float[4 * d];
        Span<float> mid = stackalloc float[2 * d];
        Span<float> fhMid = stackalloc float[Math.Max(1, d / 2)];
        Span<float> fhVec = stackalloc float[Math.Max(1, _fhDims)];
        Span<float> fhResMid = stackalloc float[32];
        Span<float> scalar = stackalloc float[1];

        for (int c = 0; c < C; c++)
        {
            GatherRef(p.CandA[c], S, ctx, e).CopyTo(zc[..d]);
            GatherRef(p.CandB[c], S, ctx, e).CopyTo(zc.Slice(d, d));

            Span<float> act = zc.Slice(2 * d, d);
            int a0 = p.Obs.CandInts[c * 5];
            int cid = p.Obs.CandInts[c * 5 + 3];
            ReadOnlySpan<float> cf = p.CandFeats.AsSpan(c * CandFeatsMax, CandFeatsMax);

            Linear(_candFeatW, _candFeatB, cf[..4], act);
            AddInPlace(act, _actEmb.AsSpan(Math.Clamp(a0, 0, _nActions - 1) * d, d));
            AddVocabRow(act, _candIdEmb, cid);

            if (a0 == ActMinigameCell)
                LinearAdd(_candFeatV15W, cf.Slice(4, 4), act);
            else if (_obsV16 && a0 == ActBuy)
                LinearAdd(_candFeatV16W!, cf.Slice(4, 4), act);
            else
                LinearAdd(_candFeatXW, cf.Slice(4, 4), act);

            if (_obsV19) LinearAdd(_candV19W!, cf.Slice(8, 4), act);
            if (_obsV26) LinearAdd(_candV26W!, cf.Slice(12, 2), act);
            if (candAdd != null) AddInPlace(act, candAdd.AsSpan(c * d, d));

            g.CopyTo(zc.Slice(3 * d, d));

            if (p.Macro)
            {
                LinearGelu(_candMlpM0W, _candMlpM0B, zc, mid);
                Linear(_candMlpM2W, _candMlpM2B, mid, scalar);
            }
            else
            {
                LinearGelu(_candMlp0W, _candMlp0B, zc, mid);
                Linear(_candMlp2W, _candMlp2B, mid, scalar);
            }

            float score = scalar[0];
            if (_futureHead && !p.Macro)
            {
                LinearGelu(_fhPred0W!, _fhPred0B, zc, fhMid);
                Linear(_fhPred2W!, _fhPred2B, fhMid, fhVec);
                LinearGelu(_fhRes0W!, _fhRes0B, fhVec, fhResMid);
                Linear(_fhRes2W!, _fhRes2B, fhResMid, scalar);
                score += scalar[0];
            }

            logits[c] = score;
        }

        return logits;
    }

    ReadOnlySpan<float> GatherRef(int idx, int S, float[] ctx, float[] e)
    {
        if (idx < 0 || S <= 0) return _noneRef;
        return idx < S ? ctx.AsSpan(idx * _d, _d) : e.AsSpan((idx - S) * _d, _d);
    }

    float ValueSingle(PreparedStep p, float[] e, float[] g)
    {
        int d = _d;
        Span<float> top = stackalloc float[d];
        top.Clear();
        if (p.DrawToks.Count > 0)
        {
            foreach (int dt in p.DrawToks)
                AddInPlace(top, e.AsSpan(dt * d, d));
            ScaleInPlace(top, 1f / p.DrawToks.Count);
        }

        Span<float> x = stackalloc float[2 * d];
        g.CopyTo(x[..d]);
        Span<float> priv = x.Slice(d, d);
        Linear(_privProjW, _privProjB, top, priv);
        AddInPlace(priv, _privBase);

        Span<float> mid = stackalloc float[2 * d];
        Span<float> scalar = stackalloc float[1];
        if (p.Macro)
        {
            LinearGelu(_valMlpM0W, _valMlpM0B, x, mid);
            Linear(_valMlpM2W, _valMlpM2B, mid, scalar);
        }
        else
        {
            LinearGelu(_valMlp0W, _valMlp0B, x, mid);
            Linear(_valMlp2W, _valMlp2B, mid, scalar);
        }

        float val = scalar[0];
        if (_valueHl && _hlBeta != 0f)
        {
            Span<float> hlLogits = stackalloc float[_hlBins];
            if (p.Macro)
            {
                LinearGelu(_valHlM0W!, _valHlM0B, x, mid);
                Linear(_valHlM2W!, _valHlM2B, mid, hlLogits);
            }
            else
            {
                LinearGelu(_valHl0W!, _valHl0B, x, mid);
                Linear(_valHl2W!, _valHl2B, mid, hlLogits);
            }

            float maxZ = float.NegativeInfinity;
            for (int i = 0; i < _hlBins; i++) if (hlLogits[i] > maxZ) maxZ = hlLogits[i];
            float sumZ = 0f, vHl = 0f;
            for (int i = 0; i < _hlBins; i++)
            {
                hlLogits[i] = MathF.Exp(hlLogits[i] - maxZ);
                sumZ += hlLogits[i];
            }
            float invZ = 1f / sumZ;
            for (int i = 0; i < _hlBins; i++)
                vHl += (hlLogits[i] * invZ) * _hlCenters![i];

            val += _hlBeta * (vHl - val);
        }

        return val;
    }

    // -------------------------------------------------------------------------
    // Equivalence-Class Merging (exact port of sts2rl/greedy.py)
    // -------------------------------------------------------------------------

    static int Argmax(double[] probs)
    {
        int best = 0;
        for (int i = 1; i < probs.Length; i++)
            if (probs[i] > probs[best]) best = i;
        return best;
    }

    static int MergedArgmax(ParsedObs o, double[] probs)
    {
        int n = o.NumCands;
        if (n <= 1) return 0;

        var kids = new Dictionary<int, List<int>>();
        for (int k = 0; k < o.NumTokens; k++)
        {
            int owner = o.TokInts[k * 5 + 3];
            if (owner >= 0)
            {
                if (!kids.TryGetValue(owner, out var list))
                    kids[owner] = list = new List<int>(2);
                list.Add(k);
            }
        }

        var first = new Dictionary<string, int>(n, StringComparer.Ordinal);
        int[] cls = new int[n];
        bool anyMerged = false;

        for (int c = 0; c < n; c++)
        {
            int a = o.CandInts[c * 5 + 1];
            int b = o.CandInts[c * 5 + 2];
            string key = BuildCandEquivKey(o, c, a, b, kids);
            if (first.TryGetValue(key, out int existing))
            {
                cls[c] = existing;
                anyMerged = true;
            }
            else
            {
                first[key] = c;
                cls[c] = c;
            }
        }

        if (!anyMerged) return Argmax(probs);

        double[] tot = new double[n];
        for (int c = 0; c < n; c++)
            tot[cls[c]] += probs[c];
        return Argmax(tot);
    }

    static string BuildCandEquivKey(ParsedObs o, int c, int a, int b, Dictionary<int, List<int>> kids)
    {
        var sb = new StringBuilder(256);
        sb.Append(o.CandInts[c * 5]).Append('|').Append(o.CandInts[c * 5 + 3]).Append('|').Append(b).Append('|');
        AppendFloatBits(sb, o.RawCandFeats.AsSpan(c * CandFeatsMax, CandFeatsMax));
        sb.Append('|');
        if (a >= 0 && a < o.NumTokens)
        {
            sb.Append(o.TokInts[a * 5]).Append(',')
              .Append(o.TokInts[a * 5 + 1]).Append(',')
              .Append(o.TokInts[a * 5 + 2]).Append(',')
              .Append(o.TokInts[a * 5 + 4]).Append(':');
            AppendFloatBits(sb, o.RawTokFeats.AsSpan(a * TokFeatsMax, TokFeatsMax));
            if (kids.TryGetValue(a, out var kl) && kl.Count > 0)
            {
                var attStrings = new List<string>(kl.Count);
                foreach (int k in kl)
                {
                    var kb = new StringBuilder(128);
                    kb.Append(o.TokInts[k * 5]).Append(',').Append(o.TokInts[k * 5 + 1]).Append(':');
                    AppendFloatBits(kb, o.RawTokFeats.AsSpan(k * TokFeatsMax, TokFeatsMax));
                    attStrings.Add(kb.ToString());
                }
                attStrings.Sort(StringComparer.Ordinal);
                foreach (string s in attStrings) sb.Append(';').Append(s);
            }
        }
        return sb.ToString();
    }

    static void AppendFloatBits(StringBuilder sb, ReadOnlySpan<float> vals)
    {
        for (int i = 0; i < vals.Length; i++)
        {
            // Canonicalize -0.0f vs +0.0f? Note: Python greedy.py uses raw float32 .tobytes(),
            // and Observation.cs emits +0.0f; BitConverter.SingleToInt32Bits matches .tobytes() bit-for-bit.
            sb.Append(BitConverter.SingleToUInt32Bits(vals[i]).ToString("X8"));
        }
    }

    // -------------------------------------------------------------------------
    // SIMD Tensor Kernels (System.Numerics.Vector<float> — AVX2 / AVX-512 / NEON)
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void AddVocabRow(Span<float> dst, float[] table, int id)
    {
        int clamped = id < 0 ? 0 : id;
        if (clamped < _vocabRows)
            AddInPlace(dst, table.AsSpan(clamped * _d, _d));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int i = 0, n = a.Length;
        int w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && n >= w)
        {
            var acc0 = Vector<float>.Zero;
            var acc1 = Vector<float>.Zero;
            int limit2 = n - (w * 2);
            for (; i <= limit2; i += w * 2)
            {
                acc0 += new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w));
                acc1 += new Vector<float>(a.Slice(i + w, w)) * new Vector<float>(b.Slice(i + w, w));
            }
            acc0 += acc1;
            for (; i <= n - w; i += w)
                acc0 += new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w));
            float sum = Vector.Sum(acc0);
            for (; i < n; i++) sum += a[i] * b[i];
            return sum;
        }
        float s = 0f;
        for (; i < n; i++) s += a[i] * b[i];
        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void AddInPlace(Span<float> dst, ReadOnlySpan<float> src)
    {
        int i = 0, n = dst.Length, w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && n >= w)
        {
            for (; i <= n - w; i += w)
                (new Vector<float>(dst.Slice(i, w)) + new Vector<float>(src.Slice(i, w))).CopyTo(dst.Slice(i, w));
        }
        for (; i < n; i++) dst[i] += src[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void AddScaledInPlace(Span<float> dst, ReadOnlySpan<float> src, float scale)
    {
        if (scale == 0f) return;
        int i = 0, n = dst.Length, w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && n >= w)
        {
            var sv = new Vector<float>(scale);
            for (; i <= n - w; i += w)
                (new Vector<float>(dst.Slice(i, w)) + new Vector<float>(src.Slice(i, w)) * sv).CopyTo(dst.Slice(i, w));
        }
        for (; i < n; i++) dst[i] += src[i] * scale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void MulInPlace(Span<float> dst, ReadOnlySpan<float> gate)
    {
        int i = 0, n = dst.Length, w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && n >= w)
        {
            for (; i <= n - w; i += w)
                (new Vector<float>(dst.Slice(i, w)) * new Vector<float>(gate.Slice(i, w))).CopyTo(dst.Slice(i, w));
        }
        for (; i < n; i++) dst[i] *= gate[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void AddMulInPlace(Span<float> dst, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int i = 0, n = dst.Length, w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && n >= w)
        {
            for (; i <= n - w; i += w)
                (new Vector<float>(dst.Slice(i, w)) + new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w))).CopyTo(dst.Slice(i, w));
        }
        for (; i < n; i++) dst[i] += a[i] * b[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ScaleInPlace(Span<float> dst, float scale)
    {
        int i = 0, n = dst.Length, w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && n >= w)
        {
            var sv = new Vector<float>(scale);
            for (; i <= n - w; i += w)
                (new Vector<float>(dst.Slice(i, w)) * sv).CopyTo(dst.Slice(i, w));
        }
        for (; i < n; i++) dst[i] *= scale;
    }

    static void Linear(float[] weight, float[]? bias, ReadOnlySpan<float> x, Span<float> y)
    {
        int inDim = x.Length;
        int outDim = y.Length;
        ReadOnlySpan<float> wSpan = weight;
        for (int r = 0; r < outDim; r++)
            y[r] = Dot(wSpan.Slice(r * inDim, inDim), x) + (bias != null ? bias[r] : 0f);
    }

    static void LinearAdd(float[] weight, ReadOnlySpan<float> x, Span<float> y)
    {
        // Fast skip if x is all zeros (common for sparse versioned feature splits!)
        bool nonZero = false;
        for (int i = 0; i < x.Length; i++) { if (x[i] != 0f) { nonZero = true; break; } }
        if (!nonZero) return;

        int inDim = x.Length;
        int outDim = y.Length;
        ReadOnlySpan<float> wSpan = weight;
        for (int r = 0; r < outDim; r++)
            y[r] += Dot(wSpan.Slice(r * inDim, inDim), x);
    }

    static void LinearAddWithBias(float[] weight, float[] bias, ReadOnlySpan<float> x, Span<float> y)
    {
        int inDim = x.Length;
        int outDim = y.Length;
        ReadOnlySpan<float> wSpan = weight;
        for (int r = 0; r < outDim; r++)
            y[r] += Dot(wSpan.Slice(r * inDim, inDim), x) + bias[r];
    }

    static void LinearGelu(float[] weight, float[]? bias, ReadOnlySpan<float> x, Span<float> y)
    {
        int inDim = x.Length;
        int outDim = y.Length;
        ReadOnlySpan<float> wSpan = weight;
        for (int r = 0; r < outDim; r++)
        {
            float z = Dot(wSpan.Slice(r * inDim, inDim), x) + (bias != null ? bias[r] : 0f);
            y[r] = Gelu(z);
        }
    }

    static void LinearRelu(float[] weight, float[] bias, ReadOnlySpan<float> x, Span<float> y)
    {
        int inDim = x.Length;
        int outDim = y.Length;
        ReadOnlySpan<float> wSpan = weight;
        for (int r = 0; r < outDim; r++)
        {
            float z = Dot(wSpan.Slice(r * inDim, inDim), x) + bias[r];
            y[r] = z > 0f ? z : 0f;
        }
    }

    static void LayerNorm(ReadOnlySpan<float> x, float[] weight, float[] bias, Span<float> y)
    {
        int n = x.Length;
        float sum = 0f;
        for (int i = 0; i < n; i++) sum += x[i];
        float mean = sum / n;
        float varSum = 0f;
        for (int i = 0; i < n; i++)
        {
            float d = x[i] - mean;
            varSum += d * d;
        }
        float invStd = 1f / MathF.Sqrt(varSum / n + 1e-5f);
        for (int i = 0; i < n; i++)
            y[i] = (x[i] - mean) * invStd * weight[i] + bias[i];
    }

    static void LayerNormInPlace(Span<float> x, float[] weight, float[] bias)
    {
        int n = x.Length;
        float sum = 0f;
        for (int i = 0; i < n; i++) sum += x[i];
        float mean = sum / n;
        float varSum = 0f;
        for (int i = 0; i < n; i++)
        {
            float d = x[i] - mean;
            varSum += d * d;
        }
        float invStd = 1f / MathF.Sqrt(varSum / n + 1e-5f);
        for (int i = 0; i < n; i++)
            x[i] = (x[i] - mean) * invStd * weight[i] + bias[i];
    }

    /// <summary>Exact Gaussian Error Linear Unit matching PyTorch nn.GELU(approximate='none').</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Gelu(float x)
    {
        const float InvSqrt2 = 0.7071067811865475244f;
        return 0.5f * x * (1f + Erf(x * InvSqrt2));
    }

    /// <summary>Abramowitz &amp; Stegun 7.1.26 rational approximation (max error &lt; 1.5e-7).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Erf(float x)
    {
        if (x >= 4.0f) return 1.0f;
        if (x <= -4.0f) return -1.0f;
        float sign = x < 0f ? -1f : 1f;
        float ax = MathF.Abs(x);
        float t = 1.0f / (1.0f + 0.3275911f * ax);
        float poly = (((((1.061405429f * t - 1.453152027f) * t) + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t;
        float y = 1.0f - poly * MathF.Exp(-ax * ax);
        return sign * y;
    }
}

/// <summary>
/// Thread-safe background loader &amp; asynchronous inference dispatcher for built-in .s2nn character models.
/// Keeps the Godot UI render thread 100% stutter-free.
/// </summary>
public sealed class NativeModelManager
{
    readonly string _modelsDir;
    readonly ConcurrentDictionary<string, NativeModel> _loaded = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Task<NativeModel?>> _loading = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentQueue<PolicyReply> _replyQueue = new();

    volatile string _statusText = "";
    volatile string _lastError = "";
    public string StatusText => _statusText;
    public string LastError => _lastError;

    public NativeModelManager(string modDir)
    {
        _modelsDir = Path.Combine(modDir, "models");
    }

    public bool HasAnyModelFile()
    {
        try { return Directory.Exists(_modelsDir) && Directory.EnumerateFiles(_modelsDir, "*.s2nn").Any(); }
        catch { return false; }
    }

    public NativeModel? TryGetLoaded(string character)
    {
        string key = NormalizeChar(character);
        if (_loaded.TryGetValue(key, out var m)) return m;
        EnsureLoading(key);
        return null;
    }

    public int? GetObsFormatForCharacter(string character)
    {
        string key = NormalizeChar(character);
        if (_loaded.TryGetValue(key, out var m)) return m.ObsFormat;
        return key switch
        {
            "FOOL" => 27,
            "IRONCLAD" => 28,
            "NECROBINDER" => 28,
            "SILENT" => 23,
            "REGENT" => 20,
            "DEFECT" => 19,
            _ => null,
        };
    }

    public void PreloadCharacter(string character)
    {
        EnsureLoading(NormalizeChar(character));
    }

    Task<NativeModel?> EnsureLoading(string key)
    {
        return _loading.GetOrAdd(key, ch => Task.Run(() =>
        {
            string path = Path.Combine(_modelsDir, $"{ch}.s2nn");
            if (!File.Exists(path))
            {
                // Fallback to FOOL.s2nn or any .s2nn if playing a modded character
                var any = Directory.Exists(_modelsDir)
                    ? Directory.EnumerateFiles(_modelsDir, "*.s2nn").FirstOrDefault()
                    : null;
                if (any == null)
                {
                    _statusText = $"未找到内置模型 ({ch}.s2nn)";
                    return null;
                }
                path = any;
            }

            try
            {
                _statusText = $"正在加载内置模型 {Path.GetFileNameWithoutExtension(path)}...";
                var sw = Stopwatch.StartNew();
                var model = NativeModel.Load(path);
                _loaded[ch] = model;
                _statusText = $"内置模型就绪: {model.Character} v{model.CkptVersion}";
                BridgeLog.Info($"Loaded NativeModel {path} (char={model.Character}, v{model.CkptVersion}, fmt={model.ObsFormat}) in {sw.Elapsed.TotalMilliseconds:0.0}ms");
                return model;
            }
            catch (Exception e)
            {
                _statusText = $"内置模型加载失败: {e.Message}";
                BridgeLog.Error($"Failed to load NativeModel {path}: {e}");
                return null;
            }
        }));
    }

    public void EnqueueInference(int seq, string character, IReadOnlyList<string> choices, byte[] rawFrame)
    {
        string key = NormalizeChar(character);
        var choicesCopy = choices.ToArray();
        Task.Run(async () =>
        {
            try
            {
                if (!_loaded.TryGetValue(key, out var model))
                    model = await EnsureLoading(key);
                if (model == null)
                {
                    _lastError = _statusText.Length > 0 ? _statusText : $"未能加载角色模型 ({key})";
                    return;
                }
                _lastError = "";
                var reply = model.Decide(seq, choicesCopy, rawFrame);
                _replyQueue.Enqueue(reply);
            }
            catch (Exception e)
            {
                _lastError = $"推理异常: {e.GetBaseException().Message}";
                BridgeLog.Error($"NativeModel inference error on seq={seq}: {e}");
            }
        });
    }

    public bool TryDequeueReply(out PolicyReply? reply) => _replyQueue.TryDequeue(out reply);

    static string NormalizeChar(string character)
    {
        if (string.IsNullOrWhiteSpace(character)) return "DEFECT";
        string u = character.Trim().ToUpperInvariant();
        if (u.StartsWith("CHARACTER.")) u = u["CHARACTER.".Length..];
        return u;
    }
}
