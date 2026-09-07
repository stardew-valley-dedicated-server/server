using System.Threading;

namespace JunimoServer.Util;

/// <summary>
/// Average of the last N samples. Ring buffer plus running sum: O(1) per sample, no allocation
/// after construction. Writes belong to one thread; <see cref="Average"/> is safe to read from any.
/// </summary>
public sealed class RollingAverage
{
    private readonly double[] _samples;
    private int _index;
    private int _count;
    private double _sum;
    private double _average;

    public RollingAverage(int capacity)
    {
        _samples = new double[capacity];
    }

    /// <summary>Average of the samples currently in the window; 0 until the first sample.</summary>
    public double Average => Volatile.Read(ref _average);

    public void Add(double sample)
    {
        if (_count == _samples.Length)
        {
            _sum -= _samples[_index];
        }
        else
        {
            _count++;
        }

        _samples[_index] = sample;
        _sum += sample;
        _index = (_index + 1) % _samples.Length;
        Volatile.Write(ref _average, _sum / _count);
    }
}
