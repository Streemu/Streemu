using System.Text.RegularExpressions;
using Streemu.Util;

// Testes do leitor das novidades e do conteúdo do NOVIDADES.md (plano 027, etapa 1). Sem janela, sem rede, sem estado salvo.
// Cada caso imprime "OK    novidades-nome" ou "FALHA novidades-nome : motivo".

int falhas = 0;
void Caso(string nome, Action f)
{
    try { f(); Console.WriteLine("OK    novidades-" + nome); }
    catch (Exception e) { falhas++; Console.WriteLine($"FALHA novidades-{nome} : {e.Message}"); }
}
void Exige(bool ok, string msg) { if (!ok) throw new Exception(msg); }

IReadOnlyList<VersaoNovidade> Le(string texto, out List<string> avisos)
{
    var a = new List<string>();
    var r = Novidades.Ler(texto, a.Add);
    avisos = a;
    return r;
}

string RaizDoRepositorio()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "app", "Streemu", "NOVIDADES.md"))) return d.FullName;
    throw new Exception("não achei app/Streemu/NOVIDADES.md subindo a partir de " + AppContext.BaseDirectory);
}
string raiz = RaizDoRepositorio();
string textoReal = File.ReadAllText(Path.Combine(raiz, "app", "Streemu", "NOVIDADES.md"), System.Text.Encoding.UTF8);

// ---------- conteúdo real ----------

Caso("arquivo-real-sem-aviso", () =>
{
    var v = Le(textoReal, out var avisos);
    Exige(avisos.Count == 0, "o arquivo real gerou avisos: " + string.Join(" | ", avisos));
    Exige(v.Count > 0, "o arquivo real não tem nenhuma versão");
});

Caso("embutido-igual-ao-disco", () =>
{
    var disco = Le(textoReal, out _);
    var emb = Novidades.Embutidas();
    Exige(emb.Count == disco.Count, $"o recurso embutido tem {emb.Count} versões e o disco {disco.Count}");
    for (int i = 0; i < disco.Count; i++)
    {
        Exige(emb[i].Versao == disco[i].Versao && emb[i].Itens.Count == disco[i].Itens.Count, "versão diferente em " + disco[i].Versao);
        for (int j = 0; j < disco[i].Itens.Count; j++)
            Exige(emb[i].Itens[j] == disco[i].Itens[j], "item diferente em " + disco[i].Versao + ": " + disco[i].Itens[j].Titulo);
    }
    Exige(ReferenceEquals(emb, Novidades.Embutidas()), "a segunda chamada devia devolver o mesmo resultado em cache");
});

Caso("versao-do-csproj-tem-secao", () =>
{
    string csproj = File.ReadAllText(Path.Combine(raiz, "app", "Streemu", "Streemu.csproj"));
    var m = Regex.Match(csproj, @"<Version>\s*([^<\s]+)\s*</Version>");
    Exige(m.Success, "não achei <Version> no Streemu.csproj");
    string versao = m.Groups[1].Value;
    Exige(Le(textoReal, out _).Any(x => x.Versao == versao), $"o NOVIDADES.md não tem a seção da versão {versao} do csproj");
});

Caso("limites-de-tamanho", () =>
{
    foreach (var v in Le(textoReal, out _))
    {
        Exige(v.Resumo.Length <= 120, $"resumo da {v.Versao} tem {v.Resumo.Length} caracteres (máximo 120)");
        foreach (var i in v.Itens)
        {
            Exige(i.Titulo.Length <= 70, $"título com {i.Titulo.Length} caracteres (máximo 70) na {v.Versao}: {i.Titulo}");
            Exige(i.Detalhe.Length <= 220, $"detalhe com {i.Detalhe.Length} caracteres (máximo 220) na {v.Versao}: {i.Titulo}");
        }
    }
});

Caso("toda-versao-tem-item", () =>
{
    foreach (var v in Le(textoReal, out _))
        Exige(v.Itens.Count > 0, $"a versão {(v.Versao == "" ? "Não lançado" : v.Versao)} não tem item");
});

Caso("versoes-sem-repeticao-e-em-ordem", () =>
{
    var v = Le(textoReal, out _).Where(x => !x.NaoLancado).ToList();
    Exige(v.Select(x => x.Versao).Distinct().Count() == v.Count, "versão repetida");
    for (int i = 1; i < v.Count; i++)
        Exige(Version.Parse(v[i - 1].Versao) > Version.Parse(v[i].Versao), $"{v[i - 1].Versao} devia vir antes de {v[i].Versao} (mais novas primeiro)");
});

Caso("titulos-sem-repeticao", () =>
{
    // a chave é só do título: dois itens com o mesmo título seriam vistos juntos
    var chaves = Le(textoReal, out _).SelectMany(v => v.Itens).GroupBy(i => i.Chave).Where(g => g.Count() > 1).Select(g => g.First().Titulo).ToList();
    Exige(chaves.Count == 0, "títulos repetidos: " + string.Join(" | ", chaves));
});

// ---------- leitor: casos de borda ----------

Caso("versao-completa", () =>
{
    var v = Le("# Novidades\n\n## 1.2.3 — 2026-11-20\nResumo da versão.\n\n### Novo\n- **Selo:** um ponto no botão.\n\n### Correções\n- **Corrige:** o erro.\n", out var av);
    Exige(av.Count == 0, "avisos: " + string.Join("|", av));
    Exige(v.Count == 1 && v[0].Versao == "1.2.3" && !v[0].NaoLancado && !v[0].Obrigatoria, "cabeçalho errado");
    Exige(v[0].Data == new DateOnly(2026, 11, 20), "data errada");
    Exige(v[0].Resumo == "Resumo da versão.", "resumo errado: " + v[0].Resumo);
    Exige(v[0].Itens.Count == 2 && v[0].Itens[0].Grupo == "Novo" && v[0].Itens[1].Grupo == "Correções", "grupos errados");
    Exige(v[0].Itens[0].Titulo == "Selo" && v[0].Itens[0].Detalhe == "um ponto no botão.", "título/detalhe errados: " + v[0].Itens[0]);
});

Caso("tracos-entre-versao-e-data", () =>
{
    foreach (string t in new[] { "—", "–", "-" })
    {
        var v = Le($"## 0.1.0 {t} 2026-01-02\n- **A:** b.\n", out var av);
        Exige(av.Count == 0 && v.Count == 1 && v[0].Data == new DateOnly(2026, 1, 2), $"travessão '{t}' não foi aceito");
    }
});

Caso("sem-data", () =>
{
    var v = Le("## 0.1.0\n- **A:** b.\n", out var av);
    Exige(av.Count == 0 && v.Count == 1 && v[0].Data == null && v[0].Versao == "0.1.0", "versão sem data devia ser aceita");
});

Caso("data-invalida-avisa-e-segue", () =>
{
    var v = Le("## 0.1.0 — 2026-13-45\n- **A:** b.\n", out var av);
    Exige(av.Count == 1 && v.Count == 1 && v[0].Data == null && v[0].Itens.Count == 1, "data inválida devia dar um aviso e manter a versão");
});

Caso("item-sem-negrito", () =>
{
    var v = Le("## 0.1.0\n- Só uma linha.\n", out var av);
    Exige(av.Count == 0 && v[0].Itens[0].Titulo == "Só uma linha." && v[0].Itens[0].Detalhe == "", "a linha inteira devia virar o título");
});

Caso("dois-pontos-fora-do-negrito", () =>
{
    var v = Le("## 0.1.0\n- **Título**: detalhe.\n", out _);
    Exige(v[0].Itens[0].Titulo == "Título" && v[0].Itens[0].Detalhe == "detalhe.", "título/detalhe errados: " + v[0].Itens[0]);
});

Caso("continuacao-recuada", () =>
{
    var v = Le("## 0.1.0\n- **Título:** primeira parte\n  segunda parte\n    terceira\n- **Outro:** x\n", out var av);
    Exige(av.Count == 0 && v[0].Itens.Count == 2, "devia ter 2 itens e nenhum aviso");
    Exige(v[0].Itens[0].Detalhe == "primeira parte segunda parte terceira", "continuação não juntou: " + v[0].Itens[0].Detalhe);
});

Caso("grupo-desconhecido", () =>
{
    var v = Le("## 0.1.0\n### Segurança\n- **A:** b.\n### correções\n- **C:** d.\n", out var av);
    Exige(av.Count == 0 && v[0].Itens[0].Grupo == "Segurança", "grupo desconhecido devia ficar com o texto do ###");
    Exige(v[0].Itens[1].Grupo == "Correções", "grupo conhecido devia ser normalizado");
});

Caso("obrigatoria", () =>
{
    var v = Le("## 0.5.0 — 2026-11-19\n!obrigatoria\nResumo.\n- **A:** b.\n## 0.4.0\n- **C:** d.\n", out var av);
    Exige(av.Count == 0 && v[0].Obrigatoria && !v[1].Obrigatoria, "!obrigatoria errado");
    Exige(v[0].Resumo == "Resumo.", "o resumo depois do !obrigatoria devia valer: " + v[0].Resumo);
});

Caso("nao-lancado-vazio-omitido", () =>
{
    var v = Le("# Novidades\n\n## Não lançado\n\n## 0.1.0\n- **A:** b.\n", out var av);
    Exige(av.Count == 0 && v.Count == 1 && v[0].Versao == "0.1.0", "o Não lançado vazio devia sumir");
});

Caso("nao-lancado-com-itens", () =>
{
    var v = Le("## Não lançado\n### Novo\n- **A:** b.\n## 0.1.0\n- **C:** d.\n", out var av);
    Exige(av.Count == 0 && v.Count == 2 && v[0].NaoLancado && v[0].Versao == "" && v[0].Data == null && v[0].Itens.Count == 1, "o Não lançado com itens devia vir primeiro, com Versao vazia");
    Exige(!v[1].NaoLancado, "a 0.1.0 não é Não lançado");
    var w = Le("## NAO LANCADO\n- **A:** b.\n", out _);
    Exige(w.Count == 1 && w[0].NaoLancado, "o título devia ser aceito sem acento nem caixa");
});

Caso("comentarios-html", () =>
{
    var v = Le("<!-- uma linha -->\n# Novidades\n<!--\n## 9.9.9\n- **Fantasma:** não existe.\nvárias linhas\n-->\n## 0.1.0\n- **A:** b. <!-- no meio --> c.\n", out var av);
    Exige(av.Count == 0 && v.Count == 1 && v[0].Versao == "0.1.0", "o comentário de várias linhas não foi ignorado");
    Exige(v[0].Itens[0].Detalhe.Contains("b.") && !v[0].Itens[0].Detalhe.Contains("no meio"), "o comentário no meio da linha não foi tirado");
});

Caso("texto-solto-vira-um-aviso-por-linha", () =>
{
    var v = Le("texto solto antes\n## 0.1.0\n- **A:** b.\nlinha solta depois\n## sem versão nenhuma\n- **órfão:** x\n", out var av);
    Exige(v.Count == 1 && v[0].Itens.Count == 1, "o que se entende devia continuar valendo");
    Exige(av.Count == 4, $"devia ter 4 avisos (um por linha ignorada) e teve {av.Count}: " + string.Join(" | ", av));
});

Caso("nunca-lanca", () =>
{
    Exige(Novidades.Ler("", null).Count == 0, "vazio");
    Exige(Novidades.Ler("\u0000\u0001 ## ### - ** <!--", null).Count == 0, "texto binário");
    Exige(Novidades.Ler(null!, null).Count == 0, "nulo");
    var v = Novidades.Ler("texto solto\n## 0.1.0\n- **A:** b.\n", _ => throw new InvalidOperationException("o callback falhou"));
    Exige(v.Count == 1, "um callback que lança não pode derrubar a leitura");
});

Caso("crlf", () =>
{
    var v = Le("## 0.1.0 — 2026-01-02\r\nResumo.\r\n\r\n### Novo\r\n- **A:** b.\r\n", out var av);
    Exige(av.Count == 0 && v[0].Resumo == "Resumo." && v[0].Itens[0].Detalhe == "b.", "terminadores CRLF não foram tratados");
});

// ---------- chave ----------

Caso("chave-formato", () =>
{
    string c = Novidades.Chave("Selo da atividade");
    Exige(Regex.IsMatch(c, "^[0-9a-f]{12}$"), "a chave devia ter 12 hex minúsculos: " + c);
    Exige(Novidades.Chave("Selo da atividade") == c, "a chave devia ser estável");
    Exige(Novidades.Chave("Outro título") != c, "títulos diferentes, chaves diferentes");
});

Caso("chave-ignora-espacos-caixa-e-dois-pontos", () =>
{
    string a = Novidades.Chave("Selo da atividade");
    Exige(Novidades.Chave("  SELO   da\tatividade  ") == a, "espaços e maiúsculas não podem mudar a chave");
    Exige(Novidades.Chave("Selo da atividade:") == a, "o : final não pode mudar a chave");
    Exige(Novidades.Chave("Selo da atividade") != Novidades.Chave("Selo de atividade"), "palavra diferente muda a chave");
});

Caso("chave-vem-so-do-titulo", () =>
{
    var v = Le("## Não lançado\n- **A:** x.\n## 0.5.2\n- **A:** x.\n", out _);
    var w = Le("## 0.5.2 — 2026-11-21\n- **a:** outro detalhe.\n", out _);
    Exige(v[0].Itens[0].Chave == v[1].Itens[0].Chave && v[0].Itens[0].Chave == w[0].Itens[0].Chave, "renomear a seção ou mudar o detalhe não pode mudar a chave");
});

// ---------- lido e não lido ----------

var amostra = Le("## 0.2.0\n- **A:** a.\n- **B:** b.\n## 0.1.0\n- **C:** c.\n", out _);
string kA = Novidades.Chave("A"), kB = Novidades.Chave("B"), kC = Novidades.Chave("C");

Caso("nao-lidas-nulo-e-zero", () => Exige(Novidades.NaoLidas(amostra, null) == 0, "nulo (primeira abertura) devia dar 0"));

Caso("nao-lidas-vazio-parcial-e-tudo", () =>
{
    Exige(Novidades.NaoLidas(amostra, new List<string>()) == 3, "lista vazia: tudo é novo");
    Exige(Novidades.NaoLidas(amostra, new[] { kA }) == 2, "uma vista: 2 novas");
    Exige(Novidades.NaoLidas(amostra, new[] { kA, kB, kC, "outra-chave" }) == 0, "tudo visto: 0");
    Exige(Novidades.NaoLidas(Array.Empty<VersaoNovidade>(), new List<string>()) == 0, "sem versões: 0");
});

Caso("marcar-vistas-primeira-vez", () =>
{
    var r = Novidades.MarcarVistas(amostra, null);
    Exige(r.Count == 3 && r.SequenceEqual(new[] { kA, kB, kC }), "devia ter as 3 chaves, na ordem da tela");
    Exige(Novidades.NaoLidas(amostra, r) == 0, "depois de marcar não sobra nada");
});

Caso("marcar-vistas-junta-sem-repetir-atuais-primeiro", () =>
{
    var r = Novidades.MarcarVistas(amostra, new[] { "antiga1", kB, "antiga2", "antiga1", "" });
    Exige(r.Take(3).SequenceEqual(new[] { kA, kB, kC }), "as atuais vêm primeiro: " + string.Join(",", r));
    Exige(r.Skip(3).SequenceEqual(new[] { "antiga1", "antiga2" }) && r.Count == 5, "as antigas vêm depois, sem repetir: " + string.Join(",", r));
});

Caso("marcar-vistas-limite-de-400", () =>
{
    var antigas = Enumerable.Range(0, 1000).Select(i => "antiga" + i).ToList();
    var r = Novidades.MarcarVistas(amostra, antigas);
    Exige(r.Count == 400, "devia limitar a 400 e deu " + r.Count);
    Exige(r.Take(3).SequenceEqual(new[] { kA, kB, kC }), "as atuais nunca caem");
    Exige(r[3] == "antiga0" && r[399] == "antiga396", "caem as antigas do fim da lista");
    Exige(Novidades.NaoLidas(amostra, r) == 0, "as atuais continuam vistas");
});

return falhas == 0 ? 0 : 1;
