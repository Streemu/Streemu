using System.Text.Json.Nodes;
using Streemu.Util;

namespace Streemu.Telemetria;

/// <summary>Na abertura: acha sessões que não terminaram de subir (app travou, PC desligou, sem internet) e limpa o que já subiu.</summary>
public static class Manutencao
{
    /// <summary>Teto da pasta local da telemetria (decisão de 2026-11-16: 3 GB; era 300 MB).</summary>
    public const long TetoBytes = 3L * 1024 * 1024 * 1024;
    static readonly TimeSpan GuardaEnviadas = TimeSpan.FromDays(3), GuardaLogs = TimeSpan.FromDays(30);

    /// <summary>Sessões anteriores com algo ainda por enviar. A que ficou aberta sem o dono vivo ganha o evento "não fechou normalmente".</summary>
    public static List<SessaoLocal> Anteriores(SessaoLocal atual)
    {
        var lista = new List<SessaoLocal>();
        foreach (var dir in Dirs())
        {
            var s = SessaoLocal.Abrir(dir);
            if (s == null || s.Id == atual.Id || s.E.NaoEnviar || s.E.Concluida) continue;
            if (s.Aberta)
            {
                if (s.DonoVivo()) continue; // outra instância aberta agora (testes locais): não é nossa
                s.Evento("sessao_sem_fim");
                s.E.Fechou = "nao";
                s.Salvar();
                Log.Info($"[telemetria] a sessão {s.Id} não fechou normalmente (o app travou ou o PC desligou); envio o que ficou dela.");
            }
            lista.Add(s);
        }
        return lista.OrderBy(s => s.E.Inicio).ToList();
    }

    /// <summary>
    /// Apaga sessões já enviadas há mais de 3 dias, respeita o teto de <see cref="TetoBytes"/> e apaga logs de mais de 30 dias que não falta enviar.
    /// Ordem de descarte quando passa do teto (política proposta na etapa 4, a aprovar na revisão do PR): 1) sessões já enviadas ou que nunca sobem
    /// (desligadas), da mais antiga para a mais nova; 2) só se ainda passar, sessões de aberturas anteriores que NÃO terminaram de subir, também da mais
    /// antiga para a mais nova, com o log delas (que está em <c>logs\</c> e entra na conta). Nunca apaga a sessão atual nem a de outra instância aberta.
    /// Cada sessão com dado por enviar que for apagada deixa o evento "sessao_descartada" na sessão atual (quantos bytes ficaram sem subir): o buraco
    /// aparece na telemetria em vez de parecer "não teve erro". <paramref name="aoDescartar"/> avisa o envio para esquecer a sessão.
    /// </summary>
    public static void Limpar(string? logAtual, SessaoLocal? atual = null, long tetoBytes = TetoBytes, Action<string>? aoDescartar = null)
    {
        try
        {
            var sessoes = Dirs().Select(d => (Dir: d, S: SessaoLocal.Abrir(d))).ToList();
            var pendentesLogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, s) in sessoes) if (s is { E.Concluida: false, E.NaoEnviar: false, E.LogPath: string lp }) pendentesLogs.Add(lp);

            long total = 0;
            var candidatas = new List<(string Dir, DateTime Quando, long Tam)>();
            var pendentes = new List<(string Dir, SessaoLocal S, DateTime Quando, long Tam, long Log)>();
            foreach (var (dir, s) in sessoes)
            {
                long tam = Tamanho(dir);
                long log = s is { E.Concluida: false, E.NaoEnviar: false } && LogDaTelemetria(s.E.LogPath) is string lg ? TamanhoArquivo(lg) : 0;
                total += tam + log;
                if (s == null || s.Aberta && s.DonoVivo() || atual != null && s.Id == atual.Id) continue;
                var quando = DateTimeOffset.FromUnixTimeMilliseconds(s.E.Inicio).LocalDateTime;
                if (s.E.Concluida || s.E.NaoEnviar) { if (!s.Aberta) candidatas.Add((dir, quando, tam)); }
                else pendentes.Add((dir, s, quando, tam, log));
            }
            foreach (var c in candidatas.OrderBy(c => c.Quando))
            {
                if (DateTime.Now - c.Quando < GuardaEnviadas && total < tetoBytes) break;
                try { Directory.Delete(c.Dir, true); total -= c.Tam; } catch { }
            }
            foreach (var pe in pendentes.OrderBy(x => x.Quando))
            {
                if (total <= tetoBytes) break;
                long semEnviar = BytesSemEnviar(pe.S);
                try
                {
                    Directory.Delete(pe.Dir, true);
                    if (pe.Log > 0 && LogDaTelemetria(pe.S.E.LogPath) is string lg) File.Delete(lg);
                    total -= pe.Tam + pe.Log;
                }
                catch { continue; }
                Log.Info($"[telemetria] a pasta local passou de {tetoBytes / (1024 * 1024)} MB: apaguei a sessão {pe.S.Id}, que não tinha terminado de subir ({semEnviar} bytes ficaram sem enviar).");
                atual?.Evento("sessao_descartada", new JsonObject
                {
                    ["sessao_descartada"] = pe.S.Id, ["motivo"] = "teto_local", ["teto_bytes"] = tetoBytes, ["bytes_sem_enviar"] = semEnviar,
                    ["inicio_utc"] = DateTimeOffset.FromUnixTimeMilliseconds(pe.S.E.Inicio).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["fechou"] = pe.S.E.Fechou,
                });
                aoDescartar?.Invoke(pe.S.Id);
            }

            if (Log.Folder == "" || !Directory.Exists(Log.Folder)) return;
            foreach (var f in Directory.EnumerateFiles(Log.Folder, "sessao-*.log"))
            {
                if (string.Equals(f, logAtual, StringComparison.OrdinalIgnoreCase) || pendentesLogs.Contains(f)) continue;
                try { if (DateTime.Now - File.GetLastWriteTime(f) > GuardaLogs) File.Delete(f); } catch { }
            }
        }
        catch (Exception e) { Log.Info("[telemetria] limpeza da pasta local falhou: " + e.Message); }
    }

    /// <summary>O log da sessão só conta (e só é apagado) se for um sessao-*.log da pasta de logs do app: o caminho vem de um arquivo no disco.</summary>
    static string? LogDaTelemetria(string? caminho)
    {
        if (caminho == null || Log.Folder == "") return null;
        try
        {
            return Path.GetFileName(caminho).StartsWith("sessao-") && caminho.EndsWith(".log")
                && string.Equals(Path.GetDirectoryName(Path.GetFullPath(caminho)), Path.GetFullPath(Log.Folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ? caminho : null;
        }
        catch { return null; }
    }

    static long TamanhoArquivo(string f) { try { return new FileInfo(f).Length; } catch { return 0; } }

    static long BytesSemEnviar(SessaoLocal s)
    {
        long soma = 0;
        foreach (var (nome, caminho) in s.Arquivos()) soma += Math.Max(0, TamanhoArquivo(caminho) - s.Cursor(nome));
        return soma;
    }

    static IEnumerable<string> Dirs()
    {
        try { return Directory.Exists(Pasta.Raiz) ? Directory.GetDirectories(Pasta.Raiz) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    static long Tamanho(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }
}
