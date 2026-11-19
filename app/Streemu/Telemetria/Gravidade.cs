namespace Streemu.Telemetria;

/// <summary>
/// Quais linhas do log são falha GRAVE (merecem o relatório completo na hora, além do evento <c>falha</c>) e o freio que impede o relatório de
/// virar rotina: montar o relatório custa na thread da interface e uma falha costuma se repetir em rajada (a captura reabre várias vezes, o
/// laço de erro escreve a mesma linha a cada segundo). A spec 009, 5.5, pede relatório "em cada falha"; o freio é o que torna isso seguro.
/// </summary>
public static class Gravidade
{
    /// <summary>
    /// Esta linha do log é uma falha para a telemetria (vira o evento <c>falha</c> e conta em "problemas" do resumo)? Linhas dos ffmpeg, do sensor,
    /// da própria telemetria e os anúncios de amigo ("[amigo] fulano começou uma live") nunca são: antes o anúncio era "[aviso]" e inflava os problemas
    /// de quem só estava assistindo (visto no teste de campo de 2026-11-16).
    /// </summary>
    public static bool EhFalha(string linha)
    {
        if (linha.Contains("[telemetria]") || linha.Contains("[ffmpeg") || linha.Contains("[ffplay") || linha.Contains("[sonda]") || linha.Contains("[sensor]")
            || linha.Contains("[teste-") || linha.Contains("[amigo]")) return false;
        return linha.Contains("ERRO (") || linha.Contains("[aviso]") || linha.Contains("TRAVOU") || linha.Contains(" caiu")
               || linha.Contains("Não consegui") || linha.Contains("falhou") || linha.Contains("caiu para a CPU") || linha.Contains("Reabrindo a captura");
    }

    /// <summary>O tipo da falha grave ("fatal", "ui", "captura_travou", "captura_reaberta", "caiu_para_cpu") ou nulo se a linha não é grave.</summary>
    public static string? Classificar(string linha)
    {
        if (linha.Contains("ERRO (fatal)")) return "fatal";
        if (linha.Contains("ERRO (UI)")) return "ui";
        if (linha.Contains("TRAVOU")) return "captura_travou";
        if (linha.Contains("Reabrindo a captura")) return "captura_reaberta";
        if (linha.Contains("caiu para a CPU")) return "caiu_para_cpu";
        return null;
    }

    /// <summary>Quem decide se um relatório automático de falha pode sair agora. Não é thread-safe: quem chama (interface e a thread da exceção fatal) trava um lock.</summary>
    public sealed class Freio
    {
        readonly Dictionary<string, long> _ultimoPorTipo = new();
        long _ultimoGeralMs = long.MinValue;
        bool _algum;
        int _emitidos;

        public TimeSpan EntreMesmoTipo { get; init; } = TimeSpan.FromMinutes(2);
        public TimeSpan EntreQuaisquer { get; init; } = TimeSpan.FromSeconds(20);
        public int MaximoPorSessao { get; init; } = 10;
        public int Emitidos => _emitidos;

        /// <summary>Vale (e conta) se o tipo não saiu há pouco, nada saiu há pouco e a sessão não passou do teto. "fatal" nunca é freado: é a última chance.</summary>
        public bool Permitir(string tipo, long agoraMs)
        {
            if (tipo != "fatal")
            {
                if (_emitidos >= MaximoPorSessao) return false;
                if (_algum && agoraMs - _ultimoGeralMs < EntreQuaisquer.TotalMilliseconds) return false;
                if (_ultimoPorTipo.TryGetValue(tipo, out long t) && agoraMs - t < EntreMesmoTipo.TotalMilliseconds) return false;
            }
            _ultimoPorTipo[tipo] = agoraMs; _ultimoGeralMs = agoraMs; _algum = true; _emitidos++;
            return true;
        }
    }
}
