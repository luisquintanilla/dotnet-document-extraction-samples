using Microsoft.ML.OnnxRuntime;
using OcriaNano.DocumentExtraction.Onnx;

namespace OcriaNano.DocumentExtraction.Tests;

public sealed class ModelSmokeTests
{
    [Fact]
    public void CoreModelsLoadWithExpectedIo()
    {
        string dir = TestModels.Require();
        using SessionPool pool = new(dir, verifyHashes: true, sessionOptions: null);

        AssertIo(pool.Get("det.onnx"), ["x"], ["prob"]);
        AssertIo(pool.Get("router.onnx"), ["x"], ["group", "flip"]);
        AssertIo(pool.Get("rec_latin.onnx"), ["x"], ["ids", "conf"]);
        AssertIo(pool.Get("tablenet.onnx"), ["feats", "mask", "pairf", "image", "boxes", "valid"], ["node", "pair", "cls"]);
    }

    [Fact]
    public void DetectorRunsOnBlankPage()
    {
        string dir = TestModels.Require();
        using SessionPool pool = new(dir, verifyHashes: false, sessionOptions: null);
        using OrtInput<float> x = new(1, 3, 64, 96);
        x.Span.Fill(1f);
        using var outputs = pool.Get("det.onnx").Run([("x", x.Value)], "prob");
        // The detector predicts at stride 2; LiteParse derives the stride from the output shape.
        Assert.Equal([1L, 1, 32, 48], outputs.First().GetShape());
        Assert.True(outputs.First().AsTensorSpan<float>()[0, 0, 10, 10] < 0.5f);
    }

    [Fact]
    public void RouterAndRecognizerRun()
    {
        string dir = TestModels.Require();
        using SessionPool pool = new(dir, verifyHashes: false, sessionOptions: null);
        using OrtInput<float> line = new(2, 1, 32, 512);
        line.Span.Fill(1f);
        using (var r = pool.Get("router.onnx").Run([("x", line.Value)], "group", "flip"))
        {
            Assert.Equal([2L, 4], r.ElementAt(0).GetShape());
            Assert.Equal([2L, 2], r.ElementAt(1).GetShape());
        }

        using OrtInput<float> rec = new(2, 1, 32, 256);
        rec.Span.Fill(1f);
        using var o = pool.Get("rec_latin.onnx").Run([("x", rec.Value)], "ids", "conf");
        Assert.Equal(2L, o.ElementAt(0).GetShape()[0]);
        Assert.Equal(o.ElementAt(0).GetShape(), o.ElementAt(1).GetShape());
    }

    private static void AssertIo(InferenceSession session, string[] inputs, string[] outputs)
    {
        Assert.Equal(inputs, session.InputNames);
        Assert.Equal(outputs, session.OutputNames);
    }
}
