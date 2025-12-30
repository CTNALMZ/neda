// Effect processor oluşturma;
using System.Threading;

var effectProcessor = new EffectProcessor();

// Audio data yükleme;
var audioData = LoadAudioFile("input.wav");

// Effect'ler oluşturma;
var reverb = new ReverbEffect { WetLevel = 0.3f, RoomSize = 0.7f };
var delay = new DelayEffect { DelayTime = 0.5f, Feedback = 0.4f };
var equalizer = new EqualizerEffect();

// Audio processing;
var processedAudio = effectProcessor.Process(audioData, reverb, delay, equalizer);

// Async processing;
var processedAudioAsync = await effectProcessor.ProcessAsync(
    audioData,
    new[] { reverb, delay, equalizer },
    cancellationToken;
);

// Preset kaydetme;
effectProcessor.SavePreset("MyPreset", "presets.xml", reverb, delay, equalizer);
