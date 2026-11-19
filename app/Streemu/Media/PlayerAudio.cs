using System.Runtime.InteropServices;
using Streemu.Util;
using NAudio.CoreAudioApi;

namespace Streemu.Media;

/// <summary>
/// Volume e mudo só do ffplay, pela sessão de áudio dele no Windows (a mesma do Misturador de volume).
/// Não mexe no volume do sistema nem de outros apps. A sessão só existe depois do primeiro áudio tocado,
/// então <see cref="AttachWhenReady"/> espera por ela e aplica o volume escolhido.
/// Uso: <c>viewer.Audio.SetPercent(40); viewer.Audio.ToggleMuted();</c>
/// </summary>
public sealed class PlayerAudio
{
    const int PollMs = 100;
    const int SlowAfterMs = 15000;
    const int SlowPollMs = 1000;
    readonly Func<int?> _processId;
    CancellationTokenSource? _attach;

    public PlayerAudio(Func<int?> processId) => _processId = processId;

    public int Percent { get; private set; } = 100;
    public bool Muted { get; private set; }
    /// <summary>Disparado quando o usuário muda volume ou mudo (para salvar a escolha).</summary>
    public event Action? Changed;

    /// <summary>Define o valor guardado de uma sessão anterior, sem avisar <see cref="Changed"/>.</summary>
    public void Restore(int percent, bool muted)
    {
        Percent = Math.Clamp(percent, 0, 100);
        Muted = muted;
    }

    public void SetPercent(int percent)
    {
        Percent = Math.Clamp(percent, 0, 100);
        ApplyToSession();
        Changed?.Invoke();
    }

    public void SetMuted(bool muted)
    {
        Muted = muted;
        ApplyToSession();
        Changed?.Invoke();
    }

    public void ToggleMuted() => SetMuted(!Muted);

    /// <summary>Chamar a cada vez que o ffplay abre (inclusive na volta para a CPU, que abre outro processo).</summary>
    public void AttachWhenReady()
    {
        _attach?.Cancel();
        var cts = _attach = new CancellationTokenSource();
        OutputWatcher.Changed -= OnOutputChanged;
        OutputWatcher.Changed += OnOutputChanged;
        _ = Task.Run(() => WaitForSession(cts.Token));
    }

    // Campo (2026-11-08): volume em 10% no fone, trocou a saída do Windows para a caixa Edifier e o volume voltou a outro valor.
    // O volume do Misturador é por sessão E por dispositivo: o ffplay (SDL) reabre o áudio no dispositivo novo, nasce outra sessão
    // com o volume que o Windows guardou para ele naquele dispositivo, e aqui só se aplicava uma vez, no dispositivo da abertura.
    // Agora, quando a saída padrão muda, reaplica por alguns segundos (a sessão nova demora a aparecer) em todos os dispositivos.
    void OnOutputChanged()
    {
        if (_processId() == null) { OutputWatcher.Changed -= OnOutputChanged; return; }
        _attach?.Cancel();
        var cts = _attach = new CancellationTokenSource();
        _ = Task.Run(() =>
        {
            for (int i = 0; i < 30 && !cts.IsCancellationRequested && _processId() != null; i++)
            {
                ApplyToSession();
                Thread.Sleep(500);
            }
        });
    }

    // Campo (0.1.11): o áudio do amigo só começou depois de mais de 15 s e o volume nunca foi aplicado, porque
    // aqui se desistia aos 15 s. Agora segue tentando, mais devagar, enquanto o ffplay estiver aberto.
    void WaitForSession(CancellationToken stop)
    {
        int waited = 0;
        bool warned = false;
        while (!stop.IsCancellationRequested && _processId() != null)
        {
            if (ApplyToSession()) return;
            int delay = waited < SlowAfterMs ? PollMs : SlowPollMs;
            Thread.Sleep(delay);
            waited += delay;
            if (warned || waited < SlowAfterMs) continue;
            warned = true;
            Log.Info($"Ainda não achei a sessão de áudio do player ({SlowAfterMs / 1000} s); sigo tentando enquanto ele estiver aberto (normal se a transmissão não tem áudio ou o som demora a começar).");
        }
    }

    /// <summary>Verdadeiro se achou a sessão do ffplay (em qualquer saída ativa) e aplicou volume e mudo.</summary>
    bool ApplyToSession()
    {
        if (_processId() is not int pid) return false;
        bool found = false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        var sessions = device.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            if (session.GetProcessID != pid) continue;
                            session.SimpleAudioVolume.Volume = Percent / 100f;
                            session.SimpleAudioVolume.Mute = Muted;
                            found = true;
                        }
                    }
                    catch (COMException) { } // dispositivo saindo no meio (fone desconectado): os outros seguem
                }
            }
        }
        catch (COMException) { } // sem dispositivo de saída no momento: tenta de novo no próximo ciclo
        return found;
    }

    /// <summary>Avisa quando a saída de áudio padrão do Windows muda. Um só registro para o app inteiro.</summary>
    static class OutputWatcher
    {
        static readonly object Gate = new();
        static IDeviceEnumerator? _enumerator;
        static Client? _client;
        static Action? _changed;

        public static event Action Changed
        {
            add { lock (Gate) { EnsureRegistered(); _changed += value; } }
            remove { lock (Gate) _changed -= value; }
        }

        // O IMMNotificationClient do NAudio é interno nesta versão: as duas interfaces COM vão declaradas aqui, na ordem da vtable.
        static void EnsureRegistered()
        {
            if (_enumerator != null) return;
            try
            {
                var enumerator = (IDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
                _client = new Client();
                Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(_client));
                _enumerator = enumerator;
            }
            catch (Exception ex) // falhar aqui só tira a reaplicação na troca de saída; o player segue normal
            {
                Log.Info($"Não deu para acompanhar a troca de saída de áudio: {ex.Message}");
            }
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDeviceEnumerator
        {
            void EnumAudioEndpoints_NaoUsar();
            void GetDefaultAudioEndpoint_NaoUsar();
            void GetDevice_NaoUsar();
            [PreserveSig] int RegisterEndpointNotificationCallback(INotificationClient client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(INotificationClient client);
        }

        [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface INotificationClient
        {
            void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
            void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
            void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKeyStruct key);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PropertyKeyStruct { public Guid FormatId; public int PropertyId; }

        // Chega numa thread do Windows: só repassa, o trabalho de COM roda numa tarefa separada.
        static void Raise()
        {
            Action? handlers;
            lock (Gate) handlers = _changed;
            if (handlers != null) _ = Task.Run(handlers);
        }

        [ComVisible(true)]
        sealed class Client : INotificationClient
        {
            public void OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
            {
                if (flow == (int)DataFlow.Render && role == (int)Role.Multimedia) Raise();
            }
            public void OnDeviceStateChanged(string deviceId, int newState) { }
            public void OnDeviceAdded(string deviceId) { }
            public void OnDeviceRemoved(string deviceId) { }
            public void OnPropertyValueChanged(string deviceId, PropertyKeyStruct key) { }
        }
    }
}
