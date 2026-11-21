using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Streemu.Util;

/// <summary>Um item da tela Novidades. <see cref="Chave"/> identifica o item para saber se a pessoa já viu (só do título, sem a versão).</summary>
public sealed record ItemNovidade(string Grupo, string Titulo, string Detalhe, string Chave);

/// <summary>Uma versão da tela Novidades. <see cref="Versao"/> é "" (e <see cref="NaoLancado"/> verdadeiro) para os itens que já estão no app mas ainda não têm número.</summary>
public sealed record VersaoNovidade(string Versao, DateOnly? Data, string Resumo, bool Obrigatoria, bool NaoLancado, IReadOnlyList<ItemNovidade> Itens);

/// <summary>
/// Texto da tela "Novidades" (plano 027). Vem do <c>app/Streemu/NOVIDADES.md</c>, embutido no exe, e não do <c>CHANGELOG.md</c> da raiz por três motivos
/// (plano 027, D1): o exe entregue é compilado no repositório público, que não recebe o CHANGELOG da raiz; o CHANGELOG cita nomes do grupo, e a trava de
/// nomes da publicação pararia a entrega de todo mundo; e ele é escrito para quem desenvolve, com causa técnica, o que na tela vira parede de texto.
/// O leitor é tolerante de propósito: arquivo quebrado nunca derruba o app, só deixa a tela com menos (ou nenhum) conteúdo.
/// </summary>
public static class Novidades
{
    const int LimiteVistas = 400;
    const string Recurso = "Streemu.NOVIDADES.md";

    static readonly Regex Comentarios = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex CabecalhoVersao = new(@"^(?<v>\d+(?:\.\d+){1,3})\s*(?:[—–\-]\s*(?<d>\S+))?$", RegexOptions.Compiled);
    static readonly Regex ItemNegrito = new(@"^\*\*(?<t>.+?)\*\*\s*(?<d>.*)$", RegexOptions.Compiled);
    static readonly Regex Espacos = new(@"\s+", RegexOptions.Compiled);

    static readonly object Gate = new();
    static IReadOnlyList<VersaoNovidade>? _embutidas;

    sealed class VersaoEmAndamento
    {
        public string Versao = "";
        public DateOnly? Data;
        public string Resumo = "";
        public bool Obrigatoria;
        public bool NaoLancado;
        public readonly List<ItemEmAndamento> Itens = new();
    }

    sealed class ItemEmAndamento
    {
        public string Grupo = "", Titulo = "", Chave = "";
        public readonly StringBuilder Detalhe = new();
    }

    /// <summary>Lê o formato do NOVIDADES.md. Linha que não entende é ignorada e vira um aviso (um por linha); nunca lança.</summary>
    public static IReadOnlyList<VersaoNovidade> Ler(string texto, Action<string>? aviso = null)
    {
        var resultado = new List<VersaoNovidade>();
        void Avisar(string m) { try { aviso?.Invoke(m); } catch { /* o aviso é só diagnóstico: não pode derrubar a leitura */ } }
        try
        {
            texto = Comentarios.Replace(texto ?? "", "");
            VersaoEmAndamento? versao = null;
            string grupo = "";
            ItemEmAndamento? ultimo = null;

            void Fechar()
            {
                if (versao == null) return;
                // "Não lançado" vazio é o estado normal entre duas versões: não aparece na tela
                if (!(versao.NaoLancado && versao.Itens.Count == 0))
                {
                    var itens = versao.Itens.Select(i => new ItemNovidade(i.Grupo, i.Titulo, i.Detalhe.ToString(), i.Chave)).ToList();
                    resultado.Add(new VersaoNovidade(versao.Versao, versao.Data, versao.Resumo, versao.Obrigatoria, versao.NaoLancado, itens));
                }
                versao = null;
            }

            var linhas = texto.Split('\n');
            for (int n = 0; n < linhas.Length; n++)
            {
                string bruta = linhas[n].TrimEnd('\r');
                string linha = bruta.Trim();
                if (linha.Length == 0) { ultimo = null; continue; }
                string onde = $"linha {n + 1}";

                if (linha.StartsWith("### ", StringComparison.Ordinal))
                {
                    if (versao == null) { Avisar($"{onde}: grupo fora de uma versão, ignorado: {Cortar(linha)}"); continue; }
                    grupo = NomeDoGrupo(linha[4..].Trim());
                    ultimo = null;
                    continue;
                }
                if (linha.StartsWith("## ", StringComparison.Ordinal))
                {
                    Fechar();
                    grupo = ""; ultimo = null;
                    string titulo = linha[3..].Trim();
                    if (SemAcento(titulo).Equals("nao lancado", StringComparison.OrdinalIgnoreCase))
                    {
                        versao = new VersaoEmAndamento { NaoLancado = true };
                        continue;
                    }
                    var m = CabecalhoVersao.Match(titulo);
                    if (!m.Success) { Avisar($"{onde}: título de versão que não entendi, a seção inteira foi ignorada: {Cortar(linha)}"); continue; }
                    versao = new VersaoEmAndamento { Versao = m.Groups["v"].Value };
                    if (m.Groups["d"].Success)
                    {
                        if (DateOnly.TryParseExact(m.Groups["d"].Value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) versao.Data = d;
                        else Avisar($"{onde}: data que não entendi (use AAAA-MM-DD): {m.Groups["d"].Value}");
                    }
                    continue;
                }
                if (linha.StartsWith("# ", StringComparison.Ordinal)) continue; // título do arquivo

                if (versao == null) { Avisar($"{onde}: linha fora de uma versão, ignorada: {Cortar(linha)}"); continue; }

                if (linha.Equals("!obrigatoria", StringComparison.OrdinalIgnoreCase) || linha.Equals("!obrigatória", StringComparison.OrdinalIgnoreCase))
                {
                    versao.Obrigatoria = true; ultimo = null;
                    continue;
                }

                bool recuada = bruta.Length > 0 && char.IsWhiteSpace(bruta[0]);
                if (ultimo != null && recuada)
                {
                    // continuação do item acima (linha recuada): vira parte do detalhe
                    if (ultimo.Detalhe.Length > 0) ultimo.Detalhe.Append(' ');
                    ultimo.Detalhe.Append(linha);
                    continue;
                }

                if (linha.StartsWith("- ", StringComparison.Ordinal) || linha.StartsWith("* ", StringComparison.Ordinal))
                {
                    string corpo = linha[2..].Trim();
                    string tituloItem, detalhe;
                    var mi = ItemNegrito.Match(corpo);
                    if (mi.Success)
                    {
                        tituloItem = mi.Groups["t"].Value.Trim().TrimEnd(':').Trim();
                        detalhe = mi.Groups["d"].Value.Trim().TrimStart(':').Trim();
                    }
                    else { tituloItem = corpo; detalhe = ""; }
                    if (tituloItem.Length == 0) { Avisar($"{onde}: item sem título, ignorado: {Cortar(linha)}"); ultimo = null; continue; }
                    var item = new ItemEmAndamento { Grupo = grupo, Titulo = tituloItem, Chave = Chave(tituloItem) };
                    item.Detalhe.Append(detalhe);
                    versao.Itens.Add(item);
                    ultimo = item;
                    continue;
                }

                // texto solto logo abaixo do título da versão, antes de qualquer grupo ou item, é o resumo
                if (grupo.Length == 0 && versao.Itens.Count == 0)
                {
                    versao.Resumo = versao.Resumo.Length == 0 ? linha : versao.Resumo + " " + linha;
                    continue;
                }
                Avisar($"{onde}: linha que não entendi, ignorada: {Cortar(linha)}");
            }
            Fechar();
        }
        catch (Exception e) { Avisar("leitura interrompida: " + e.Message); }
        return resultado;
    }

    /// <summary>O texto embutido no exe (<c>Streemu.NOVIDADES.md</c>), lido uma vez. Se faltar ou falhar, lista vazia e uma linha no log.</summary>
    public static IReadOnlyList<VersaoNovidade> Embutidas()
    {
        lock (Gate)
        {
            if (_embutidas != null) return _embutidas;
            try
            {
                using var s = typeof(Novidades).Assembly.GetManifestResourceStream(Recurso);
                if (s == null) { Log.Info("Novidades: o texto embutido não está neste exe."); return _embutidas = Array.Empty<VersaoNovidade>(); }
                using var r = new StreamReader(s, Encoding.UTF8);
                _embutidas = Ler(r.ReadToEnd(), m => Log.Info("Novidades: " + m));
            }
            catch (Exception e)
            {
                Log.Info("Novidades: não consegui ler o texto embutido: " + e.Message);
                _embutidas = Array.Empty<VersaoNovidade>();
            }
            return _embutidas;
        }
    }

    /// <summary>
    /// Identificador de um item: 12 hex minúsculos do SHA-1 do título normalizado (sem espaço nas pontas ou em sequência, minúsculas, sem ":" no fim).
    /// Só o título, sem a versão, para o item não voltar a parecer novo quando a release renomeia "Não lançado".
    /// </summary>
    public static string Chave(string titulo)
    {
        string t = Espacos.Replace((titulo ?? "").Trim().ToLowerInvariant(), " ").TrimEnd(':').Trim();
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(t)))[..12];
    }

    /// <summary>Quantos itens ainda não foram vistos. Nulo = nunca abriu uma versão com Novidades (a primeira vez marca tudo como visto, sem selo): 0.</summary>
    public static int NaoLidas(IReadOnlyList<VersaoNovidade> versoes, IReadOnlyCollection<string>? vistas)
    {
        if (vistas == null) return 0;
        var vistos = vistas as ISet<string> ?? new HashSet<string>(vistas);
        return versoes.Sum(v => v.Itens.Count(i => !vistos.Contains(i.Chave)));
    }

    /// <summary>As chaves de agora seguidas das antigas, sem repetir e no máximo 400 (as de agora têm prioridade, as mais antigas caem primeiro).</summary>
    public static List<string> MarcarVistas(IReadOnlyList<VersaoNovidade> versoes, IEnumerable<string>? vistasAntes)
    {
        var lista = new List<string>();
        var jaTem = new HashSet<string>();
        foreach (var v in versoes)
            foreach (var i in v.Itens)
                if (jaTem.Add(i.Chave)) lista.Add(i.Chave);
        if (vistasAntes != null)
            foreach (var k in vistasAntes)
                if (!string.IsNullOrEmpty(k) && jaTem.Add(k)) lista.Add(k);
        if (lista.Count > LimiteVistas) lista.RemoveRange(LimiteVistas, lista.Count - LimiteVistas);
        return lista;
    }

    static string NomeDoGrupo(string texto) => SemAcento(texto).ToLowerInvariant() switch
    {
        "novo" => "Novo",
        "melhorias" => "Melhorias",
        "correcoes" => "Correções",
        _ => texto,
    };

    static string SemAcento(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (char c in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    static string Cortar(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
