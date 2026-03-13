using System.Collections.Generic;

namespace WhisperInk
{
    public class AppConfig
    {
        public string MistralApiKey { get; set; } = "";
        public bool IsSoundEnabled { get; set; } = true;
        public string SystemPrompt { get; set; } = "You are a precise execution engine. The user will give you text and a voice instruction. Follow the instruction exactly. Return only the result — no commentary, no markdown, no explanation.";
        public int SelectedDevice { get; set; } = 0;
        
        // Mistral Realtime API parameter: balances latency vs accuracy
        public int TargetStreamingDelayMs { get; set; } = 480;
        
        // "Realtime" = live streaming via WebSocket, "Batch" = record-then-transcribe
        public string DictationMode { get; set; } = "Realtime";
        
        // Path to the Mistral realtime proxy script (Python)
        public string ProxyPath { get; set; } = "";
        
        // Context biasing terms for batch transcription (up to 100 words/phrases)
        // Helps with domain-specific vocabulary like medical terminology
        public List<string> ContextBiasTerms { get; set; } = new();
        
        // When true, batch transcription results are passed through a fast LLM correction pass
        public bool PostProcessBatch { get; set; } = false;
        
        // Prompt used for the post-processing correction pass
        public string PostProcessPrompt { get; set; } = "You are a medical transcription corrector. The following text was produced by speech recognition during clinical dictation. Fix only words that are clearly speech recognition errors where a medical term was likely intended (e.g., \"a fusion\" → \"effusion\", \"new monia\" → \"pneumonia\"). Do not alter grammar, punctuation, style, or sentence structure. Return only the corrected text.";
    }
}
