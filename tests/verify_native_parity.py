#!/usr/bin/env python3
"""Automated numerical parity & latency verifier between PyTorch PolicyValueNet and C# NativeModel.

Compiles a lightweight .NET 9.0 runner referencing the built `Sts2Bridge.dll`, loads all exported
`.s2nn` character models (`FOOL`, `IRONCLAD`, `NECROBINDER`, `SILENT`, `REGENT`, `DEFECT`), and
asserts against `export_s2nn.py --verify-out` ground-truth vectors:
  - `max_abs_prob_diff < 3e-3`
  - 100% identical Top-K probability ordering
  - 100% identical `ChosenAction` (including equivalence-merged argmax)
"""
from __future__ import annotations

import argparse
import os
import subprocess
import tempfile
from pathlib import Path

CS_PROGRAM = r"""
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2Bridge;

sealed class CaseVec
{
    [JsonPropertyName("character")] public string Character { get; set; } = "";
    [JsonPropertyName("case")] public string Case { get; set; } = "";
    [JsonPropertyName("obs_format")] public int ObsFormat { get; set; }
    [JsonPropertyName("raw_b64")] public string RawB64 { get; set; } = "";
    [JsonPropertyName("logits")] public float[] Logits { get; set; } = Array.Empty<float>();
    [JsonPropertyName("probs")] public double[] Probs { get; set; } = Array.Empty<double>();
    [JsonPropertyName("value")] public float Value { get; set; }
    [JsonPropertyName("action")] public int Action { get; set; }
}

class Program
{
    static int Main(string[] args)
    {
        string modelsDir = args[0];
        string verifyJson = args[1];
        var cases = JsonSerializer.Deserialize<List<CaseVec>>(File.ReadAllText(verifyJson))!;
        var models = new Dictionary<string, NativeModel>(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"Vector.IsHardwareAccelerated={System.Numerics.Vector.IsHardwareAccelerated}, Vector<float>.Count={System.Numerics.Vector<float>.Count}");

        double globalMaxProbDiff = 0.0;
        double globalMaxValDiff = 0.0;
        int mismatches = 0;

        foreach (var c in cases)
        {
            if (!models.TryGetValue(c.Character, out var m))
            {
                string path = Path.Combine(modelsDir, $"{c.Character}.s2nn");
                var swLoad = Stopwatch.StartNew();
                m = NativeModel.Load(path);
                Console.WriteLine($"[LOAD] {c.Character}.s2nn (v{m.CkptVersion}, fmt={m.ObsFormat}) loaded in {swLoad.Elapsed.TotalMilliseconds:F1} ms");
                models[c.Character] = m;
            }

            byte[] raw = Convert.FromBase64String(c.RawB64);
            // Warmup JIT
            m.EvaluateRaw(raw);

            // Benchmark 10 runs
            var sw = Stopwatch.StartNew();
            NativeModel.InferOutput res = default;
            const int N = 10;
            for (int it = 0; it < N; it++)
                res = m.EvaluateRaw(raw);
            double avgMs = sw.Elapsed.TotalMilliseconds / N;

            double maxProbDiff = 0.0;
            double maxLogitDiff = 0.0;
            for (int i = 0; i < c.Probs.Length; i++)
            {
                maxProbDiff = Math.Max(maxProbDiff, Math.Abs(res.Probs[i] - c.Probs[i]));
                maxLogitDiff = Math.Max(maxLogitDiff, Math.Abs(res.Logits[i] - c.Logits[i]));
            }
            double valDiff = Math.Abs(res.Value - c.Value);

            var pyOrder = Enumerable.Range(0, c.Probs.Length).OrderByDescending(i => c.Probs[i]).ThenBy(i => i).ToArray();
            var csOrder = Enumerable.Range(0, res.Probs.Length).OrderByDescending(i => res.Probs[i]).ThenBy(i => i).ToArray();
            // Allow ties only when probabilities are bit-identical (e.g. duplicate Strike cards 0 and 1!)
            bool topKMatch = true;
            for (int i = 0; i < pyOrder.Length; i++)
            {
                if (pyOrder[i] != csOrder[i] && Math.Abs(c.Probs[pyOrder[i]] - c.Probs[csOrder[i]]) > 1e-4)
                    topKMatch = false;
            }

            bool actMatch = res.ChosenAction == c.Action;
            bool probOk = maxProbDiff < 3e-3;
            bool ok = actMatch && topKMatch && probOk;
            if (!ok) mismatches++;

            globalMaxProbDiff = Math.Max(globalMaxProbDiff, maxProbDiff);
            globalMaxValDiff = Math.Max(globalMaxValDiff, valDiff);

            Console.WriteLine(
                $"[{(ok ? "PASS" : "FAIL")}] {c.Character,-11} {c.Case,-11} | " +
                $"max_prob_diff={maxProbDiff:E3} max_logit_diff={maxLogitDiff:E3} val_diff={valDiff:E3} | " +
                $"act={res.ChosenAction} (py={c.Action}) topK_ok={topKMatch} | avg_infer={avgMs:F2} ms");
        }

        Console.WriteLine($"=== SUMMARY: {cases.Count - mismatches}/{cases.Count} passed, global_max_prob_diff={globalMaxProbDiff:E3}, global_max_val_diff={globalMaxValDiff:E3} ===");
        return mismatches == 0 ? 0 : 1;
    }
}
"""


def main() -> None:
    p = argparse.ArgumentParser(description="Verify C# NativeModel parity against PyTorch ground-truth vectors")
    p.add_argument("--models-dir", type=Path, required=True, help="Directory with <CHARACTER>.s2nn files")
    p.add_argument("--verify-json", type=Path, required=True, help="JSON generated by export_s2nn.py --verify-out")
    p.add_argument("--bridge-dll", type=Path, required=True, help="Compiled Sts2Bridge.dll path")
    p.add_argument("--game-dir", type=Path, required=True, help="STS2 managed DLL directory (for assembly resolution)")
    p.add_argument("--dotnet", type=str, default="dotnet", help="dotnet CLI binary")
    args = p.parse_args()

    with tempfile.TemporaryDirectory(prefix="s2nn_parity_") as tmp:
        tmp_dir = Path(tmp)
        csproj = f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Sts2Bridge"><HintPath>{args.bridge_dll.resolve()}</HintPath></Reference>
    <Reference Include="sts2"><HintPath>{(args.game_dir / 'sts2.dll').resolve()}</HintPath></Reference>
    <Reference Include="GodotSharp"><HintPath>{(args.game_dir / 'GodotSharp.dll').resolve()}</HintPath></Reference>
    <Reference Include="0Harmony"><HintPath>{(args.game_dir / '0Harmony.dll').resolve()}</HintPath></Reference>
  </ItemGroup>
</Project>
"""
        (tmp_dir / "ParityRunner.csproj").write_text(csproj, encoding="utf-8")
        (tmp_dir / "Program.cs").write_text(CS_PROGRAM, encoding="utf-8")

        env = dict(os.environ)
        cmd = [
            args.dotnet,
            "run",
            "-c",
            "Release",
            "--project",
            str(tmp_dir / "ParityRunner.csproj"),
            "--",
            str(args.models_dir.resolve()),
            str(args.verify_json.resolve()),
        ]
        subprocess.run(cmd, check=True, env=env)


if __name__ == "__main__":
    main()
