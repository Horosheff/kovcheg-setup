using System.IO;
using System.Media;
using System.Reflection;

namespace KovchegVPN;

// Джарвис: короткие голосовые фразы на события (vendor/jarvis-sound).
public enum JarvisClip { Connected, Split, Off, Diagnostic, Warning, Checked, Done, Startup, Rebooted, AsYouWish, YesSir }

public static class Jarvis
{
    private static readonly Dictionary<JarvisClip, byte[]> Clips = LoadAll();

    private static Dictionary<JarvisClip, byte[]> LoadAll()
    {
        var clips = new Dictionary<JarvisClip, byte[]>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var (clip, res) in new[]
        {
            (JarvisClip.Connected, "jarvis-50.wav"),   // Мы подключены и готовы
            (JarvisClip.Split, "jarvis-32.wav"),       // Поможет оставаться незамеченным
            (JarvisClip.Off, "jarvis-46.wav"),         // Отключаю питание
            (JarvisClip.Diagnostic, "jarvis-45.wav"),  // Отключаю питание, начинаю диагностику
            (JarvisClip.Warning, "jarvis-25.wav"),         // Сэр, вы под прицелом, нужен обманный маневр
            (JarvisClip.Checked, "jarvis-47.wav"),     // Проверка завершена
            (JarvisClip.Done, "jarvis-16.wav"),        // Запрос выполнен, сэр
            (JarvisClip.Startup, "jarvis-35.wav"),      // С возвращением, сэр
            (JarvisClip.Rebooted, "jarvis-51.wav"),     // Я перезагрузился, сэр
            (JarvisClip.AsYouWish, "jarvis-48.wav"),    // Как пожелаете
            (JarvisClip.YesSir, "jarvis-53.wav"),       // Есть
        })
        {
            try
            {
                using var s = asm.GetManifestResourceStream("KovchegVPN.Resources." + res);
                if (s == null) { LogBus.Write("jarvis: нет ресурса " + res); continue; }
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                clips[clip] = ms.ToArray();
            }
            catch (Exception ex) { LogBus.Write("jarvis: " + res + " " + ex.Message); }
        }
        return clips;
    }

    private static int _talking;
    private static readonly SemaphoreSlim _gate = new(1, 1);
    public static bool Talking => _talking > 0;

    public static void Say(JarvisClip clip)
    {
        if (!Clips.TryGetValue(clip, out var bytes) || bytes.Length == 0) { LogBus.Write("jarvis: нет клипа " + clip); return; }
        LogBus.Write("jarvis: " + clip);
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                Interlocked.Increment(ref _talking);
                try
                {
                    using var p = new SoundPlayer(new MemoryStream(bytes));
                    p.PlaySync();
                }
                finally { Interlocked.Decrement(ref _talking); }
            }
            catch (Exception ex) { LogBus.Write("jarvis play: " + ex.Message); }
            finally { _gate.Release(); }
        });
    }
}
