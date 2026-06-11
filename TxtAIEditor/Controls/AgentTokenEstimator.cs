namespace TxtAIEditor.Controls
{
    internal static class AgentTokenEstimator
    {
        public static double Estimate(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            double tokens = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                tokens += c <= 127 ? 0.25 : 0.7;
            }

            return tokens;
        }

        public static string Format(double value)
        {
            if (value >= 1000000.0)
            {
                return (value / 1000000.0).ToString("0.#") + "M";
            }

            if (value >= 1000.0)
            {
                return (value / 1000.0).ToString("0.#") + "k";
            }

            return value.ToString("F0");
        }
    }
}
