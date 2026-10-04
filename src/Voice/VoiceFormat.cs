namespace CommPanel.Voice;

/// <summary>
/// The wire format for the voice link.
///
/// 16 kHz mono 16-bit is telephone quality and then some - comfortably clear for speech -
/// at 256 kbit/s, which a home upstream can carry without thought. Windows mixes at 48 kHz
/// stereo float, so both ends resample; see <see cref="VoiceResampler"/>.
/// </summary>
internal static class VoiceFormat
{
    public const int SampleRate = 16000;

    /// <summary>20 ms per packet: small enough to keep latency low, large enough that the
    /// per-packet overhead stays negligible.</summary>
    public const int FrameMilliseconds = 20;

    public const int SamplesPerFrame = SampleRate * FrameMilliseconds / 1000; // 320
    public const int BytesPerFrame = SamplesPerFrame * 2;                      // 640

    /// <summary>
    /// Above this a level counts as real sound rather than a quiet room. Used to tell
    /// "nobody is talking" apart from "the audio path is broken", which look identical on
    /// a meter and need completely different answers.
    /// </summary>
    public const float SignalFloor = 0.02f;

    /// <summary>Below this, a stream is producing nothing at all.</summary>
    public const float SilenceFloor = 0.005f;
}

/// <summary>
/// Converts between the device's sample rate and the voice rate.
///
/// The fractional read position is kept across calls on purpose. Resetting it per buffer
/// would put a discontinuity at every buffer boundary, which is audible as a click fifty
/// times a second.
/// </summary>
internal sealed class VoiceResampler
{
    private readonly double _step;
    private readonly List<float> _pending = new();
    private double _position;

    public VoiceResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        _step = (double)inputRate / outputRate;
    }

    public void Push(float sample) => _pending.Add(sample);

    public void Push(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples) _pending.Add(sample);
    }

    /// <summary>
    /// Produces as many output samples as the input so far allows.
    ///
    /// Downsampling averages across the whole step rather than picking one sample, which is a
    /// crude anti-aliasing filter but enough to keep 48 kHz speech from folding noise into the
    /// 16 kHz band. Upsampling interpolates between neighbours.
    /// </summary>
    public void Drain(List<float> output)
    {
        if (_step >= 1.0)
        {
            while (_position + _step <= _pending.Count)
            {
                int from = (int)_position;
                int to = Math.Min(_pending.Count, (int)Math.Ceiling(_position + _step));

                float sum = 0f;
                int count = 0;
                for (int i = from; i < to; i++) { sum += _pending[i]; count++; }

                output.Add(count > 0 ? sum / count : 0f);
                _position += _step;
            }
        }
        else
        {
            while (_position + 1 < _pending.Count)
            {
                int index = (int)_position;
                float fraction = (float)(_position - index);
                float a = _pending[index];
                float b = _pending[index + 1];

                output.Add(a + (b - a) * fraction);
                _position += _step;
            }
        }

        // Discard what can never be read again, keeping one sample of overlap so interpolation
        // across the seam still has a left-hand neighbour.
        int consumed = Math.Max(0, (int)_position - 1);
        if (consumed > 0)
        {
            _pending.RemoveRange(0, Math.Min(consumed, _pending.Count));
            _position -= consumed;
        }

        // A stalled reader must not let the backlog grow without bound.
        const int ceiling = 48000;
        if (_pending.Count > ceiling)
        {
            _pending.RemoveRange(0, _pending.Count - ceiling / 2);
            _position = 0;
        }
    }

    public void Reset()
    {
        _pending.Clear();
        _position = 0;
    }
}
