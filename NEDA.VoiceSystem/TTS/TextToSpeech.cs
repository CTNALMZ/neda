using System;
using System.Speech.Synthesis;

namespace NEDA.VoiceSystem.TTS
{
    public class TextToSpeech
    {
        private SpeechSynthesizer _synth;

        public TextToSpeech()
        {
            _synth = new SpeechSynthesizer();
            _synth.SelectVoice("Microsoft Zira Desktop"); // Kadın ses
            _synth.Rate = 0;
            _synth.Volume = 100;
        }

        public void Speak(string text)
        {
            _synth.SpeakAsync(text);
            Console.WriteLine("[TTS] NEDA: " + text);
        }
    }
}
