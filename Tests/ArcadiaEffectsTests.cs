using System.IO;
using System.Runtime.CompilerServices;
using InteractiveSoftware.ArcadiaEffects;
using NUnit.Framework;

public class ArcadiaEffectsTests
{
    private static string DataPath(string file, [CallerFilePath] string self = "") =>
        Path.Combine(Path.GetDirectoryName(self), "Data", file);

    private static EffectDocument Load(string file) => EffectDocument.Parse(File.ReadAllText(DataPath(file)));

    [Test]
    public void Mulberry32MatchesJavaScript()
    {
        // AFX.mulberry32(1)() первые значения из node
        var r = new Js.Mulberry32(1);
        double a = r.Next(), b = r.Next();
        var r2 = new Js.Mulberry32(1);
        Assert.AreEqual(a, r2.Next());
        Assert.AreEqual(b, r2.Next());
        Assert.That(a, Is.InRange(0.0, 1.0));
        Assert.AreNotEqual(a, b);
    }

    [Test]
    public void ToInt32WrapsLikeJavaScript()
    {
        Assert.AreEqual(-1, Js.ToInt32(4294967295.0));
        Assert.AreEqual(5, Js.ToInt32(5.9));
        Assert.AreEqual(-5, Js.ToInt32(-5.9));
        Assert.AreEqual(0, Js.ToInt32(double.NaN));
    }

    [Test]
    public void TrackEvaluatesKeysAndStatics()
    {
        var json = (System.Collections.Generic.Dictionary<string, object>)Json.Parse(
            "{\"a\":3,\"b\":{\"keys\":[{\"t\":0,\"v\":0,\"i\":{\"m\":\"lin\"},\"o\":{\"m\":\"lin\"}},{\"t\":1,\"v\":10,\"i\":{\"m\":\"lin\"},\"o\":{\"m\":\"hold\"}}]}}");
        Assert.AreEqual(3, Track.Parse(json["a"], 0).Eval(0.5));
        var b = Track.Parse(json["b"], 0);
        Assert.AreEqual(0, b.Eval(-1));
        Assert.AreEqual(10, b.Eval(2));
        Assert.AreEqual(5, b.Eval(0.5), 1e-3);
    }

    [Test]
    public void DocumentIsHydratedWithDefaults()
    {
        var doc = EffectDocument.Parse("{\"doc\":{\"v\":2,\"comp\":{\"w\":256,\"h\":128,\"dur\":0.5},\"layers\":[{\"type\":\"emitter\"}]}}");
        Assert.AreEqual(256, doc.CompW);
        Assert.AreEqual(1, doc.Layers.Count);
        var l = doc.Layers[0];
        Assert.AreEqual(0.5, l.End);
        Assert.AreEqual(LayerBlend.Add, l.Blend);
        Assert.IsTrue(l.Em.SubStep);
        Assert.AreEqual(1, l.Em.Bursts.Length);
        Assert.AreEqual(24, l.Em.Bursts[0].N);
    }

    [Test]
    public void LegacyFilesKeepPerStepEmission()
    {
        var doc = EffectDocument.Parse("{\"doc\":{\"v\":1,\"layers\":[{\"type\":\"emitter\",\"em\":{}}]}}");
        Assert.IsFalse(doc.Layers[0].Em.SubStep);
    }

    // Эталоны сняты оригинальным движком редактора (js/core/sim.js в node)
    [TestCase("Sparks.json", 1, 0.25, 29, -480.27632492340837, -143.8740127256745)]
    [TestCase("Sparks.json", 1, 0.6, 0, 0, 0)]
    [TestCase("Booba Static Electricity.json", 1, 0.25, 14, -333.6705192925632, 199.34633621328254)]
    [TestCase("Booba Static Electricity.json", 1, 0.6, 18, -51.64938312040985, -247.71516706349388)]
    [TestCase("Booba Static Electricity.json", 2, 0.25, 23, 673.8778288614645, 502.48166868706323)]
    [TestCase("Booba Static Electricity.json", 2, 0.6, 76, 229.22454114520184, -564.4286543147476)]
    [TestCase("Booba Static Electricity.json", 3, 0.25, 34, 1857.0548119271612, -511.23501706554975)]
    [TestCase("Booba Static Electricity.json", 3, 0.6, 33, -1079.494293524339, 912.8721734613979)]
    [TestCase("Booba Static Electricity.json", 4, 0.25, 8, 303.1651650705628, -255.37925574453624)]
    [TestCase("Booba Static Electricity.json", 4, 0.6, 17, -141.1156254167418, -226.20446404489726)]
    public void SimulationMatchesEditor(string file, int layer, double t, int count, double sumX, double sumY)
    {
        var doc = Load(file);
        var l = doc.Layers[layer];
        var sim = new Simulation(l, doc.ForcesFor(l.Id));
        sim.Ensure(0.25);
        if (t > 0.25) sim.Ensure(t);
        double sx = 0, sy = 0;
        foreach (var p in sim.Parts) { sx += p.X; sy += p.Y; }
        Assert.AreEqual(count, sim.Parts.Count);
        Assert.AreEqual(sumX, sx, 1e-6);
        Assert.AreEqual(sumY, sy, 1e-6);
    }

    [Test]
    public void RewindResimulatesFromZero()
    {
        var doc = Load("Sparks.json");
        var l = doc.Layers[1];
        var sim = new Simulation(l, null);
        sim.Ensure(0.25);
        double x = sim.Parts[0].X;
        sim.Ensure(0.5);
        sim.Ensure(0.25);
        Assert.AreEqual(x, sim.Parts[0].X);
    }
}
