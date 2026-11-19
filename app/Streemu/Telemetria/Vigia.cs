using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Streemu.Telemetria;

/// <summary>
/// Três sinais do PC que explicam "a live travou" sem ser culpa da rede nem do encoder, todos só com números:
///  - <c>suspensao</c>: o Windows suspendeu ou voltou (a conexão cai e o relógio dá salto; sem isso o log parece bug do app);
///  - <c>monitor</c>: a quantidade e o tamanho das telas mudou (a captura da tela inteira muda de resolução no meio da live);
///  - <c>ui_travada</c>: a janela do Streemu ficou sem responder por mais que o limite (travamento da interface vira atraso de tudo o que depende dela).
/// Custo: um carimbo de tempo por segundo na thread da interface (o <see cref="Batimento"/>, que já pega carona no timer da telemetria) e uma
/// thread de prioridade baixa acordando a cada segundo para comparar. Sem WMI, sem disco fora do <see cref="Gravador"/>, sem nome de tela
/// nem de aplicativo.
/// </summary>
public sealed class Vigia : IDisposable
{
    readonly SessaoLocal _s;
    readonly Func<JsonNode?> _contexto;
    readonly TimeSpan _limite, _emCurso, _graca, _passo;
    readonly Thread? _thread;
    readonly ManualResetEventSlim _parar = new(false);
    readonly bool _sistema;
    System.Threading.Timer? _debounceMonitor;
    readonly object _g = new();

    long _ultimoMs;            // último batimento (relógio monotônico)
    long _inicioTravaMs = -1;  // quando a interface parou de responder (último batimento antes da trava)
    bool _emCursoAvisado;
    long _suspensoDesdeMs = -1;
    long _gracaAteMs;
    int _travadas;
    bool _fechado;

    static long Agora() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <param name="limite">Sem batimento por mais que isso = interface travada (padrão 3 s).</param>
    /// <param name="emCurso">Travada por mais que isso: avisa sem esperar voltar, porque pode nunca voltar (padrão 10 s).</param>
    /// <param name="graca">Depois de voltar da suspensão, ninguém é cobrado por esse tempo (padrão 5 s).</param>
    /// <param name="sistema">Falso nos testes: não assina eventos do Windows nem lê as telas.</param>
    public Vigia(SessaoLocal s, Func<JsonNode?> contexto, TimeSpan? limite = null, TimeSpan? emCurso = null, TimeSpan? graca = null, TimeSpan? passo = null, bool sistema = true)
    {
        _s = s; _contexto = contexto;
        _limite = limite ?? TimeSpan.FromSeconds(3);
        _emCurso = emCurso ?? TimeSpan.FromSeconds(10);
        _graca = graca ?? TimeSpan.FromSeconds(5);
        _passo = passo ?? TimeSpan.FromSeconds(1);
        _sistema = sistema;
        _ultimoMs = Agora();
        _thread = new Thread(Laco) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "telemetria-ui" };
        _thread.Start();
        if (sistema)
        {
            try
            {
                SystemEvents.PowerModeChanged += AoMudarEnergia;
                SystemEvents.DisplaySettingsChanged += AoMudarTelas;
            }
            catch { /* sem a janela de mensagens do sistema, só perdemos esses dois sinais */ }
            Monitor("inicio");
        }
    }

    /// <summary>Chamado pela thread da interface a cada segundo. Se vinha travada, fecha o aviso com a duração.</summary>
    public void Batimento()
    {
        long agora = Agora();
        long travouEm;
        lock (_g)
        {
            travouEm = _inicioTravaMs;
            _inicioTravaMs = -1; _emCursoAvisado = false;
            _ultimoMs = agora;
        }
        if (travouEm >= 0) Avisar((agora - travouEm) / 1000.0, emCurso: false);
    }

    void Laco()
    {
        while (!_parar.Wait(_passo)) { try { Verificar(); } catch { } }
    }

    /// <summary>Compara o último batimento com o relógio. Público para os testes chamarem sem esperar a thread.</summary>
    public void Verificar()
    {
        long agora = Agora();
        double? avisar = null;
        lock (_g)
        {
            if (_suspensoDesdeMs >= 0 || agora < _gracaAteMs) return; // o PC dormiu ou acabou de acordar: não é a interface
            long parada = agora - _ultimoMs;
            if (parada < _limite.TotalMilliseconds) return;
            if (_inicioTravaMs < 0) _inicioTravaMs = _ultimoMs;
            if (!_emCursoAvisado && parada >= _emCurso.TotalMilliseconds) { _emCursoAvisado = true; avisar = parada / 1000.0; }
        }
        if (avisar != null) Avisar(avisar.Value, emCurso: true);
    }

    void Avisar(double segundos, bool emCurso)
    {
        if (_fechado) return;
        Interlocked.Increment(ref _travadas);
        var o = new JsonObject { ["duracao_s"] = Math.Round(segundos, 1), ["em_curso"] = emCurso };
        try { o["lives"] = _contexto(); } catch { }
        _s.Evento("ui_travada", o);
    }

    /// <summary>Só os testes (e quem quiser contar): quantos avisos de interface travada saíram.</summary>
    public int Avisos => Volatile.Read(ref _travadas);

    // ---------- suspensão ----------

    void AoMudarEnergia(object? _, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Suspendeu();
        else if (e.Mode == PowerModes.Resume) Voltou();
    }

    public void Suspendeu()
    {
        lock (_g) { if (_suspensoDesdeMs >= 0) return; _suspensoDesdeMs = Agora(); }
        _s.Evento("suspensao", new JsonObject { ["fase"] = "suspende" });
    }

    public void Voltou()
    {
        long desde, agora = Agora();
        lock (_g)
        {
            desde = _suspensoDesdeMs; _suspensoDesdeMs = -1;
            _gracaAteMs = agora + (long)_graca.TotalMilliseconds;
            _inicioTravaMs = -1; _emCursoAvisado = false; _ultimoMs = agora; // o salto do relógio não é trava
        }
        var o = new JsonObject { ["fase"] = "volta" };
        // a duração só vale quando vimos o início; sem ele (o app abriu durante o sono) não inventa
        if (desde >= 0) o["duracao_s"] = Math.Round((agora - desde) / 1000.0, 1);
        _s.Evento("suspensao", o);
    }

    // ---------- monitores ----------

    void AoMudarTelas(object? _, EventArgs e)
    {
        // o Windows dispara várias vezes seguidas ao trocar de modo; um evento só depois de assentar
        try { (_debounceMonitor ??= new System.Threading.Timer(_ => Monitor("mudou"))).Change(1000, Timeout.Infinite); } catch { }
    }

    void Monitor(string fase)
    {
        if (_fechado) return;
        try
        {
            var telas = new JsonArray();
            foreach (var t in System.Windows.Forms.Screen.AllScreens)
                telas.Add(new JsonObject { ["w"] = t.Bounds.Width, ["h"] = t.Bounds.Height, ["primaria"] = t.Primary });
            _s.Evento("monitor", new JsonObject { ["fase"] = fase, ["quantidade"] = telas.Count, ["telas"] = telas });
        }
        catch { }
    }

    public void Dispose()
    {
        _fechado = true;
        if (_sistema)
        {
            try { SystemEvents.PowerModeChanged -= AoMudarEnergia; SystemEvents.DisplaySettingsChanged -= AoMudarTelas; } catch { }
        }
        _debounceMonitor?.Dispose();
        _parar.Set();
    }
}
