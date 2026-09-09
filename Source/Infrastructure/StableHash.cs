namespace ServerSideTweaks.Infrastructure
{
    internal static class StableHash
    {
        internal static int Compute(string value)
        {
            // Valheim 0.221.13 changed the method signature without changing
            // the algorithm. Keep this mod binary compatible with both forms.
            unchecked
            {
                int hash1 = 5381;
                int hash2 = hash1;

                for (int index = 0; index < value.Length && value[index] != '\0'; index += 2)
                {
                    hash1 = ((hash1 << 5) + hash1) ^ value[index];
                    if (index == value.Length - 1 || value[index + 1] == '\0')
                    {
                        break;
                    }

                    hash2 = ((hash2 << 5) + hash2) ^ value[index + 1];
                }

                return hash1 + hash2 * 1566083941;
            }
        }
    }
}
