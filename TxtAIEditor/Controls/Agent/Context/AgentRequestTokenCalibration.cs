using System;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentRequestTokenCalibration
    {
        private int _estimatedBaseline;
        private int _actualBaseline;

        public int Estimate(int currentEstimate) => _actualBaseline > 0
            ? (int)Math.Min(int.MaxValue, (long)_actualBaseline + Math.Max(0, currentEstimate - _estimatedBaseline))
            : currentEstimate;

        public void Record(int estimate, int actual)
        {
            if (estimate <= 0 || actual <= 0) return;
            _estimatedBaseline = estimate;
            _actualBaseline = actual;
        }

        public void Reset() => _actualBaseline = _estimatedBaseline = 0;
    }
}
