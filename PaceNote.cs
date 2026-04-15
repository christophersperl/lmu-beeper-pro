namespace LMUWeaver
{
    public class PaceNote
    {
        public double Distance { get; set; }
        /// <summary>Symbol code stored here, e.g. "L3", "R5", "CREST".</summary>
        public string Symbol   { get; set; } = "R3";
        /// <summary>Free text spoken by TTS and shown below the symbol.</summary>
        public string Text     { get; set; } = "";
    }
}
