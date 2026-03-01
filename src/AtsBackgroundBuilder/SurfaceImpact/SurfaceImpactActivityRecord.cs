using System;

namespace AtsBackgroundBuilder.SurfaceImpact
{
    internal sealed class SurfaceImpactActivityRecord
    {
        // Original ActivityNumber from XML
        public string DispositionNumber { get; set; } = string.Empty;

        public string OwnerName { get; set; } = string.Empty;
        public string ExpiryDateString { get; set; } = string.Empty;
        public string LandLocation { get; set; } = string.Empty;
        public string VersionDateString { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;

        public DateTime? ReportRunDate { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public bool IsExpired { get; set; }

        // Flag set by processor when the original disposition contains RTF or TFA
        public bool IsRtfTfa { get; set; }

        public string BaseLandLocation { get; set; } = string.Empty;

        // What the table should display in "ACTIVITY NO."
        public string ActivityNoForTable
        {
            get { return IsRtfTfa ? "ACTIVE RTF/TFA(S)" : DispositionNumber; }
        }
    }
}
