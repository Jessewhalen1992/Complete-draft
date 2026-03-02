namespace AtsBackgroundBuilder
{
    public enum QuarterSelection
    {
        None,
        NorthWest,
        NorthEast,
        SouthWest,
        SouthEast,
        NorthHalf,
        SouthHalf,
        EastHalf,
        WestHalf,
        All
    }

    public sealed class SectionKey
    {
        public SectionKey(int zone, string section, string township, string range, string meridian)
        {
            Zone = zone;
            Section = section ?? string.Empty;
            Township = township ?? string.Empty;
            Range = range ?? string.Empty;
            Meridian = meridian ?? string.Empty;
        }

        public int Zone { get; }
        public string Section { get; }
        public string Township { get; }
        public string Range { get; }
        public string Meridian { get; }
    }

    public sealed class SectionRequest
    {
        public SectionRequest(QuarterSelection quarter, SectionKey key, string secType = "AUTO")
        {
            Quarter = quarter;
            Key = key;
            SecType = secType ?? string.Empty;
        }

        public QuarterSelection Quarter { get; }
        public SectionKey Key { get; }
        public string SecType { get; }
    }
}
