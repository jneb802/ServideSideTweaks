using System;
using System.Globalization;

namespace ServerSideTweaks.Features.ServerSigns
{
    internal static class SignTextScale
    {
        internal const string DefaultSourceValue = "1";
        private const float MinScale = 0.1f;
        private const float MaxScale = 10.0f;

        internal static bool TryNormalize(string value, out string scale)
        {
            string normalized = (value ?? "").Trim().ToLowerInvariant();
            scale = "";
            if (!float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out float numeric) ||
                float.IsNaN(numeric) ||
                float.IsInfinity(numeric))
            {
                return false;
            }

            numeric = Math.Min(MaxScale, Math.Max(MinScale, numeric));
            scale = numeric.ToString("0.###", CultureInfo.InvariantCulture);
            return true;
        }

        internal static string NormalizeOrDefault(string value)
        {
            return TryNormalize(value, out string scale) ? scale : DefaultSourceValue;
        }

        internal static float ToMultiplier(string value)
        {
            string scale = NormalizeOrDefault(value);
            return float.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out float numeric)
                ? numeric
                : 1.0f;
        }
    }
}
