using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using InteractiveSoftware.ArcadiaEffects;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// Регрессия картинки: кадры, отрисованные пакетом, сравниваются с кадрами самого редактора
/// Arcaidia Effector (Tests/Data/Golden, «эффект@время.png», уменьшены в 4 раза площадным
/// усреднением, поверх чёрного фона).
/// </summary>
public class RenderRegressionTests
{
    private const int Down = 4;
    // пороги в единицах 0..255 по уменьшенному кадру
    private const double MaxMean = 1.0;
    private const double MaxP99 = 10;

    private static string DataDir([CallerFilePath] string self = "") => Path.Combine(Path.GetDirectoryName(self), "Data");

    public static IEnumerable<string> Goldens()
    {
        string dir = Path.Combine(DataDir(), "Golden");
        foreach (string f in Directory.GetFiles(dir, "*.png")) yield return Path.GetFileNameWithoutExtension(f);
    }

    [TestCaseSource(nameof(Goldens))]
    public void FrameMatchesEditor(string golden)
    {
        int at = golden.LastIndexOf('@');
        string effect = golden.Substring(0, at);
        double t = double.Parse(golden.Substring(at + 1), CultureInfo.InvariantCulture);

        var data = ArcadiaEffectData.Parse(File.ReadAllText(Path.Combine(DataDir(), effect + ".json")));
        using (var r = new ArcadiaEffectRenderer(data, ArcadiaQuality.High))
        {
            r.Render(t);
            Color32[] got = ReadDownscaled(r.Output, out int w, out int h);
            var refTex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            refTex.LoadImage(File.ReadAllBytes(Path.Combine(DataDir(), "Golden", golden + ".png")));
            Assert.AreEqual(refTex.width, w, "width");
            Assert.AreEqual(refTex.height, h, "height");
            Color32[] exp = refTex.GetPixels32();
            Object.DestroyImmediate(refTex);

            var diffs = new List<int>(exp.Length * 3);
            double sum = 0;
            for (int i = 0; i < exp.Length; i++)
            {
                int dr = Mathf.Abs(exp[i].r - got[i].r), dg = Mathf.Abs(exp[i].g - got[i].g), db = Mathf.Abs(exp[i].b - got[i].b);
                diffs.Add(dr); diffs.Add(dg); diffs.Add(db);
                sum += dr + dg + db;
            }
            diffs.Sort();
            double mean = sum / diffs.Count;
            int p99 = diffs[(int)(diffs.Count * 0.99)];
            TestContext.WriteLine($"{golden}: mean={mean:0.000} p99={p99}");
            Assert.Less(mean, MaxMean, "mean abs diff");
            Assert.LessOrEqual(p99, MaxP99, "p99 abs diff");
        }
    }

    /// <summary>Прочитать композицию поверх чёрного и уменьшить в Down раз площадным усреднением.</summary>
    internal static Color32[] ReadDownscaled(RenderTexture rt, out int w, out int h)
    {
        var tmp = RenderTexture.GetTemporary(rt.width, rt.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(rt, tmp);
        var prev = RenderTexture.active;
        RenderTexture.active = tmp;
        var tex = new Texture2D(tmp.width, tmp.height, TextureFormat.RGBA32, false, true);
        tex.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(tmp);
        Color32[] px = tex.GetPixels32();
        int sw = tex.width, sh = tex.height;
        Object.DestroyImmediate(tex);

        // строки Texture2D снизу вверх, у эталона (PNG) — тоже после LoadImage: сравниваем в одной системе
        w = sw / Down;
        h = sh / Down;
        var o = new Color32[w * h];
        // площадное усреднение по блокам от ВЕРХНЕГО края (как у эталона, обрезанного снизу)
        int yOff = sh - h * Down;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int r = 0, g = 0, b = 0;
            for (int dy = 0; dy < Down; dy++)
            for (int dx = 0; dx < Down; dx++)
            {
                Color32 c = px[(yOff + y * Down + dy) * sw + x * Down + dx];
                r += c.r; g += c.g; b += c.b;
            }
            const float n = Down * Down;
            o[y * w + x] = new Color32((byte)Mathf.FloorToInt(r / n + 0.5f), (byte)Mathf.FloorToInt(g / n + 0.5f), (byte)Mathf.FloorToInt(b / n + 0.5f), 255);
        }
        return o;
    }
}
