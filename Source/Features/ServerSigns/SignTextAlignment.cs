namespace ServerSideTweaks.Features.ServerSigns
{
    internal static class SignTextAlignment
    {
        internal const string DefaultSourceValue = "left";

        internal static bool TryNormalize(string value, out string alignment)
        {
            string normalized = (value ?? "").Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "left":
                case "center":
                case "right":
                    alignment = normalized;
                    return true;
                default:
                    alignment = "";
                    return false;
            }
        }

        internal static string NormalizeOrDefault(string value)
        {
            return TryNormalize(value, out string alignment) ? alignment : DefaultSourceValue;
        }
    }
}
