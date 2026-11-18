using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// 播放 880Hz 测试音指定秒数，供 MvpE2E 录制期间的系统声 loopback 采集。
int seconds = args.Length > 0 && int.TryParse(args[0], out int s) ? s : 10;
var enumerator = new MMDeviceEnumerator();
var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
using var outDev = new WasapiOut(dev, AudioClientShareMode.Shared, true, 50);
var sig = new SignalGenerator(48000, 2) { Frequency = 880, Gain = 0.3, Type = SignalGeneratorType.Sin };
outDev.Init(sig.ToWaveProvider16());
outDev.Play();
Console.WriteLine($"PLAYING {seconds}s");
Thread.Sleep(seconds * 1000);
outDev.Stop();
Console.WriteLine("DONE");
