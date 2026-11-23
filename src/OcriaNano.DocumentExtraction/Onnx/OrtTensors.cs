using System.Buffers;
using System.Numerics.Tensors;
using Microsoft.ML.OnnxRuntime;

namespace OcriaNano.DocumentExtraction.Onnx;

/// <summary>
/// A model input backed by a pooled array. Write it through <see cref="Span"/> or <see cref="AsTensorSpan"/>, then pass
/// <see cref="Value"/> to ONNX Runtime; the <see cref="OrtValue"/> wraps the array without copying.
/// </summary>
/// <remarks>
/// This is our own bridge to System.Numerics.Tensors on purpose: ONNX Runtime's <c>Tensor&lt;T&gt;</c> interop is
/// marked experimental (SYSLIB5001) and was compiled against an older tensor API.
/// </remarks>
internal sealed class OrtInput<T> : IDisposable
    where T : unmanaged
{
    private readonly T[] _array;
    private readonly nint[] _lengths;
    private OrtValue? _value;

    public OrtInput(params ReadOnlySpan<long> shape)
    {
        long count = 1;
        _lengths = new nint[shape.Length];
        for (int i = 0; i < shape.Length; i++)
        {
            count *= shape[i];
            _lengths[i] = (nint)shape[i];
        }

        Length = checked((int)count);
        Shape = shape.ToArray();
        _array = ArrayPool<T>.Shared.Rent(Math.Max(Length, 1));
        Span.Clear();
    }

    public int Length { get; }

    public long[] Shape { get; }

    public Span<T> Span => _array.AsSpan(0, Length);

    public TensorSpan<T> AsTensorSpan() => new(Span, _lengths);

    public OrtValue Value => _value ??= OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, _array.AsMemory(0, Length), Shape);

    public void Dispose()
    {
        _value?.Dispose();
        _value = null;
        ArrayPool<T>.Shared.Return(_array);
    }
}

internal static class OrtTensors
{
    /// <summary>Views an output as a shaped read-only tensor. Valid only while the <see cref="OrtValue"/> is alive.</summary>
    public static ReadOnlyTensorSpan<T> AsTensorSpan<T>(this OrtValue value)
        where T : unmanaged
    {
        long[] shape = value.GetTensorTypeAndShape().Shape;
        nint[] lengths = new nint[shape.Length];
        for (int i = 0; i < shape.Length; i++)
        {
            lengths[i] = (nint)shape[i];
        }

        return new ReadOnlyTensorSpan<T>(value.GetTensorDataAsSpan<T>(), lengths, []);
    }

    public static long[] GetShape(this OrtValue value) => value.GetTensorTypeAndShape().Shape;

    /// <summary>Runs a session with the given named inputs and returns the named outputs in order.</summary>
    public static IDisposableReadOnlyCollection<OrtValue> Run(
        this InferenceSession session,
        ReadOnlySpan<(string Name, OrtValue Value)> inputs,
        params string[] outputs)
    {
        string[] names = new string[inputs.Length];
        OrtValue[] values = new OrtValue[inputs.Length];
        for (int i = 0; i < inputs.Length; i++)
        {
            names[i] = inputs[i].Name;
            values[i] = inputs[i].Value;
        }

        using RunOptions options = new();
        return session.Run(options, names, values, outputs);
    }
}
