using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Streemu.Arquivos;

public enum Sentido { Recebido, Enviado }

public enum Resultado { Conferido, Recusado, Expirou, Cancelado, Falhou, Parou }

/// <summary>
/// Uma linha do histórico (spec 020). <paramref name="Caminho"/>: no recebido, onde o arquivo ficou (Abrir, Mostrar na pasta);
/// no enviado, de onde saiu (o "Pedir de novo" confere se ainda é o mesmo arquivo, D6). Fica só neste PC.
/// </summary>
public sealed record ItemHistorico(
    Sentido Sentido, string Pedido, string AmigoId, string Apelido, string Nome, long Tamanho, string Identidade,
    DateTime Quando, Resultado Resultado, string? Caminho);

/// <summary>
/// Histórico de arquivos recebidos e enviados e a pasta de recebidos, num arquivo próprio (<c>arquivos.json</c>) e não no
/// <c>estado.json</c>: o histórico cresce e muda a cada envio, e o <c>AppState</c> é regravado inteiro por outras partes do app.
/// Guarda os últimos <see cref="MaxItens"/> ou <see cref="MaxDias"/> dias, o que vier primeiro (D8).
/// Ex.: <c>var h = new Historico(Historico.CaminhoPadrao(), () => DateTime.Now, Log.Info); h.Registrar(item);</c>
/// </summary>
public sealed class Historico
{
    public const int MaxItens = 200, MaxDias = 90;

    sealed class Gravado
    {
        public int V { get; set; } = 1;
        public string? Pasta { get; set; }
        public List<ItemHistorico> Itens { get; set; } = new();
    }

    static readonly JsonSerializerOptions Formato = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    readonly string? _arquivo;
    readonly Func<DateTime> _agora;
    readonly Action<string> _log;
    readonly object _trava = new();
    Gravado _dados;

    /// <param name="arquivo">Onde gravar; nulo = só na memória (STREEMU_NOSTATE=1).</param>
    public Historico(string? arquivo, Func<DateTime> agora, Action<string> log)
    {
        _arquivo = arquivo; _agora = agora; _log = log;
        _dados = Carregar();
        Cortar();
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\Streemu\arquivos.json</c>; nos testes, <c>&lt;STREEMU_TEST_DIR&gt;\&lt;papel&gt;-arquivos.json</c>, para nunca
    /// misturar com o histórico real; com STREEMU_NOSTATE=1, nulo (nada é lido nem gravado).
    /// </summary>
    public static string? CaminhoPadrao()
    {
        if (Environment.GetEnvironmentVariable("STREEMU_NOSTATE") == "1") return null;
        if (Environment.GetEnvironmentVariable("STREEMU_TEST_DIR") is string teste)
            return Path.Combine(teste, (Environment.GetEnvironmentVariable("STREEMU_TEST_ROLE") ?? "A") + "-arquivos.json");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streemu", "arquivos.json");
    }

    /// <summary>Do mais novo para o mais velho.</summary>
    public IReadOnlyList<ItemHistorico> Itens { get { lock (_trava) return _dados.Itens.ToList(); } }

    /// <summary>A pasta de recebidos escolhida; sem escolha, <c>Downloads\Streemu</c>.</summary>
    public string PastaRecebidos
    {
        get { lock (_trava) return _dados.Pasta is { Length: > 0 } p ? p : PastaPadrao(); }
        set { lock (_trava) _dados.Pasta = value; Salvar(); }
    }

    /// <summary>Acrescenta no topo, ou troca a linha do mesmo pedido e arquivo (um envio que parou e depois terminou vira uma linha só).</summary>
    public void Registrar(ItemHistorico item)
    {
        lock (_trava)
        {
            _dados.Itens.RemoveAll(i => i.Sentido == item.Sentido && i.Pedido == item.Pedido && i.Identidade == item.Identidade);
            _dados.Itens.Insert(0, item);
        }
        Cortar();
        Salvar();
    }

    /// <summary>"Limpar histórico" (D8): apaga as linhas, mantém a pasta escolhida.</summary>
    public void Limpar()
    {
        lock (_trava) _dados.Itens.Clear();
        Salvar();
    }

    void Cortar()
    {
        DateTime limite = _agora().AddDays(-MaxDias);
        lock (_trava)
        {
            _dados.Itens.RemoveAll(i => i.Quando < limite);
            if (_dados.Itens.Count > MaxItens) _dados.Itens.RemoveRange(MaxItens, _dados.Itens.Count - MaxItens);
        }
    }

    Gravado Carregar()
    {
        if (_arquivo == null || !File.Exists(_arquivo)) return new Gravado();
        try { return JsonSerializer.Deserialize<Gravado>(Telemetria.CofreLocal.LerArquivo(_arquivo), Formato) ?? new Gravado(); } // cifrado desde a A8 do plano 018 (tem nome de amigo e caminho de arquivo); o antigo, em claro, também abre
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _log($"[arquivo] histórico ilegível, começo vazio: {e.Message}");
            return new Gravado();
        }
    }

    /// <summary>Grava num .tmp e troca: o arquivo nunca fica pela metade se o app fechar no meio (o mesmo cuidado do AppState.Save).</summary>
    void Salvar()
    {
        if (_arquivo == null) return;
        try
        {
            string texto;
            lock (_trava) texto = JsonSerializer.Serialize(_dados, Formato);
            Directory.CreateDirectory(Path.GetDirectoryName(_arquivo)!);
            File.WriteAllText(_arquivo + ".tmp", Telemetria.CofreLocal.Arquivo(texto));
            File.Move(_arquivo + ".tmp", _arquivo, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log($"[arquivo] não consegui gravar o histórico: {e.Message}"); }
    }

    // a pasta Downloads pode ter sido movida (para o OneDrive, outro disco): pergunta ao Windows em vez de supor %USERPROFILE%\Downloads
    static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    static extern string SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token);

    static string PastaPadrao()
    {
        string downloads;
        try { downloads = SHGetKnownFolderPath(Downloads, 0, IntPtr.Zero); }
        catch (COMException) { downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); }
        return Path.Combine(downloads, "Streemu");
    }
}
