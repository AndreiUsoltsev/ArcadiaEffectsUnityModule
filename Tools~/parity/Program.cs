// Паритет симуляции: C#-порт (Runtime/Core) против дампа оригинального js/core редактора.
// Запуск — см. README.md рядом.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using InteractiveSoftware.ArcadiaEffects;

internal static class Parity
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: dotnet run -- <editor library dir> <js dump file>");
            return 2;
        }
        string lib = args[0];
        string[] refLines = File.ReadAllLines(args[1]);
        var docs = new Dictionary<string, EffectDocument>();
        var sims = new Dictionary<string, Simulation>();
        int exact = 0, close = 0, countMismatch = 0, total = 0;
        double maxErr = 0;
        string worst = "";

        foreach (string line in refLines)
        {
            // файл|слой|шаг|число частиц|x,y,vx,vy,age,life,size0,rot,tier,gen,vi,hist ...
            string[] parts = line.Split('|');
            string f = parts[0];
            int li = int.Parse(parts[1]), s = int.Parse(parts[2]), n = int.Parse(parts[3]);
            if (!docs.TryGetValue(f, out EffectDocument doc))
                docs[f] = doc = EffectDocument.Parse(File.ReadAllText(Path.Combine(lib, f)));
            Layer l = doc.Layers[li];
            string key = f + "|" + li;
            if (!sims.TryGetValue(key, out Simulation sim)) sims[key] = sim = new Simulation(l, doc.ForcesFor(l.Id));
            double life = l.Pt.Life != 0 ? l.Pt.Life : 1;
            double dur = Math.Max(0.05, (l.End - l.Start) + life * 1.6);
            sim.Ensure(dur * s / 24);
            total++;
            if (sim.Parts.Count != n)
            {
                countMismatch++;
                Console.WriteLine($"COUNT {key} s={s} js={n} cs={sim.Parts.Count}");
                continue;
            }
            double[] vals = parts[4].Length > 0
                ? parts[4].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()
                : Array.Empty<double>();
            double err = 0;
            bool bit = true;
            for (int i = 0; i < n; i++)
            {
                Particle p = sim.Parts[i];
                double[] cs = { p.X, p.Y, p.Vx, p.Vy, p.Age, p.Life, p.Size0, p.Rot, p.Tier, p.Gen, p.Vi, p.Hist != null ? p.Hist.Count : -1 };
                for (int k = 0; k < 12; k++)
                {
                    double js = vals[i * 12 + k];
                    if (js != cs[k]) bit = false;
                    err = Math.Max(err, Math.Abs(js - cs[k]) / Math.Max(1, Math.Abs(js)));
                }
            }
            if (bit) exact++;
            else close++;
            if (err > maxErr) { maxErr = err; worst = key + " s=" + s; }
        }
        Console.WriteLine($"total={total} bitExact={exact} notExact={close} countMismatch={countMismatch} maxRelErr={maxErr:E3} worst={worst}");
        // пороги: числа частиц обязаны совпасть, координаты — до последних бит libm
        return countMismatch == 0 && maxErr < 1e-6 ? 0 : 1;
    }
}
