using System.Collections.Concurrent;
using System.Diagnostics;

namespace Streemu.Util
{
    static class Log
    {
        public static readonly ConcurrentQueue<string> Lines = new();
        public static void Info(string line) => Lines.Enqueue(line);
    }

    // O Tools.cs de verdade pede o caminho ao Binarios (só o embutido, conferido por SHA-256). Aqui o "ffplay" é uma cópia do apphost deste teste, ao lado do exe:
    // a conferência de verdade é do tests/Binarios; este teste só quer o ciclo de vida do Viewer.
    enum Binario { Ffmpeg, Ffplay, Mpv }

    sealed class BinarioIndisponivelException : InvalidOperationException
    {
        public BinarioIndisponivelException(string m) : base(m) { }
    }

    static class Binarios
    {
        public static string? Caminho(Binario b) => b == Binario.Ffplay ? Path.Combine(AppContext.BaseDirectory, "ffplay.exe") : null;
        public static string Exigir(Binario b) => Caminho(b) ?? throw new BinarioIndisponivelException("sem " + b);
        public static string Motivo(Binario b) => "sem " + b;
    }

    static class Native
    {
        public static void AddToKillOnCloseJob(Process p)
        {
            // Força o caso de campo: saída zero ANTES de Tools.Start devolver o processo e de o stderr ser lido.
            if (Environment.GetEnvironmentVariable("VIEWER_FAKE_CASE") == "surface-zero" &&
                p.StartInfo.ArgumentList.Contains("-enable_vulkan"))
                if (!p.WaitForExit(3000)) throw new Exception("O player simulado não encerrou.");
        }
    }
}

namespace Streemu.Media
{
    public sealed class PlayerWindow
    {
        public PlayerWindow(Func<int?> pid) { }
    }
    public sealed class PlayerAudio
    {
        public PlayerAudio(Func<int?> pid) { }
        public void AttachWhenReady() { }
    }
}
