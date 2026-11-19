using Streemu.Filme;
using Streemu.Ui;
using Streemu.Util;

namespace Streemu.Arquivos;

/// <summary>
/// O que a tela mostra de cada pedido e de cada linha do histórico (spec 020, mockups 1 a 3): títulos, linha de apoio, selo e
/// botões. Puro: recebe o pedido e a hora, devolve o desenho; os códigos das ações são os que o MainForm.ArquivosTela.cs trata.
/// Ex.: <c>TextoDoPedido.Linha(pedido, DateTime.Now)</c>.
/// </summary>
public static class TextoDoPedido
{
    public const string Aceitar = "aceitar", Recusar = "recusar", Cancelar = "cancelar", Mandar = "mandar";
    public const string RelaySim = "relay-sim", RelayNao = "relay-nao", Abrir = "abrir", Pasta = "pasta", DeNovo = "de-novo";

    static readonly AcaoDaLinha[] SoCancelar = [new("Cancelar", false, Cancelar)];

    /// <summary>O índice da cor do avatar: o mesmo amigo, a mesma cor, durante a sessão.</summary>
    public static int Cor(string amigoId) => amigoId.Aggregate(7, (h, c) => unchecked(h * 31 + c)) & int.MaxValue;

    public static string Letra(string apelido) => apelido.Length > 0 ? char.ToUpperInvariant(apelido[0]).ToString() : "?";

    /// <summary>"mumu.png", "a.png e b.zip" ou "a.png e mais 2".</summary>
    public static string Nomes(OfertaDeArquivos o) => Nomes(o.Arquivos.Select(a => a.Nome).ToList());

    /// <summary>
    /// Como <see cref="Nomes(OfertaDeArquivos)"/>, mas, no que já chegou, com o nome que ficou gravado na pasta: o que o amigo anunciou
    /// ("foto.png") pode não ser o que foi gravado ("foto.png.exe"), e a tela precisa mostrar a extensão real.
    /// </summary>
    static string Nomes(PedidoEmCurso p)
    {
        bool gravados = p.Sentido == Sentido.Recebido && p.Caminhos.Count == p.Oferta.Arquivos.Count && p.Oferta.Arquivos.All(a => p.Caminhos.ContainsKey(a.Identidade));
        return gravados ? Nomes(p.Oferta.Arquivos.Select(a => Path.GetFileName(p.Caminhos[a.Identidade])).ToList()) : Nomes(p.Oferta);
    }

    // sempre ParaMostrar: o nome vem do outro PC e pode trazer caracteres que invertem o texto e escondem a extensão
    static string Nomes(IReadOnlyList<string> nomes) => nomes.Count switch
    {
        1 => NomeSeguro.ParaMostrar(nomes[0]),
        2 => $"{NomeSeguro.ParaMostrar(nomes[0])} e {NomeSeguro.ParaMostrar(nomes[1])}",
        _ => $"{NomeSeguro.ParaMostrar(nomes[0])} e mais {nomes.Count - 1}",
    };

    static string Tamanho(long bytes) => EnviarArquivoView.TextoDoTamanho(bytes);

    static int MinutosAte(PedidoEmCurso p, DateTime desde, DateTime agora) =>
        Math.Max(1, (int)Math.Ceiling((PedidoEmCurso.Validade - (agora - desde)).TotalMinutes));

    /// <summary>Esperando uma resposta minha: vira card no Início e conta no "Enviar arquivo · N" da barra.</summary>
    public static bool EsperaPorMim(PedidoEmCurso p) =>
        p.Fase is FaseArquivo.PedidoDeNovo or FaseArquivo.ConfirmandoRelay || p.Sentido == Sentido.Recebido && p.Fase == FaseArquivo.Esperando;

    // ------------------------------------------------------------------ "Em andamento"

    public static DadosDaLinha Linha(PedidoEmCurso p, DateTime agora)
    {
        string titulo = p.Fase == FaseArquivo.PedidoDeNovo ? $"{p.Apelido} quer {Nomes(p.Oferta)} de novo"
            : p.Sentido == Sentido.Enviado ? $"{Nomes(p.Oferta)} para {p.Apelido}" : $"{Nomes(p)} de {p.Apelido}";
        if (p.Terminado) return Fim(p, titulo);
        var (apoio, acoes) = EmCurso(p, agora);
        int? pct = p.Fase == FaseArquivo.Transferindo ? Porcentagem(p) : null;
        return new DadosDaLinha(Letra(p.Apelido), Cor(p.AmigoId), titulo, apoio, null, TomDoSelo.Nenhum, pct, acoes);
    }

    static (string, IReadOnlyList<AcaoDaLinha>) EmCurso(PedidoEmCurso p, DateTime agora) => p.Fase switch
    {
        FaseArquivo.Esperando when p.Sentido == Sentido.Enviado =>
            ($"Esperando {p.Apelido} aceitar · expira em {MinutosAte(p, p.Criado, agora)} min", [new("Cancelar pedido", false, Cancelar)]),
        FaseArquivo.Esperando =>
            ($"{Tamanho(p.Oferta.Total)} · expira em {MinutosAte(p, p.Criado, agora)} min", [new("Aceitar", true, Aceitar), new("Recusar", false, Recusar)]),
        FaseArquivo.PedidoDeNovo => ($"Pediu pelo histórico. Se o arquivo mudou, o {UiBrand.Name} avisa e não manda.", [new("Mandar", true, Mandar), new("Recusar", false, Recusar)]),
        FaseArquivo.AguardandoDeNovo => ($"Pedindo de novo a {p.Apelido}...", SoCancelar),
        FaseArquivo.Conectando => ($"Conectando com {p.Apelido}...", SoCancelar),
        FaseArquivo.ConfirmandoRelay => ($"Pelo relay de reserva: usa {Tamanho(p.Oferta.Total)} da cota do grupo", [new("Receber mesmo assim", true, RelaySim), new("Cancelar", false, RelayNao)]),
        _ => (Andamento(p), SoCancelar),
    };

    /// <summary>"arquivo 2 de 3 · 62% · 18,4 MB/s · faltam 2 min" (ou "conferindo 40%" na conferência do fim).</summary>
    static string Andamento(PedidoEmCurso p)
    {
        int n = p.Oferta.Arquivos.Count, pct = Porcentagem(p);
        string qual = n > 1 ? $"arquivo {p.ArquivoAtual + 1} de {n} · " : "";
        if (p.Progresso is { Fase: "conferindo" } c) return $"{qual}conferindo {c.Pct}%";
        if (p.Progresso is not { BytesPorSegundo: > 1 } pr) return $"{qual}{pct}%";
        long falta = p.Oferta.Total - Feito(p);
        return $"{qual}{pct}% · {Tamanho((long)pr.BytesPorSegundo)}/s · {Progresso.Faltam(falta / pr.BytesPorSegundo)}";
    }

    static long Feito(PedidoEmCurso p) =>
        p.Oferta.Arquivos.Take(p.ArquivoAtual).Sum(a => a.Tamanho) + (p.Progresso is { Fase: "baixando" or "enviando" } pr ? pr.Feito : 0);

    static int Porcentagem(PedidoEmCurso p) => p.Oferta.Total <= 0 ? 0 : (int)Math.Min(100, Feito(p) * 100 / p.Oferta.Total);

    static DadosDaLinha Fim(PedidoEmCurso p, string titulo)
    {
        var (selo, tom) = Selo(p.Fase);
        bool recebeu = p.Fase == FaseArquivo.Conferido && p.Sentido == Sentido.Recebido && p.Caminhos.Count > 0;
        string apoio = p.Motivo ?? (recebeu ? $"{Tamanho(p.Oferta.Total)} · na pasta de recebidos" : Tamanho(p.Oferta.Total));
        IReadOnlyList<AcaoDaLinha> acoes = recebeu ? [new("Abrir", false, Abrir), new("Mostrar na pasta", false, Pasta)] : [];
        return new DadosDaLinha(Letra(p.Apelido), Cor(p.AmigoId), titulo, apoio, selo, tom, null, acoes);
    }

    static (string, TomDoSelo) Selo(FaseArquivo fase) => fase switch
    {
        FaseArquivo.Conferido => ("Conferido ✓", TomDoSelo.Ok),
        FaseArquivo.Recusado => ("Recusado", TomDoSelo.Neutro),
        FaseArquivo.Expirou => ("Expirou", TomDoSelo.Neutro),
        FaseArquivo.Cancelado => ("Cancelado", TomDoSelo.Neutro),
        FaseArquivo.PrecisaAtualizar => ("Precisa atualizar", TomDoSelo.Atencao),
        FaseArquivo.Bloqueado => ("Grande demais", TomDoSelo.Atencao),
        FaseArquivo.Parou => ("Parou", TomDoSelo.Atencao),
        _ => ("Falhou", TomDoSelo.Atencao),
    };

    // ------------------------------------------------------------------ card do Início

    /// <summary>O card do pedido que espera por mim; nulo se o pedido não espera nada.</summary>
    public static CardDoPedido? Card(PedidoEmCurso p, string pasta, DateTime agora)
    {
        if (!EsperaPorMim(p)) return null;
        var arquivos = p.Oferta.Arquivos.Select(a => (NomeSeguro.ParaMostrar(a.Nome), Tamanho(a.Tamanho))).ToList();
        int n = p.Oferta.Arquivos.Count;
        return p.Fase switch
        {
            FaseArquivo.ConfirmandoRelay => new CardDoPedido(p.Pedido, Letra(p.Apelido), Cor(p.AmigoId), true,
                $"A conexão com {p.Apelido} passa pelo relay de reserva",
                $"A ligação direta não funcionou. {(n == 1 ? "O arquivo vai" : "Os arquivos vão")} usar {Tamanho(p.Oferta.Total)} da cota do grupo e podem chegar mais devagar. Nada foi enviado ainda.",
                [], null, [new("Receber mesmo assim", true, RelaySim), new("Cancelar", false, RelayNao)]),
            FaseArquivo.PedidoDeNovo => new CardDoPedido(p.Pedido, Letra(p.Apelido), Cor(p.AmigoId), false,
                $"{p.Apelido} quer {Nomes(p.Oferta)} de novo", $"Pediu pelo histórico. Se o arquivo mudou ou saiu do lugar, o {UiBrand.Name} avisa e não manda.",
                arquivos, null, [new("Mandar", true, Mandar), new("Recusar", false, Recusar)]),
            _ => new CardDoPedido(p.Pedido, Letra(p.Apelido), Cor(p.AmigoId), false,
                $"{p.Apelido} quer te mandar {(n == 1 ? NomeSeguro.ParaMostrar(p.Oferta.Arquivos[0].Nome) : $"{n} arquivos")}",
                $"{Tamanho(p.Oferta.Total)}{(n > 1 ? " no total" : "")} · expira em {MinutosAte(p, p.Criado, agora)} min",
                arquivos, pasta, [new("Aceitar", true, Aceitar), new("Recusar", false, Recusar)]),
        };
    }

    /// <summary>O texto do balão da bandeja quando um pedido novo chega (D7: o balão some com o silêncio).</summary>
    public static string Aviso(PedidoEmCurso p) => p.Fase switch
    {
        FaseArquivo.ConfirmandoRelay => $"A conexão com {p.Apelido} passa pelo relay. Confirme no {UiBrand.Name} antes de receber.",
        FaseArquivo.PedidoDeNovo => $"{p.Apelido} quer {Nomes(p.Oferta)} de novo.",
        _ => $"{p.Apelido} quer te mandar {Nomes(p.Oferta)} ({Tamanho(p.Oferta.Total)}).",
    };

    // ------------------------------------------------------------------ histórico

    public static DadosDaLinha LinhaDoHistorico(ItemHistorico i, DateTime agora)
    {
        string apoio = $"{(i.Sentido == Sentido.Recebido ? "De" : "Para")} {i.Apelido} · {Quando(i.Quando, agora)} · {Tamanho(i.Tamanho)}";
        var (selo, tom) = i.Resultado switch
        {
            Resultado.Conferido => ("Conferido ✓", TomDoSelo.Ok),
            Resultado.Parou => ("Parou", TomDoSelo.Atencao),
            Resultado.Falhou => ("Falhou", TomDoSelo.Atencao),
            Resultado.Recusado => ("Recusado", TomDoSelo.Neutro),
            Resultado.Expirou => ("Expirou", TomDoSelo.Neutro),
            _ => ("Cancelado", TomDoSelo.Neutro),
        };
        // recebido e gravado: o nome do arquivo na pasta (com a extensão real); senão, o anunciado, sem invisíveis
        string nome = i.Sentido == Sentido.Recebido && i.Caminho != null ? Path.GetFileName(i.Caminho) : i.Nome;
        return new DadosDaLinha(Letra(i.Apelido), Cor(i.AmigoId), NomeSeguro.ParaMostrar(nome), apoio, selo, tom, null, AcoesDoHistorico(i));
    }

    static IReadOnlyList<AcaoDaLinha> AcoesDoHistorico(ItemHistorico i)
    {
        if (i.Sentido == Sentido.Enviado) return i.Caminho != null ? [new("Mostrar na pasta", false, Pasta)] : [];
        var acoes = new List<AcaoDaLinha>();
        if (i.Resultado == Resultado.Conferido && i.Caminho != null) { acoes.Add(new("Abrir", false, Abrir)); acoes.Add(new("Mostrar na pasta", false, Pasta)); }
        acoes.Add(new("Pedir de novo", false, DeNovo));
        return acoes;
    }

    /// <summary>"hoje, 14:32", "ontem, 23:40" ou "15/11, 21:10".</summary>
    public static string Quando(DateTime quando, DateTime agora) =>
        quando.Date == agora.Date ? $"hoje, {quando:HH:mm}" : quando.Date == agora.Date.AddDays(-1) ? $"ontem, {quando:HH:mm}" : $"{quando:dd/MM, HH:mm}";
}
