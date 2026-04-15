namespace SunshineLibrary.Models
{
    public enum OverrideSource { BuiltIn, Global, Host, PerGame }

    public class FieldProvenance
    {
        /// <summary>Localized display label, or section heading text when <see cref="IsSection"/> is true.</summary>
        public string Label { get; set; }

        /// <summary>Human-readable resolved value (e.g. "1920x1080", "Auto (match client)", "On").</summary>
        public string ResolvedValue { get; set; }

        /// <summary>Which override layer supplied this value.</summary>
        public OverrideSource Source { get; set; }

        /// <summary>Optional italic note shown in the fourth column (e.g. "→ 1920x1080 (from display)").</summary>
        public string RuntimeNote { get; set; }

        /// <summary>When true this entry is a section-group heading, not a data row.</summary>
        public bool IsSection { get; set; }
    }
}
